"""The agent WebSocket: authentication, then one JSON message per frame.

Agent -> backend                         Backend -> agent
  hello        {version, capabilities,     welcome        {deviceId, heartbeatIntervalSeconds}
                agentState, activeJobId}   heartbeat.ack  {serverTime}
  heartbeat    {deviceId, version,         job.assign     {jobId, jobType, payload, attempt}
                status, capabilities}      job.start      {jobId, jobType, payload, attempt}
  job.accepted {jobId}                     job.revoke     {jobId}
  job.rejected {jobId, reason}             ack            {jobId, event}
  job.started  {jobId}                     error          {code, message}
  job.succeeded{jobId, result}
  job.failed   {jobId, error}

Every job.* message is answered with `ack` once it is stored, so the agent can drop it from its
outbox; a repeat of something already stored is acknowledged again and otherwise ignored.
"""

from __future__ import annotations

import json
import logging
import uuid
from datetime import UTC
from typing import Any

from fastapi import APIRouter, WebSocket
from sqlalchemy import select

from .config import Settings
from .connections import AgentConnection, ConnectionRegistry
from .devices import authenticate_device
from .dispatch import Dispatcher, assignment_message
from .jobs import Decision, agent_accepted, agent_finished, agent_rejected, agent_started
from .models import Device, Job
from .notifications import class_warning, job_failed
from .observability import emit
from .security import bearer_token
from .validation import clean_capabilities

router = APIRouter()

CLOSE_UNAUTHORIZED = 4401
CLOSE_TOO_BIG = 1009
JOB_EVENTS = ("job.accepted", "job.rejected", "job.started", "job.succeeded", "job.failed")


class AgentSession:
    """Handles the messages of one connected device."""

    def __init__(self, app_state: Any, connection: AgentConnection) -> None:
        self.state = app_state
        self.settings: Settings = app_state.settings
        self.connection = connection
        self.device_id = connection.device_id

    @property
    def dispatcher(self) -> Dispatcher:
        return self.state.dispatcher

    def now(self):  # noqa: ANN201
        return self.state.clock()

    async def on_connected(self) -> None:
        now = self.now()
        async with self.state.sessionmaker() as session, session.begin():
            device = await session.get(Device, self.device_id, with_for_update=True)
            if device is not None:
                device.status = "online"
                device.connected_at = now
                device.last_heartbeat = now
                device.updated_at = now
        await self.connection.send(
            {
                "type": "welcome",
                "deviceId": str(self.device_id),
                "heartbeatIntervalSeconds": self.settings.heartbeat_interval_seconds,
            }
        )

    async def handle(self, message: Any) -> None:
        kind = message.get("type") if isinstance(message, dict) else None
        if kind == "hello":
            await self._hello(message)
        elif kind == "heartbeat":
            await self._heartbeat(message)
        elif kind in JOB_EVENTS:
            await self._job_event(kind, message)
        else:
            await self.connection.send({"type": "error", "code": "unknownMessage", "message": "Unknown message type."})

    async def _touch(self, message: dict[str, Any]) -> str | None:
        """Update what the device says about itself; returns its state (idle/busy)."""
        now = self.now()
        state = message.get("status", message.get("agentState"))
        state = state if state in ("idle", "busy") else None
        async with self.state.sessionmaker() as session, session.begin():
            device = await session.get(Device, self.device_id, with_for_update=True)
            if device is None:
                return state
            device.last_heartbeat = now
            device.status = "online"
            device.agent_state = state
            if isinstance(message.get("version"), str) and message["version"].strip():
                device.version = message["version"].strip()[:50]
            if "capabilities" in message:
                device.capabilities = clean_capabilities(message.get("capabilities"))
            device.updated_at = now
        return state

    async def _hello(self, message: dict[str, Any]) -> None:
        await self._touch(message)
        # Anything still in flight for this device is offered again: the agent recognises what it
        # already has (by job id) and neither accepts nor runs anything twice.
        async with self.state.sessionmaker() as session:
            pending = (
                await session.execute(
                    select(Job).where(Job.device_id == self.device_id, Job.status == "assigned").order_by(Job.assigned_at)
                )
            ).scalars().all()
        for job in pending:
            await self.connection.send(assignment_message(job, "job.start" if job.accepted_at else "job.assign"))
        emit("agent.hello", deviceId=str(self.device_id), resent=len(pending),
             activeJobId=message.get("activeJobId") if isinstance(message.get("activeJobId"), str) else None)
        await self.dispatcher.dispatch()

    async def _heartbeat(self, message: dict[str, Any]) -> None:
        state = await self._touch(message)
        emit("agent.heartbeat", level=logging.DEBUG, deviceId=str(self.device_id), agentState=state)
        await self.connection.send({"type": "heartbeat.ack", "serverTime": self.now().astimezone(UTC).isoformat()})
        if state == "idle":
            await self.dispatcher.dispatch()

    async def _job_event(self, kind: str, message: dict[str, Any]) -> None:
        try:
            job_id = uuid.UUID(str(message.get("jobId")))
        except ValueError:
            await self.connection.send({"type": "error", "code": "badJobId", "message": "jobId is not a job id."})
            return

        now = self.now()
        follow_up: dict[str, Any] | None = None
        outcome = "stored"
        async with self.state.sessionmaker() as session, session.begin():
            if kind == "job.accepted":
                decision, job = await agent_accepted(session, self.device_id, job_id, now)
                if decision is Decision.START and job is not None:
                    follow_up = assignment_message(job, "job.start")
                elif decision is Decision.REVOKE:
                    follow_up = {"type": "job.revoke", "jobId": str(job_id)}
                    outcome = "revoked"
            elif kind == "job.rejected":
                reason = message.get("reason") if isinstance(message.get("reason"), str) else "unspecified"
                outcome = "requeued" if await agent_rejected(session, self.device_id, job_id, reason, now, self.settings) else "ignored"
            elif kind == "job.started":
                outcome = "stored" if await agent_started(session, self.device_id, job_id, now) else "ignored"
            else:
                succeeded = kind == "job.succeeded"
                outcome = await agent_finished(
                    session,
                    self.device_id,
                    job_id,
                    succeeded=succeeded,
                    body=message.get("result") if succeeded else message.get("error"),
                    now=now,
                    settings=self.settings,
                )
                # Somebody is told what a page would otherwise have to be opened to notice: a step
                # that will not be tried again, and a step that finished leaving something behind
                # (a meeting nobody could close). Sent beside this, never in its way.
                if outcome in ("failed", "succeeded"):
                    finished = await session.get(Job, job_id)
                    if finished is not None and outcome == "failed":
                        job_failed(self.state, finished)
                    elif finished is not None and isinstance(finished.result, dict):
                        warning = finished.result.get("warning")
                        if isinstance(warning, str) and warning.strip():
                            class_warning(self.state, finished, warning.strip())

        if outcome == "ignored":
            emit("agent.job_event_ignored", deviceId=str(self.device_id), jobId=str(job_id), messageType=kind)
        await self.connection.send({"type": "ack", "jobId": str(job_id), "event": kind})
        if follow_up is not None:
            await self.connection.send(follow_up)
        if kind in ("job.rejected", "job.succeeded", "job.failed"):
            await self.dispatcher.dispatch()


@router.websocket("/ws/agent")
async def agent_socket(websocket: WebSocket) -> None:
    state = websocket.app.state
    registry: ConnectionRegistry = state.registry
    token = bearer_token(websocket.headers.get("authorization"))
    async with state.sessionmaker() as session:
        device = await authenticate_device(session, token)
    if device is None:
        emit("agent.refused", level=logging.WARNING, reason="invalid or missing device token",
             client=websocket.client.host if websocket.client else None)
        await websocket.close(code=CLOSE_UNAUTHORIZED)  # before accept: the handshake fails (HTTP 403)
        return

    await websocket.accept()
    connection = AgentConnection(device.id, websocket)
    await registry.register(connection)
    agent = AgentSession(state, connection)
    emit("agent.connected", deviceId=str(device.id), name=device.name,
         client=websocket.client.host if websocket.client else None)
    reason = "closed"
    try:
        await agent.on_connected()
        while True:
            frame = await websocket.receive()
            if frame["type"] == "websocket.disconnect":
                reason = f"code {frame.get('code', 1000)}"
                break
            text = frame.get("text")
            if text is None:
                await connection.send({"type": "error", "code": "textOnly", "message": "Send JSON as text frames."})
                continue
            if len(text.encode("utf-8")) > state.settings.max_agent_message_bytes:
                reason = "message too large"
                await connection.close(CLOSE_TOO_BIG, "message too large")
                break
            try:
                message = json.loads(text)
            except ValueError:
                await connection.send({"type": "error", "code": "badJson", "message": "The message is not JSON."})
                continue
            await agent.handle(message)
    except Exception as exc:  # noqa: BLE001 - a broken connection must not take the server down
        reason = type(exc).__name__
        emit("agent.error", level=logging.ERROR, deviceId=str(device.id), error=reason)
        # Close rather than leave the agent waiting: it reconnects and is offered its work again.
        await connection.close(1011, "internal error")
    finally:
        registry.unregister(connection)
        emit("agent.disconnected", deviceId=str(device.id), reason=reason)

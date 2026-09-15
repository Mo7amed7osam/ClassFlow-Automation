"""Handing queued jobs to devices (Dispatcher) and cleaning up after devices that went quiet (Sweeper)."""

from __future__ import annotations

import asyncio
import logging
import uuid
from collections.abc import Callable
from datetime import datetime, timedelta
from typing import Any

from sqlalchemy import and_, or_, select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from .config import Settings
from .connections import ConnectionRegistry
from .jobs import add_event, requeue
from .recording_jobs import record_job_outcome
from .models import ACTIVE_JOB_STATUSES, Device, Job
from .observability import emit
from .validation import JOB_TYPES

Clock = Callable[[], datetime]


def assignment_message(job: Job, message_type: str = "job.assign") -> dict[str, Any]:
    return {
        "type": message_type,
        "jobId": str(job.id),
        "jobType": job.type,
        "payload": job.payload,
        "attempt": job.attempts + 1,
    }


async def pick_device(
    session: AsyncSession, capability: str, connected: set[uuid.UUID], now: datetime, settings: Settings
) -> Device | None:
    """Online (connected, fresh heartbeat, not revoked), capable, and without an assigned or running
    job; of those, the one assigned least recently (never-assigned first), then by name."""
    busy = select(Job.device_id).where(Job.status.in_(ACTIVE_JOB_STATUSES), Job.device_id.is_not(None))
    statement = (
        select(Device)
        .where(
            Device.id.in_(list(connected)),
            Device.revoked_at.is_(None),
            Device.last_heartbeat >= now - timedelta(seconds=settings.device_stale_seconds),
            Device.capabilities.contains([capability]),
            Device.id.not_in(busy),
            or_(Device.agent_state.is_(None), Device.agent_state != "busy"),
        )
        .order_by(Device.last_assigned_at.asc().nulls_first(), Device.name, Device.id)
        .limit(1)
        .with_for_update(skip_locked=True)
    )
    return (await session.execute(statement)).scalar_one_or_none()


class Dispatcher:
    def __init__(
        self,
        sessionmaker: async_sessionmaker[AsyncSession],
        registry: ConnectionRegistry,
        settings: Settings,
        clock: Clock,
    ) -> None:
        self._sessionmaker = sessionmaker
        self._registry = registry
        self._settings = settings
        self._clock = clock
        self._lock = asyncio.Lock()

    async def dispatch(self) -> int:
        """Assign every job that can be assigned now. One at a time, so two callers never pick the
        same device; the database row locks make it safe even so."""
        assigned = 0
        async with self._lock:
            while True:
                picked = await self._assign_one()
                if picked is None:
                    return assigned
                job_id, device_id, message = picked
                if await self._registry.send(device_id, message):
                    emit("job.assigned", jobId=str(job_id), deviceId=str(device_id), attempt=message["attempt"])
                    assigned += 1
                else:
                    await self._undo(job_id, device_id)

    async def _assign_one(self) -> tuple[uuid.UUID, uuid.UUID, dict[str, Any]] | None:
        connected = self._registry.connected_device_ids()
        if not connected:
            return None
        now = self._clock()
        async with self._sessionmaker() as session, session.begin():
            candidates = (
                await session.execute(
                    select(Job)
                    .where(Job.status == "queued", Job.available_at <= now)
                    .order_by(Job.created_at, Job.id)
                    .limit(20)
                    .with_for_update(skip_locked=True)
                )
            ).scalars().all()
            for job in candidates:
                capability = JOB_TYPES.get(job.type)
                if capability is None:
                    continue
                device = await pick_device(session, capability, connected, now, self._settings)
                if device is None:
                    continue
                job.status = "assigned"
                job.device_id = device.id
                job.assigned_at = now
                job.updated_at = now
                device.last_assigned_at = now
                add_event(session, job.id, "assigned", now, device.id)
                return job.id, device.id, assignment_message(job)
        return None

    async def _undo(self, job_id: uuid.UUID, device_id: uuid.UUID) -> None:
        """The device's connection died between choosing it and sending: put the job back."""
        now = self._clock()
        async with self._sessionmaker() as session, session.begin():
            job = await session.get(Job, job_id, with_for_update=True)
            if job is not None and job.status == "assigned" and job.device_id == device_id and job.accepted_at is None:
                add_event(session, job.id, "assignment_undelivered", now, device_id)
                requeue(job, now, 0, "assignment could not be delivered")


class Sweeper:
    """Runs every few seconds: marks silent devices offline, takes back assignments nobody took,
    fails jobs whose device vanished mid-run, then dispatches."""

    def __init__(
        self,
        sessionmaker: async_sessionmaker[AsyncSession],
        registry: ConnectionRegistry,
        dispatcher: Dispatcher,
        settings: Settings,
        clock: Clock,
    ) -> None:
        self._sessionmaker = sessionmaker
        self._registry = registry
        self._dispatcher = dispatcher
        self._settings = settings
        self._clock = clock

    async def sweep_once(self) -> dict[str, int]:
        now = self._clock()
        s = self._settings
        stale_before = now - timedelta(seconds=s.device_stale_seconds)
        orphan_before = now - timedelta(seconds=s.running_orphan_timeout_seconds)
        counts = {"offline": 0, "requeued": 0, "lost": 0}
        silent: list[uuid.UUID] = []

        async with self._sessionmaker() as session, session.begin():
            for device in (
                await session.execute(
                    select(Device)
                    .where(
                        Device.status == "online",
                        or_(Device.last_heartbeat.is_(None), Device.last_heartbeat < stale_before),
                    )
                    .with_for_update(skip_locked=True)
                )
            ).scalars():
                device.status = "offline"
                device.updated_at = now
                silent.append(device.id)
                counts["offline"] += 1
                emit("device.offline", level=logging.WARNING, deviceId=str(device.id),
                     lastHeartbeat=device.last_heartbeat.isoformat() if device.last_heartbeat else None)

            # Assigned but never accepted: the assignment expired, or its device went silent.
            unaccepted = (
                await session.execute(
                    select(Job, Device)
                    .join(Device, Device.id == Job.device_id)
                    .where(
                        Job.status == "assigned",
                        Job.accepted_at.is_(None),
                        or_(
                            Job.assigned_at < now - timedelta(seconds=s.assignment_ack_timeout_seconds),
                            Device.last_heartbeat.is_(None),
                            Device.last_heartbeat < stale_before,
                        ),
                    )
                    .with_for_update(of=Job, skip_locked=True)
                )
            ).all()
            for job, device in unaccepted:
                add_event(session, job.id, "assignment_expired", now, device.id)
                requeue(job, now, 0, "assignment was not accepted in time")
                counts["requeued"] += 1

            # Accepted or running on a device that has been gone for the orphan timeout: it may
            # have finished or not, so the job is failed rather than silently run again.
            orphaned = (
                await session.execute(
                    select(Job, Device)
                    .join(Device, Device.id == Job.device_id)
                    .where(
                        or_(Job.status == "running", and_(Job.status == "assigned", Job.accepted_at.is_not(None))),
                        or_(Device.last_heartbeat.is_(None), Device.last_heartbeat < orphan_before),
                    )
                    .with_for_update(of=Job, skip_locked=True)
                )
            ).all()
            for job, device in orphaned:
                job.status = "failed"
                job.finished_at = now
                job.updated_at = now
                job.error = {
                    "code": "agentLost",
                    "message": "The device running this job stopped reporting before it finished. "
                    "Check the LMS before retrying; a repeat with replaceExisting=false is safe.",
                    "retryable": False,
                }
                add_event(session, job.id, "agent_lost", now, device.id)
                emit("job.failed", level=logging.WARNING, jobId=str(job.id), deviceId=str(device.id), code="agentLost")
                await record_job_outcome(session, job, now)
                counts["lost"] += 1

        for device_id in silent:
            if self._registry.get(device_id) is not None:
                await self._registry.close(device_id, 4002, "heartbeat timeout")
        await self._dispatcher.dispatch()
        return counts

    async def run(self, stop: asyncio.Event) -> None:
        while not stop.is_set():
            try:
                await self.sweep_once()
            except Exception as exc:  # noqa: BLE001 - the loop must survive a database hiccup
                emit("sweeper.error", level=logging.ERROR, error=type(exc).__name__)
            try:
                await asyncio.wait_for(stop.wait(), timeout=self._settings.sweep_interval_seconds)
            except TimeoutError:
                pass

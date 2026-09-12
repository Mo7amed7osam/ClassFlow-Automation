"""REST endpoints.

API clients (n8n), X-API-Key or Authorization: Bearer <client key>:
    POST /api/v1/jobs                  create a job (202), or return the same job for a repeated Idempotency-Key (200)
    GET  /api/v1/jobs/{jobId}          status and result
    POST /api/v1/jobs/{jobId}/cancel   cancel a job that is still queued
    GET  /api/v1/devices               the devices and whether they are online

Agents, with a single-use enrollment token (not a client key):
    POST /api/v1/agents/register       issue this installation's device token

No key:
    GET  /health                       {"status": "ok"} when the database answers
"""

from __future__ import annotations

import logging
import re
import uuid
from datetime import UTC
from typing import Any

from fastapi import APIRouter, Depends, Header, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import select, text

from .devices import RegistrationRefused, device_view, register_device
from .jobs import IdempotencyConflict, cancel_job, create_job, job_view
from .models import Device, Job
from .observability import emit
from .security import bearer_token, matches_any
from .validation import PayloadError, validate_payload

router = APIRouter()

_IDEMPOTENCY_KEY = re.compile(r"^[\x21-\x7e]{1,200}$")


class ApiError(Exception):
    def __init__(self, status: int, error: str, details: Any = None) -> None:
        self.status = status
        self.error = error
        self.details = details


def error_response(status: int, error: str, details: Any = None) -> JSONResponse:
    body: dict[str, Any] = {"error": error}
    if details is not None:
        body["details"] = details
    return JSONResponse(body, status_code=status, headers={"Cache-Control": "no-store"})


def require_client(request: Request) -> None:
    """The API-client domain. A device token or an enrollment token is not a client key and fails here."""
    presented = request.headers.get("x-api-key") or bearer_token(request.headers.get("authorization"))
    if not matches_any(presented, request.app.state.settings.client_api_key_hashes):
        emit("client.refused", level=logging.WARNING, path=request.url.path,
             client=request.client.host if request.client else None)
        raise ApiError(401, "Unauthorized")


def _uuid(value: str) -> uuid.UUID:
    try:
        return uuid.UUID(value)
    except ValueError as exc:
        raise ApiError(404, "Not found") from exc


class JobCreate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    type: str = Field(max_length=64)
    payload: dict[str, Any]


class AgentRegistration(BaseModel):
    model_config = ConfigDict(extra="forbid")
    enrollmentToken: str = Field(min_length=10, max_length=200)
    installationId: uuid.UUID
    name: str = Field(min_length=1, max_length=100)
    version: str = Field(min_length=1, max_length=50)
    capabilities: list[str] = Field(default_factory=list, max_length=20)


@router.get("/health")
async def health(request: Request) -> JSONResponse:
    try:
        async with request.app.state.sessionmaker() as session:
            await session.execute(text("SELECT 1"))
    except Exception:  # noqa: BLE001
        return error_response(503, "Database unavailable")
    return JSONResponse({"status": "ok"}, headers={"Cache-Control": "no-store"})


@router.post("/api/v1/jobs", dependencies=[Depends(require_client)])
async def post_job(
    body: JobCreate,
    request: Request,
    idempotency_key: str | None = Header(default=None, alias="Idempotency-Key"),
) -> JSONResponse:
    if idempotency_key is not None and not _IDEMPOTENCY_KEY.match(idempotency_key):
        raise ApiError(400, "Invalid request", "Idempotency-Key must be 1-200 visible ASCII characters.")
    try:
        payload = validate_payload(body.type, body.payload)
    except PayloadError as exc:
        raise ApiError(400, "Invalid request", str(exc)) from exc

    state = request.app.state
    try:
        async with state.sessionmaker() as session, session.begin():
            job, created = await create_job(
                session,
                job_type=body.type,
                payload=payload,
                idempotency_key=idempotency_key,
                now=state.clock(),
                max_attempts=state.settings.default_max_attempts,
            )
            response = {"jobId": str(job.id), "status": job.status}
    except IdempotencyConflict as exc:
        raise ApiError(409, "Conflict", "This Idempotency-Key was already used for a different job.") from exc

    if created:
        await state.dispatcher.dispatch()
        return JSONResponse(response, status_code=202, headers={"Cache-Control": "no-store"})
    return JSONResponse({**response, "duplicate": True}, status_code=200, headers={"Cache-Control": "no-store"})


@router.get("/api/v1/jobs/{job_id}", dependencies=[Depends(require_client)])
async def get_job(job_id: str, request: Request) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        job = await session.get(Job, _uuid(job_id))
    if job is None:
        raise ApiError(404, "Not found")
    return JSONResponse(_json(job_view(job)), headers={"Cache-Control": "no-store"})


@router.post("/api/v1/jobs/{job_id}/cancel", dependencies=[Depends(require_client)])
async def post_cancel(job_id: str, request: Request) -> JSONResponse:
    state = request.app.state
    async with state.sessionmaker() as session, session.begin():
        job = await cancel_job(session, _uuid(job_id), state.clock())
        view = job_view(job) if job is not None else None
    if view is None:
        raise ApiError(404, "Not found")
    if view["status"] != "cancelled":
        raise ApiError(409, "Conflict", f"Only a queued job can be cancelled; this one is {view['status']}.")
    return JSONResponse(_json(view), headers={"Cache-Control": "no-store"})


@router.get("/api/v1/devices", dependencies=[Depends(require_client)])
async def get_devices(request: Request) -> JSONResponse:
    state = request.app.state
    connected = state.registry.connected_device_ids()
    now = state.clock()
    async with state.sessionmaker() as session:
        devices = (await session.execute(select(Device).order_by(Device.name, Device.id))).scalars().all()
    return JSONResponse(
        _json({"devices": [device_view(d, connected, now, state.settings) for d in devices]}),
        headers={"Cache-Control": "no-store"},
    )


@router.post("/api/v1/agents/register")
async def post_register(body: AgentRegistration, request: Request) -> JSONResponse:
    state = request.app.state
    try:
        async with state.sessionmaker() as session, session.begin():
            device, token = await register_device(
                session,
                enrollment_token=body.enrollmentToken,
                installation_id=body.installationId,
                name=body.name,
                version=body.version,
                capabilities=body.capabilities,
                now=state.clock(),
            )
            device_id = str(device.id)
    except RegistrationRefused as exc:
        emit("agent.registration_refused", level=logging.WARNING,
             installationId=str(body.installationId), client=request.client.host if request.client else None)
        raise ApiError(401, "Unauthorized", "The enrollment token is unknown, already used or expired.") from exc
    return JSONResponse(
        {
            "deviceId": device_id,
            "deviceToken": token,
            "heartbeatIntervalSeconds": state.settings.heartbeat_interval_seconds,
        },
        status_code=201,
        headers={"Cache-Control": "no-store"},
    )


def _json(value: Any) -> Any:
    """Datetimes as ISO-8601 UTC strings."""
    if isinstance(value, dict):
        return {k: _json(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_json(v) for v in value]
    if hasattr(value, "astimezone") and hasattr(value, "isoformat"):
        return value.astimezone(UTC).isoformat().replace("+00:00", "Z")
    return value

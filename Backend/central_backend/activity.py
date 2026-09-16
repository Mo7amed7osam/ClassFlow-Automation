"""What each PC did by itself, reported to the server.

Every PC runs its classes on its own - it opens the meeting, records the attendance and does the
LMS steps without asking anyone - and writes down what it did as it happens. Its agent sends those
notes here whenever the server answers, so a server that was off loses nothing and no PC waits for
it. The notes are what happened, not instructions: nothing here changes a class.

    POST /api/v1/devices/activity      the agent's own notes (Bearer zaad_...); re-sending is safe
    GET  /api/v1/dashboard/activity    what the PCs have done, newest first (scoped to the viewer's
                                       groups; a coordinator sees only their own classes)
"""

from __future__ import annotations

# "date" is also a field name below, and a field with a default would shadow the type.
from datetime import date as DateOnly
from datetime import datetime
from typing import Any, Literal

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, field_validator
from sqlalchemy import select
from sqlalchemy.dialects.postgresql import insert

from .access import Viewer, current_viewer
from .api import ApiError, _json
from .attendance import group_filter, ingest_principal
from .models import Device, DeviceActivity
from .observability import emit

router = APIRouter()
dashboard_router = APIRouter(dependencies=[Depends(current_viewer)])

MAXIMUM_EVENTS = 200


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


class ActivityIn(BaseModel):
    """One thing a PC did. Unknown fields are refused, so a typo is never stored silently."""

    model_config = ConfigDict(extra="forbid")
    eventId: str = Field(min_length=1, max_length=200)
    at: datetime
    kind: str = Field(min_length=1, max_length=40)
    outcome: Literal["done", "failed", "skipped"]
    group: str | None = Field(default=None, max_length=100)
    date: DateOnly | None = None
    summary: str = Field(default="", max_length=500)
    detail: dict[str, Any] | None = None

    @field_validator("at")
    @classmethod
    def _aware(cls, value: datetime) -> datetime:
        if value.tzinfo is None:
            raise ValueError("at must carry a time zone offset")
        return value


class ActivityBatch(BaseModel):
    model_config = ConfigDict(extra="forbid")
    events: list[ActivityIn] = Field(min_length=1, max_length=MAXIMUM_EVENTS)


@router.post("/api/v1/devices/activity")
async def post_activity(body: ActivityBatch, request: Request) -> JSONResponse:
    device = await ingest_principal(request)
    if device is None:
        # Only a PC reports its own work; the n8n key speaks for no device.
        raise ApiError(403, "Forbidden", "Activity is reported with a device token.")
    state = request.app.state
    now = state.clock()
    rows = [
        {
            "device_id": device.id,
            "client_event_id": event.eventId,
            "happened_at": event.at,
            "kind": event.kind,
            "outcome": event.outcome,
            "group_name": event.group,
            "session_date": event.date,
            "summary": event.summary,
            "detail": event.detail,
            "created_at": now,
        }
        for event in body.events
    ]
    async with state.sessionmaker() as session, session.begin():
        # A device that was away re-sends what it already sent; the same event is kept once.
        statement = insert(DeviceActivity).values(rows).on_conflict_do_nothing(index_elements=[DeviceActivity.client_event_id])
        await session.execute(statement)
    emit("device.activity", deviceId=str(device.id), events=len(rows))
    return _no_store({"accepted": len(rows)})


def _view(row: DeviceActivity, devices: dict[Any, Device]) -> dict[str, Any]:
    device = devices.get(row.device_id)
    return {
        "id": row.id,
        "device": device.name if device else None,
        "deviceId": str(row.device_id),
        "at": row.happened_at,
        "kind": row.kind,
        "outcome": row.outcome,
        "group": row.group_name,
        "date": row.session_date.isoformat() if row.session_date else None,
        "summary": row.summary,
        "detail": row.detail,
    }


@dashboard_router.get("/api/v1/dashboard/activity")
async def activity(request: Request, viewer: Viewer = Depends(current_viewer),
                   group: str | None = Query(default=None, max_length=100),
                   limit: int = Query(default=100, ge=1, le=500)) -> JSONResponse:
    state = request.app.state
    query = select(DeviceActivity).where(group_filter(viewer, DeviceActivity.group_name))
    if group:
        query = query.where(DeviceActivity.group_name == group)
    query = query.order_by(DeviceActivity.happened_at.desc(), DeviceActivity.id.desc()).limit(limit)
    async with state.sessionmaker() as session:
        rows = (await session.execute(query)).scalars().all()
        found = {row.device_id for row in rows}
        devices = {
            device.id: device
            for device in (await session.execute(select(Device).where(Device.id.in_(found)))).scalars().all()
        } if found else {}
    return _no_store({"items": [_view(row, devices) for row in rows]})

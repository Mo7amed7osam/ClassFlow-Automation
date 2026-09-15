"""Recording management: the recordings n8n reports from the recordings sheet, one row per session.

API clients (X-API-Key, the same keys as /api/v1/jobs):
    GET  /api/v1/recordings              all recordings; filters ?group= &date= &status= (+ limit/offset)
    GET  /api/v1/recordings/latest       the most recently updated recordings (?limit=, default 20, max 100)
    GET  /api/v1/recordings/{id}         one recording
    POST /api/v1/recordings/sync         create, or update the one with the same group + date + startTime
    PATCH /api/v1/recordings/{id}        edit the fields sent (a new link puts lmsStatus back to pending)
    GET  /api/v1/groups                  the distinct group names that have recordings

This module only records metadata. It does not create jobs or touch the LMS; lms_status stays
"pending" until a later step moves it on.
"""

from __future__ import annotations

import logging
import re
import uuid
from datetime import date, datetime
from typing import Any
from urllib.parse import urlsplit

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import func, select
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import AsyncSession

from .api import ApiError, _json, require_client
from .groups import ensure_group
from .models import Recording
from .observability import emit, link_preview
from .validation import PayloadError, is_drive_file_link

router = APIRouter()

_GROUP = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$")
_TIME = re.compile(r"^(?:[01]\d|2[0-3]):[0-5]\d$")
_STATUS = re.compile(r"^[a-z][a-z_]{0,31}$")
_ZOOM_RECORDING_PATH = re.compile(r"^/rec/(share|play)/[A-Za-z0-9._\-]+")


class RecordingSync(BaseModel):
    """The sync body, in the sheet's words. Unknown fields are refused (400)."""

    model_config = ConfigDict(extra="forbid")
    group: str | None = Field(default=None, max_length=200)
    date: str | None = Field(default=None, max_length=20)
    startTime: str | None = Field(default=None, max_length=20)
    fileName: str | None = Field(default=None, max_length=255)
    type: str | None = Field(default=None, max_length=50)
    link: str | None = Field(default=None, max_length=2048)


def is_zoom_recording_link(url: str) -> bool:
    """https://<anything>.zoom.us/rec/share/... or /rec/play/..., nothing else."""
    if any(c.isspace() or ord(c) < 32 for c in url):
        return False
    try:
        parts = urlsplit(url)
        port = parts.port
    except ValueError:
        return False
    host = (parts.hostname or "").lower()
    return (
        parts.scheme == "https"
        and port is None
        and parts.username is None
        and (host == "zoom.us" or host.endswith(".zoom.us"))
        and bool(_ZOOM_RECORDING_PATH.match(parts.path))
    )


def _optional(value: str | None) -> str | None:
    """An empty cell arrives as "" from a sheet; for optional fields that means "not given"."""
    if value is None:
        return None
    value = value.strip()
    return value or None


def validate_sync(body: RecordingSync) -> dict[str, Any]:
    """The body as column values, or PayloadError with a message safe to return and log."""
    group = (body.group or "").strip()
    if not group:
        raise PayloadError("'group' is required.")
    if not _GROUP.match(group):
        raise PayloadError("'group' may hold letters, digits, spaces and _ - . (at most 100 characters).")

    day_text = (body.date or "").strip()
    if not day_text:
        raise PayloadError("'date' is required (yyyy-MM-dd).")
    try:
        if len(day_text) != 10:
            raise ValueError
        day = date.fromisoformat(day_text)
    except ValueError as exc:
        raise PayloadError("'date' must be yyyy-MM-dd.") from exc

    start = _optional(body.startTime)
    if start is not None and not _TIME.match(start):
        raise PayloadError("'startTime' must be HH:mm (24-hour) when given.")

    values: dict[str, Any] = {
        "group_name": group,
        "session_date": day,
        "start_time": start,
        "file_name": _optional(body.fileName),
        "record_type": _optional(body.type),
    }

    link = _optional(body.link)
    if link is not None:
        if is_drive_file_link(link):
            values["drive_link"] = link
        elif is_zoom_recording_link(link):
            values["zoom_link"] = link
        else:
            raise PayloadError(
                "'link' must be a Google Drive link to one file (https://drive.google.com/file/d/<id>/view) "
                "or a Zoom recording link (https://<host>.zoom.us/rec/share/...)."
            )
    return values


def _source(recording_drive_link: str | None) -> str:
    """A Drive link wins over a Zoom one (the Drive copy is the one kept); without one, "zoom"."""
    return "drive" if recording_drive_link else "zoom"


def _same_session(group: str, day: date, start: str | None):  # noqa: ANN202
    return (
        Recording.group_name == group,
        Recording.session_date == day,
        func.coalesce(Recording.start_time, "") == (start or ""),
    )


async def sync_recording(session: AsyncSession, values: dict[str, Any], now: datetime) -> tuple[Recording, bool]:
    """(recording, created). The same group + date + startTime updates the existing row: fields that
    are given replace what was there, fields left out keep their value. A changed link puts the
    recording back to lms_status "pending", since the LMS then holds an out-of-date link."""
    # A group n8n names for the first time is registered with its first recording.
    await ensure_group(session, values["group_name"], now)
    key = _same_session(values["group_name"], values["session_date"], values["start_time"])
    existing = (await session.execute(select(Recording).where(*key).with_for_update())).scalar_one_or_none()

    if existing is None:
        drive_link = values.get("drive_link")
        inserted = (
            await session.execute(
                insert(Recording)
                .values(
                    id=uuid.uuid4(),
                    **values,
                    source=_source(drive_link),
                    lms_status="pending",
                    created_at=now,
                    updated_at=now,
                )
                .on_conflict_do_nothing()
                .returning(Recording.id)
            )
        ).scalar_one_or_none()
        if inserted is not None:
            recording = await session.get(Recording, inserted)
            assert recording is not None
            return recording, True
        # Another request created the same session a moment ago: update that one instead.
        existing = (await session.execute(select(Recording).where(*key).with_for_update())).scalar_one()

    link_changed = False
    for column in ("file_name", "record_type", "drive_link", "zoom_link"):
        new_value = values.get(column)
        if new_value is None or new_value == getattr(existing, column):
            continue
        if column in ("drive_link", "zoom_link"):
            link_changed = True
        setattr(existing, column, new_value)
    existing.source = _source(existing.drive_link)
    if link_changed and existing.lms_status != "pending":
        existing.lms_status = "pending"
    existing.updated_at = now
    await session.flush()
    return existing, False


def recording_view(recording: Recording) -> dict[str, Any]:
    return _json(
        {
            "id": str(recording.id),
            "group": recording.group_name,
            "date": recording.session_date.isoformat(),
            "startTime": recording.start_time,
            "fileName": recording.file_name,
            "type": recording.record_type,
            "driveLink": recording.drive_link,
            "zoomLink": recording.zoom_link,
            "source": recording.source,
            "lmsStatus": recording.lms_status,
            "lmsUpdatedAt": recording.lms_updated_at,
            "createdAt": recording.created_at,
            "updatedAt": recording.updated_at,
        }
    )


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(body, status_code=status, headers={"Cache-Control": "no-store"})


# ----------------------------------------------------------------------------- endpoints


@router.get("/api/v1/recordings", dependencies=[Depends(require_client)])
async def list_recordings(
    request: Request,
    group: str | None = Query(default=None, max_length=100),
    date_filter: str | None = Query(default=None, alias="date", max_length=10),
    status: str | None = Query(default=None, max_length=32),
    limit: int = Query(default=1000, ge=1, le=5000),
    offset: int = Query(default=0, ge=0),
) -> JSONResponse:
    statement = select(Recording)
    if group is not None and group.strip():
        statement = statement.where(Recording.group_name == group.strip())
    if date_filter is not None and date_filter.strip():
        try:
            statement = statement.where(Recording.session_date == date.fromisoformat(date_filter.strip()))
        except ValueError as exc:
            raise ApiError(400, "Invalid request", "'date' must be yyyy-MM-dd.") from exc
    if status is not None and status.strip():
        if not _STATUS.match(status.strip()):
            raise ApiError(400, "Invalid request", "'status' must be a status name such as pending.")
        statement = statement.where(Recording.lms_status == status.strip())
    statement = statement.order_by(
        Recording.session_date.desc(), Recording.group_name, func.coalesce(Recording.start_time, ""), Recording.id
    ).limit(limit).offset(offset)

    async with request.app.state.sessionmaker() as session:
        rows = (await session.execute(statement)).scalars().all()
    return _no_store({"recordings": [recording_view(r) for r in rows], "count": len(rows)})


LATEST_FIELDS = ("id", "group", "date", "startTime", "fileName", "type", "driveLink", "zoomLink", "source", "lmsStatus", "updatedAt")


# Registered before /api/v1/recordings/{recording_id}: routes match in order, and "latest" would
# otherwise be taken for an id.
@router.get("/api/v1/recordings/latest", dependencies=[Depends(require_client)])
async def latest_recordings(request: Request, limit: int = Query(default=20, ge=1, le=100)) -> JSONResponse:
    """The most recently created or updated recordings, newest first."""
    statement = (
        select(Recording)
        .order_by(Recording.updated_at.desc(), Recording.created_at.desc(), Recording.id)
        .limit(limit)
    )
    async with request.app.state.sessionmaker() as session:
        rows = (await session.execute(statement)).scalars().all()
    recordings = [{field: view[field] for field in LATEST_FIELDS} for view in map(recording_view, rows)]
    return _no_store({"recordings": recordings, "count": len(recordings)})


@router.get("/api/v1/recordings/{recording_id}", dependencies=[Depends(require_client)])
async def get_recording(recording_id: str, request: Request) -> JSONResponse:
    try:
        key = uuid.UUID(recording_id)
    except ValueError as exc:
        raise ApiError(404, "Not found") from exc
    async with request.app.state.sessionmaker() as session:
        recording = await session.get(Recording, key)
    if recording is None:
        raise ApiError(404, "Not found")
    return _no_store(recording_view(recording))


@router.post("/api/v1/recordings/sync", dependencies=[Depends(require_client)])
async def post_sync(body: RecordingSync, request: Request) -> JSONResponse:
    try:
        values = validate_sync(body)
    except PayloadError as exc:
        raise ApiError(400, "Invalid request", str(exc)) from exc

    state = request.app.state
    async with state.sessionmaker() as session, session.begin():
        recording, created = await sync_recording(session, values, state.clock())
        view = recording_view(recording)
    emit(
        "recording.created" if created else "recording.updated",
        level=logging.INFO,
        recordingId=view["id"],
        group=view["group"],
        date=view["date"],
        startTime=view["startTime"],
        source=view["source"],
        link=link_preview(view["driveLink"] or view["zoomLink"]),
    )
    return _no_store({"action": "created" if created else "updated", "recording": view}, 201 if created else 200)


@router.get("/api/v1/groups", dependencies=[Depends(require_client)])
async def list_groups(request: Request) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        groups = (
            await session.execute(select(Recording.group_name).distinct().order_by(Recording.group_name))
        ).scalars().all()
    return _no_store({"groups": list(groups), "count": len(groups)})


# ----------------------------------------------------------------------------- edit one recording

LMS_STATUSES = ("pending", "attached", "failed")


class RecordingPatch(BaseModel):
    """A partial edit: only the fields sent are changed. Unknown fields are refused (400)."""

    model_config = ConfigDict(extra="forbid")
    group: str | None = Field(default=None, max_length=200)
    date: str | None = Field(default=None, max_length=20)
    startTime: str | None = Field(default=None, max_length=20)
    fileName: str | None = Field(default=None, max_length=255)
    type: str | None = Field(default=None, max_length=50)
    link: str | None = Field(default=None, max_length=2048)
    lmsStatus: str | None = Field(default=None, max_length=32)


def validate_patch(body: RecordingPatch) -> dict[str, Any]:
    """The columns to change, for the fields that were sent. group, date and lmsStatus cannot be
    emptied; for the optional fields "" and null both mean "clear it"."""
    sent = body.model_fields_set
    changes: dict[str, Any] = {}

    if "group" in sent:
        group = (body.group or "").strip()
        if not group:
            raise PayloadError("'group' cannot be empty.")
        if not _GROUP.match(group):
            raise PayloadError("'group' may hold letters, digits, spaces and _ - . (at most 100 characters).")
        changes["group_name"] = group

    if "date" in sent:
        text = (body.date or "").strip()
        if not text:
            raise PayloadError("'date' cannot be empty (yyyy-MM-dd).")
        try:
            if len(text) != 10:
                raise ValueError
            changes["session_date"] = date.fromisoformat(text)
        except ValueError as exc:
            raise PayloadError("'date' must be yyyy-MM-dd.") from exc

    if "startTime" in sent:
        start = _optional(body.startTime)
        if start is not None and not _TIME.match(start):
            raise PayloadError("'startTime' must be HH:mm (24-hour), or empty to clear it.")
        changes["start_time"] = start

    if "fileName" in sent:
        changes["file_name"] = _optional(body.fileName)
    if "type" in sent:
        changes["record_type"] = _optional(body.type)

    if "link" in sent:
        link = _optional(body.link)
        if link is None:
            changes["drive_link"], changes["zoom_link"] = None, None
        elif is_drive_file_link(link):
            changes["drive_link"], changes["zoom_link"] = link, None
        elif is_zoom_recording_link(link):
            changes["drive_link"], changes["zoom_link"] = None, link
        else:
            raise PayloadError(
                "'link' must be a Google Drive link to one file (https://drive.google.com/file/d/<id>/view) "
                "or a Zoom recording link (https://<host>.zoom.us/rec/share/...), or empty to clear it."
            )

    if "lmsStatus" in sent:
        status = (body.lmsStatus or "").strip()
        if status not in LMS_STATUSES:
            raise PayloadError(f"'lmsStatus' must be one of: {', '.join(LMS_STATUSES)}.")
        changes["lms_status"] = status
    return changes


async def patch_recording(
    session: AsyncSession, recording_id: uuid.UUID, changes: dict[str, Any], now: datetime
) -> tuple[Recording | None, list[str]]:
    """(recording, the columns that actually changed), or (None, []) for an unknown id.

    A new link replaces the stored one (a Drive link clears the Zoom link and the other way
    round), sets source from it, and puts lms_status back to "pending": the LMS may hold the old
    link. Asking for another lmsStatus in the same request is refused rather than silently ignored.
    """
    recording = await session.get(Recording, recording_id, with_for_update=True)
    if recording is None:
        return None, []

    changed = [column for column, value in changes.items() if getattr(recording, column) != value]
    if "group_name" in changed:
        await ensure_group(session, changes["group_name"], now)
    link_changed = any(column in ("drive_link", "zoom_link") for column in changed)
    if link_changed and changes.get("lms_status", "pending") != "pending":
        raise PayloadError(
            "A new link puts 'lmsStatus' back to pending, because the LMS may still hold the old link. "
            "Change the link first, then set 'lmsStatus' in a separate request."
        )

    for column in changed:
        setattr(recording, column, changes[column])
    if link_changed:
        recording.source = _source(recording.drive_link)
        if recording.lms_status != "pending":
            recording.lms_status = "pending"
            changed.append("lms_status")
    if "lms_status" in changed:
        recording.lms_updated_at = now
    if changed:
        recording.updated_at = now
        await session.flush()
    return recording, changed


@router.patch("/api/v1/recordings/{recording_id}", dependencies=[Depends(require_client)])
async def patch_recording_endpoint(recording_id: str, body: RecordingPatch, request: Request) -> JSONResponse:
    try:
        key = uuid.UUID(recording_id)
    except ValueError as exc:
        raise ApiError(404, "Not found") from exc
    try:
        changes = validate_patch(body)
    except PayloadError as exc:
        raise ApiError(400, "Invalid request", str(exc)) from exc

    state = request.app.state
    try:
        async with state.sessionmaker() as session, session.begin():
            recording, changed = await patch_recording(session, key, changes, state.clock())
            if recording is None:
                raise ApiError(404, "Not found")
            view = recording_view(recording)
    except PayloadError as exc:
        raise ApiError(400, "Invalid request", str(exc)) from exc
    except IntegrityError as exc:
        # The unique index on group + date + start time: another recording is already that session.
        raise ApiError(409, "Conflict", "Another recording already exists for that group, date and start time.") from exc

    if changed:
        emit(
            "recording.patched",
            recordingId=view["id"],
            group=view["group"],
            date=view["date"],
            fields=sorted(changed),
            lmsStatus=view["lmsStatus"],
            link=link_preview(view["driveLink"] or view["zoomLink"]),
        )
    return _no_store({field: view[field] for field in LATEST_FIELDS})
"""What a signed-in user can change from the dashboard: the admin on any recording, a coordinator
on the recordings of their own groups (anything else is answered as if it did not exist).

    PATCH /api/v1/dashboard/recordings/{id}          edit a recording (the recordings module's own rules)
    POST  /api/v1/dashboard/recordings/{id}/attach   put its Drive link on the LMS, through a job
    POST  /api/v1/dashboard/recordings/{id}/cancel   stop that job while it still waits in the queue

All need the dashboard session and the X-Dashboard-Request: 1 header; the n8n API key does not
work here and never reaches the browser. Nothing is re-implemented: an edit goes through
recordings.validate_patch / patch_recording, exactly like PATCH /api/v1/recordings/{id}, and an
attach builds the same recording.process job /api/v1/jobs builds - validation.validate_payload,
jobs.create_job, then the existing dispatcher - with the recording's id on it. When the job
finishes, recording_jobs.record_job_outcome sets the recording's LMS status.

A coordinator may also move a recording to another group only if that group is theirs too.

Every change is written to admin_audit_log: who, which recording, what, when.
"""

from __future__ import annotations

import logging
import uuid
from datetime import datetime
from typing import Any

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, StrictBool
from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import AsyncSession

from .access import Viewer, current_viewer
from .auth import require_dashboard_header
from .api import ApiError, _json
from .dashboard import parse_id, recording_item
from .jobs import cancel_job, create_job
from .models import ACTIVE_JOB_STATUSES, AdminAuditLog, Job, Recording
from .observability import emit, link_preview
from .recordings import RecordingPatch, patch_recording, validate_patch
from .validation import PayloadError, validate_payload

# The session first (401), then the header (403).
router = APIRouter(dependencies=[Depends(current_viewer), Depends(require_dashboard_header)])

# patch_recording reports columns; the dashboard and the audit log speak the API's field names.
FIELD_NAMES = {"group_name": "group", "session_date": "date", "start_time": "startTime", "file_name": "fileName",
               "record_type": "type", "drive_link": "driveLink", "zoom_link": "zoomLink", "lms_status": "lmsStatus"}


def _fields(columns: list[str]) -> list[str]:
    return sorted(FIELD_NAMES.get(column, column) for column in columns)


def audit(session: AsyncSession, viewer: Viewer, action: str, recording_id: uuid.UUID,
          details: dict[str, Any], now: datetime) -> None:
    session.add(AdminAuditLog(username=viewer.user.username, user_id=viewer.user.id, action=action,
                              recording_id=recording_id, details=details, created_at=now))
    emit("dashboard.audit", username=viewer.user.username, role=viewer.user.role, action=action,
         recordingId=str(recording_id), **details)


async def _visible_recording(session: AsyncSession, viewer: Viewer, key: uuid.UUID) -> Recording:
    """The recording, locked for this transaction, or 404 when it does not exist or is not the viewer's."""
    recording = (await session.execute(select(Recording).where(Recording.id == key).with_for_update())).scalar_one_or_none()
    if recording is None or not viewer.can_see(recording.group_name):
        raise ApiError(404, "Not found")
    return recording


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


# ----------------------------------------------------------------------------- edit


@router.patch("/api/v1/dashboard/recordings/{recording_id}")
async def edit_recording(recording_id: str, body: RecordingPatch, request: Request,
                         viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """Change only the fields sent. A new link sets driveLink/zoomLink and source, and puts the LMS
    status back to pending - the recordings module's rules, unchanged."""
    key = parse_id(recording_id)
    try:
        changes = validate_patch(body)
    except PayloadError as exc:
        raise ApiError(400, "Invalid request", str(exc)) from exc
    if "group_name" in changes and not viewer.can_see(changes["group_name"]):
        raise ApiError(403, "Forbidden", "You can only move a recording to one of your own groups.")

    state = request.app.state
    now = state.clock()
    try:
        async with state.sessionmaker() as session, session.begin():
            await _visible_recording(session, viewer, key)
            recording, changed = await patch_recording(session, key, changes, now)
            if recording is None:
                raise ApiError(404, "Not found")
            if changed:
                details: dict[str, Any] = {"fields": _fields(changed)}
                if {"drive_link", "zoom_link"} & set(changed):
                    details["link"] = link_preview(recording.drive_link or recording.zoom_link) or "(cleared)"
                if "lms_status" in changed:
                    details["lmsStatus"] = recording.lms_status
                audit(session, viewer, "recording.update", key, details, now)
            last = (
                await session.execute(select(Job).where(Job.recording_id == key).order_by(Job.created_at.desc()).limit(1))
            ).scalar_one_or_none()
            item = recording_item(recording, last)
    except PayloadError as exc:
        raise ApiError(400, "Invalid request", str(exc)) from exc
    except IntegrityError as exc:
        raise ApiError(409, "Conflict", "Another recording already exists for that group, date and start time.") from exc
    return _no_store({**item, "changed": _fields(changed)})


# ----------------------------------------------------------------------------- attach


class AttachBody(BaseModel):
    """Unknown fields are refused. dryRun defaults to true: a real write must be asked for, with a
    real JSON boolean - strings such as "false" or "yes" are refused, never guessed at."""

    model_config = ConfigDict(extra="forbid")
    replaceExisting: StrictBool = False
    dryRun: StrictBool = True


def _cannot_attach(reason: str, message: str, **extra: Any) -> ApiError:
    return ApiError(409, "Cannot attach", {"reason": reason, "message": message, **extra})


@router.post("/api/v1/dashboard/recordings/{recording_id}/attach")
async def attach_recording(recording_id: str, request: Request, body: AttachBody | None = None,
                           viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    key = parse_id(recording_id)
    options = body or AttachBody()
    state = request.app.state
    now = state.clock()

    async with state.sessionmaker() as session, session.begin():
        # Locked, so two clicks cannot both see "no job running" and start two jobs.
        recording = await _visible_recording(session, viewer, key)
        if not recording.drive_link:
            if recording.zoom_link:
                raise _cannot_attach("zoomOnly", "This recording only has a Zoom link. The LMS step attaches Google "
                                     "Drive links only: add the recording's Drive link first.")
            raise _cannot_attach("noLink", "This recording has no link yet: add its Google Drive link first.")

        running = (
            await session.execute(
                select(Job.id).where(Job.recording_id == key, Job.status.in_(("queued", *ACTIVE_JOB_STATUSES))).limit(1)
            )
        ).scalar_one_or_none()
        if running is not None:
            raise _cannot_attach("jobInProgress", "An attach job for this recording is already queued or running.",
                                 jobId=str(running))

        # Exactly the payload POST /api/v1/jobs would accept, checked by the same rules. The start
        # time only matters when the group has several sessions that day.
        requested = {
            "group": recording.group_name,
            "recordLink": recording.drive_link,
            "date": recording.session_date.isoformat(),
            "replaceExisting": options.replaceExisting,
            "dryRun": options.dryRun,
        }
        if recording.start_time:
            requested["startTime"] = recording.start_time
        try:
            payload = validate_payload("recording.process", requested)
        except PayloadError as exc:
            raise _cannot_attach("invalidRecording", str(exc)) from exc

        job, _ = await create_job(session, job_type="recording.process", payload=payload, idempotency_key=None,
                                  now=now, max_attempts=state.settings.default_max_attempts)
        job.recording_id = key
        audit(session, viewer, "recording.attach", key,
              {"jobId": str(job.id), "dryRun": options.dryRun, "replaceExisting": options.replaceExisting}, now)
        response = {"recordingId": str(key), "jobId": str(job.id), "status": job.status,
                    "dryRun": options.dryRun, "replaceExisting": options.replaceExisting}

    try:
        await state.dispatcher.dispatch()
    except Exception as exc:  # noqa: BLE001 - the job is stored; the sweeper hands it out anyway
        emit("dashboard.dispatch_deferred", level=logging.WARNING, jobId=response["jobId"], error=type(exc).__name__)
    return _no_store(response, 202)


@router.post("/api/v1/dashboard/recordings/{recording_id}/cancel")
async def cancel_recording_job(recording_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """Stop the recording's attach job while it still waits in the queue (jobs.cancel_job, the same as
    POST /api/v1/jobs/{id}/cancel). A job an agent has already taken is not stopped here: the agent
    may be half-way through the LMS, so it is left to finish or fail on its own."""
    key = parse_id(recording_id)
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        await _visible_recording(session, viewer, key)
        # Locked, so the dispatcher cannot hand the job to an agent while it is being cancelled.
        job = (
            await session.execute(
                select(Job).where(Job.recording_id == key, Job.status.in_(("queued", *ACTIVE_JOB_STATUSES)))
                .order_by(Job.created_at.desc()).limit(1).with_for_update()
            )
        ).scalar_one_or_none()
        if job is None:
            raise ApiError(409, "Cannot cancel", {"reason": "noOpenJob", "message": "This recording has no attach job waiting."})
        if job.status != "queued":
            raise ApiError(409, "Cannot cancel", {
                "reason": "alreadyStarted", "jobId": str(job.id),
                "message": f"An agent has already taken this job ({job.status}); it can no longer be cancelled.",
            })
        await cancel_job(session, job.id, now)
        audit(session, viewer, "recording.cancel", key, {"jobId": str(job.id)}, now)
        response = {"recordingId": str(key), "jobId": str(job.id), "status": "cancelled"}
    return _no_store(response)

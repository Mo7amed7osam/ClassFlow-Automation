"""What a finished attach job means for the recording it was made from.

Only jobs the dashboard creates from a recording carry a recording_id; for every other job this
does nothing. It runs in the same transaction that records the job's outcome, so a job's result
and its recording's LMS status can never disagree.

    succeeded                    -> lms_status "attached"
    failed (for good)            -> lms_status "failed"
    a retryable failure          -> nothing yet: the job is queued again
    a dry run, either way        -> nothing: a dry run writes nothing to the LMS
    the recording's link changed -> nothing: the job attached a link the recording no longer has
    since the job was made
"""

from __future__ import annotations

import logging
from datetime import datetime

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .models import GoogleSheetsSyncRecord, Job, Recording
from .observability import emit

OUTCOME_STATUS = {"succeeded": "attached", "failed": "failed"}


async def record_job_outcome(session: AsyncSession, job: Job, now: datetime) -> str | None:
    """Update the job's recording, if it has one. Returns the new lms_status, or None if unchanged."""
    if job.status not in OUTCOME_STATUS:
        return None
    payload = job.payload or {}
    await _record_sheet_outcome(session, job, now)
    if job.recording_id is None:
        return None
    if payload.get("dryRun"):
        emit("recording.dry_run_finished", recordingId=str(job.recording_id), jobId=str(job.id), outcome=job.status)
        return None

    recording = await session.get(Recording, job.recording_id, with_for_update=True)
    if recording is None:
        return None
    if recording.drive_link != payload.get("recordLink"):
        emit("recording.outcome_skipped", level=logging.WARNING, recordingId=str(recording.id), jobId=str(job.id),
             reason="the recording's link changed after the job was created")
        return None

    status = OUTCOME_STATUS[job.status]
    recording.lms_status = status
    recording.lms_updated_at = now
    recording.updated_at = now
    emit("recording.lms_status", recordingId=str(recording.id), jobId=str(job.id), lmsStatus=status)
    return status


async def _record_sheet_outcome(session: AsyncSession, job: Job, now: datetime) -> None:
    """Close the exact read-only source row only after the worker's attach result is final."""
    rows = (await session.execute(
        select(GoogleSheetsSyncRecord).where(GoogleSheetsSyncRecord.job_id == job.id).with_for_update()
    )).scalars().all()
    if not rows or (job.payload or {}).get("dryRun"):
        return
    for row in rows:
        row.status = "attached" if job.status == "succeeded" else (
            "conflict" if (job.error or {}).get("code") == "recordingConflict" else "failed")
        row.detail = None if job.status == "succeeded" else str((job.error or {}).get("message") or "LMS attachment failed")[:500]
        row.processed_at = now
        row.updated_at = now
        emit("google_sheets.attach_finished", sheetRecordId=str(row.id), jobId=str(job.id), status=row.status)

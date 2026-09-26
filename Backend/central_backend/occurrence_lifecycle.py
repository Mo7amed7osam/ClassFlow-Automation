"""Keep the durable class-occurrence view in step with finished worker jobs.

The job remains the source of truth for a particular command.  An occurrence is the compact,
operator-facing lifecycle view across all commands for one class.  It intentionally never claims
that a Zoom link was attached to LMS merely because it was found; that is a later, separate job.
"""

from __future__ import annotations

from datetime import datetime

from sqlalchemy.ext.asyncio import AsyncSession

from .models import ClassOccurrence, Job


_SUCCESS_STATES = {
    "lms.run_session": "live",
    "lms.attendance": "attendanceSubmitted",
    "lms.late_joiners": "attendanceFinalized",
    "lms.complete": "lmsSessionCompleted",
    "zoom.recording": "zoomLinkFound",
}

# Jobs can be retried and can finish after a later independent stage.  Never let a late retry turn
# a completed occurrence back into "live" or "attendance submitted".
_STATE_ORDER = {
    "scheduled": 0,
    "live": 1,
    "attendancePending": 2,
    "attendanceSubmitted": 3,
    "correctionPending": 4,
    "attendanceFinalized": 5,
    "lmsSessionCompleted": 6,
    "recordingPending": 7,
    "zoomLinkFound": 8,
    "zoomLinkAttached": 9,
    "waitingForDrive": 10,
    "driveLinkFound": 11,
    "driveLinkAttached": 12,
}


async def record_occurrence_outcome(session: AsyncSession, job: Job, now: datetime) -> None:
    """Project a final job outcome into its occurrence without hiding partial failures."""
    if job.occurrence_id is None or job.status not in ("succeeded", "failed"):
        return

    occurrence = await session.get(ClassOccurrence, job.occurrence_id, with_for_update=True)
    if occurrence is None:
        return

    occurrence.updated_at = now
    if job.status == "failed":
        # A later stage can still be run manually after an agent/browser problem.  Preserve the
        # last error instead of marking the entire class irrecoverably failed.
        error = job.error or {}
        occurrence.last_error = str(error.get("message") or error.get("code") or "Worker job failed")[:500]
        return

    occurrence.last_error = None
    result = job.result or {}
    if job.type == "class.run":
        occurrence.actual_start = _timestamp(result.get("startedAt")) or occurrence.actual_start
        occurrence.actual_end = _timestamp(result.get("endedAt")) or now
        return

    state = _SUCCESS_STATES.get(job.type)
    if state and _STATE_ORDER.get(occurrence.state, -1) <= _STATE_ORDER[state]:
        occurrence.state = state
    if job.type == "zoom.recording":
        link = result.get("shareUrl")
        if isinstance(link, str) and link:
            occurrence.zoom_recording_url = link
            occurrence.recording_found_at = now


def _timestamp(value: object) -> datetime | None:
    if not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None

"""Every class and where it stands: the Sessions page.

    GET /api/v1/dashboard/sessions?from=&to=&group=&coordinator=

One row per class, and for each of them one entry per stage of running it - the same eleven the
Windows app shows on a class card. A stage is not a thing that is written down: it is read off the
jobs that were created for that class, so the page and the queue can never disagree about what
happened. Nothing here writes.

What a stage's state means:

    done      it finished and did what it was for
    running   a worker has it now
    waiting   its job exists and is queued
    due       its time has come or is coming, and no job exists yet
    failed    its job finished badly; the reason is on the stage
    later     not due yet
    blocked   it cannot run as things stand - no Zoom link, no account chosen
    missing   this stage has no cloud implementation at all

`missing` is there because saying "not started" in grey next to a stage that works would read the
same as a stage that does not exist. A page that cannot tell those apart is worse than no page.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass
from datetime import UTC, date, datetime, timedelta
from typing import Any

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from .api import _json
from .auth import CurrentUser, current_user
from .dashboard import group_items
from .models import ClassPlan, Job, LmsAccount, RunDelegation, User
from .scheduling import CAIRO, GRACE, STAGES, due_at, idempotency_key

router = APIRouter()

# The stages the scheduler makes only once the meeting was held.
FOLLOWS_MEETING = frozenset(stage.job_type for stage in STAGES if stage.follows_meeting)


@dataclass(frozen=True)
class StageShape:
    """A stage as the page draws it: what it is called, when it is due, and whether it exists yet."""

    key: str
    label: str
    caption: str
    job_type: str | None          # None when nothing runs this stage anywhere
    offset: timedelta | None      # relative to the class's start; None for "after class"
    built: bool


# The eleven stages of a class card, in the order the Windows app draws them. Which of them are
# built is a fact about this repository and is stated here rather than implied by their absence.
SHAPES: tuple[StageShape, ...] = (
    StageShape("zoom", "Zoom", "Opens", "class.run", timedelta(minutes=-15), True),
    StageShape("run", "Run", "At start", "lms.run_session", timedelta(0), True),
    StageShape("attendance", "Attendance", "Later", "lms.attendance", timedelta(minutes=90), True),
    StageShape("lateJoiners", "Late joiners", "Later", "lms.late_joiners", timedelta(minutes=180), True),
    StageShape("complete", "Complete", "Later", "lms.complete", timedelta(minutes=190), True),
    # No job of its own: a meeting is ended by the class.run that held it, at the end of holding
    # it. So this stage is read from how that job finished - see _ended.
    StageShape("ended", "Ended", "After class", None, None, True),
    StageShape("zoomReport", "Zoom report", "Later", "zoom.report", timedelta(minutes=200), True),
    StageShape("zoomRecording", "Zoom recording", "Later", "zoom.recording", timedelta(minutes=210), True),
    StageShape("drive", "Drive", "After class", "recording.process", None, True),
    StageShape("material", "Material", "After class", None, None, False),
    StageShape("assignment", "Assignment", "Due", None, None, False),
)

# A job's status, as a stage's state.
FROM_JOB = {
    "succeeded": "done",
    "running": "running",
    "assigned": "running",
    "queued": "waiting",
    "failed": "failed",
    "cancelled": "failed",
}


def _stage_state(
    shape: StageShape,
    plan: ClassPlan,
    job: Job | None,
    now: datetime,
    blocked: str | None,
) -> dict[str, Any]:
    """One stage of one class, as the page draws it."""
    entry: dict[str, Any] = {"key": shape.key, "label": shape.label, "caption": shape.caption}

    if not shape.built:
        entry["state"] = "missing"
        entry["detail"] = "not built for the cloud yet"
        return entry

    if job is not None:
        entry["state"] = FROM_JOB.get(job.status, "waiting")
        entry["jobId"] = str(job.id)
        if job.status == "failed" and job.error:
            entry["detail"] = (job.error or {}).get("message") or (job.error or {}).get("code")
            entry["retryable"] = bool((job.error or {}).get("retryable"))
        elif job.status == "succeeded":
            # A stage can succeed and still have something worth reading: a class that was held but
            # whose meeting could not be closed is not the same as one that ran clean, and drawn as
            # a plain tick it looks identical. The warning is shown instead of the time, because the
            # time is the part nobody needs when something is wrong.
            warning = (job.result or {}).get("warning")
            if warning:
                entry["warning"] = warning
                entry["detail"] = warning
            elif job.finished_at:
                entry["detail"] = f"Done {job.finished_at.astimezone(CAIRO):%H:%M}"
        return entry

    when = _due(shape, plan)
    if when is not None:
        entry["dueAt"] = when
        entry["detail"] = f"Due {when.astimezone(CAIRO):%H:%M}"

    if blocked is not None:
        entry["state"] = "blocked"
        entry["detail"] = blocked
    elif when is None:
        entry["state"] = "later"
    elif when > now:
        entry["state"] = "later"
    elif when < now - GRACE:
        # Its window came and went with no job made. Not "later", which reads as fine.
        entry["state"] = "failed"
        entry["detail"] = "its time passed and nothing ran"
    else:
        entry["state"] = "due"
    return entry


def _ended(shape: StageShape, held: Job | None) -> dict[str, Any]:
    """Whether the meeting was closed, from the class.run that held it.

    There is no separate job to end a meeting: the worker holding it is the only thing in the
    meeting, and it ends it for everyone when the class's time is up. A separate job would queue
    behind the one holding the meeting and arrive after it was already over. So this stage says
    what that job reported, and a meeting left open is a failure here rather than a quiet tick.
    """
    entry: dict[str, Any] = {"key": shape.key, "label": shape.label, "caption": shape.caption}
    if held is None or held.status in ("queued", "assigned"):
        entry["state"] = "later"
        return entry
    if held.status == "running":
        entry["state"] = "waiting"
        entry["detail"] = "when the class's time is up"
        return entry
    result = held.result or {}
    if held.status != "succeeded":
        entry["state"] = "blocked"
        entry["detail"] = "the meeting was not held"
        return entry
    if result.get("dryRun"):
        entry["state"] = "done"
        entry["detail"] = "rehearsal: nothing was opened"
    elif result.get("endedTheMeeting") is False:
        entry["state"] = "failed"
        entry["detail"] = result.get("warning") or "the meeting could not be ended and may still be open"
    else:
        entry["state"] = "done"
        if held.finished_at:
            entry["detail"] = f"Ended {held.finished_at.astimezone(CAIRO):%H:%M}"
    entry["jobId"] = str(held.id)
    return entry


def _due(shape: StageShape, plan: ClassPlan) -> datetime | None:
    """When this stage is due, in UTC, or None for one that has no clock of its own."""
    if shape.offset is None or not plan.start_time:
        return None
    # Reuses the scheduler's own arithmetic for the stages it schedules, so the page cannot say a
    # different time from the one a class is actually opened at.
    for scheduled in STAGES:
        if scheduled.job_type == shape.job_type:
            return due_at(plan, scheduled)
    from datetime import time as _time

    try:
        local = _time.fromisoformat(plan.start_time)
    except ValueError:
        return None
    return datetime.combine(plan.session_date, local, tzinfo=CAIRO).astimezone(UTC) + shape.offset


def _blocked(shape: StageShape, plan: ClassPlan, delegation: RunDelegation | None) -> str | None:
    """Why this stage cannot run as things stand, or None."""
    if delegation is None or not delegation.enabled:
        return "this coordinator is not turned on"
    if shape.job_type == "class.run" and not plan.meeting_url:
        return "no Zoom link on this class"
    if shape.job_type and shape.job_type.startswith(("class.", "zoom.")) and delegation.zoom_account_id is None:
        return "no Zoom account chosen for this coordinator"
    if shape.job_type and shape.job_type.startswith("lms.") and delegation.lms_account_id is None:
        return "no LMS account chosen for this coordinator"
    return None


def _headline(stages: list[dict[str, Any]]) -> str:
    """What the class card says in one word, for the list and for the counters."""
    states = {s["state"] for s in stages}
    if "failed" in states:
        return "needsAttention"
    if "blocked" in states:
        return "blocked"
    if "running" in states:
        return "running"
    if states <= {"done", "missing"}:
        return "done"
    return "planned"


@router.get("/api/v1/dashboard/sessions")
async def sessions(
    request: Request,
    from_: date | None = Query(default=None, alias="from"),
    to: date | None = Query(default=None),
    group: str | None = Query(default=None, max_length=100),
    coordinator: str | None = Query(default=None),
    user: CurrentUser = Depends(current_user),
) -> JSONResponse:
    """Every class in the window, and where each one stands.

    A coordinator sees only their own classes; an admin sees everyone's. The window defaults to the
    week the Sessions page opens on.
    """
    now = request.app.state.clock()
    today = now.astimezone(CAIRO).date()
    start = from_ or today
    finish = to or (start + timedelta(days=6))
    if finish < start:
        start, finish = finish, start
    finish = min(finish, start + timedelta(days=92))

    async with request.app.state.sessionmaker() as session:
        query = select(ClassPlan).where(ClassPlan.session_date.between(start, finish))
        if group:
            query = query.where(ClassPlan.group_name == group.strip())

        # A coordinator sees their own classes and nobody else's, whatever they ask for.
        if not user.is_admin:
            query = query.where(ClassPlan.coordinator_id == user.id)
        elif coordinator:
            try:
                query = query.where(ClassPlan.coordinator_id == uuid.UUID(coordinator))
            except ValueError:
                pass

        plans = (await session.execute(
            query.order_by(ClassPlan.session_date, ClassPlan.start_time).limit(500)
        )).scalars().all()

        rows = [await _class_view(session, plan, now) for plan in plans]

    counters = {
        "runningOnTheLms": sum(1 for r in rows if r["headline"] == "running"),
        "classesToday": sum(1 for r in rows if r["date"] == today.isoformat()),
        "needAttention": sum(1 for r in rows if r["headline"] == "needsAttention"),
        "blocked": sum(1 for r in rows if r["headline"] == "blocked"),
        "fullyDone": sum(1 for r in rows if r["headline"] == "done"),
    }
    return JSONResponse(
        _json({"from": start.isoformat(), "to": finish.isoformat(),
               "classes": rows, "counters": counters,
               "groups": sorted({r["group"] for r in rows})}),
        headers={"Cache-Control": "no-store"},
    )


async def _class_view(session: AsyncSession, plan: ClassPlan, now: datetime) -> dict[str, Any]:
    delegation = await session.get(RunDelegation, plan.coordinator_id)
    coordinator = await session.get(User, plan.coordinator_id)

    # Every job ever made for this class, by the key the scheduler creates them under. A stage run
    # by hand from the dashboard carries the same key, so both show in the same place.
    jobs: dict[str, Job] = {}
    for shape in SHAPES:
        if not shape.job_type:
            continue
        for scheduled in STAGES:
            if scheduled.job_type == shape.job_type:
                key = idempotency_key(plan.id, scheduled)
                found = (await session.execute(
                    select(Job).where(Job.idempotency_key == key))).scalar_one_or_none()
                if found is not None:
                    jobs[shape.key] = found
                break
        else:
            found = (await session.execute(
                select(Job)
                .where(Job.type == shape.job_type,
                       Job.payload["classPlanId"].astext == str(plan.id))
                .order_by(Job.created_at.desc())
                .limit(1)
            )).scalar_one_or_none()
            if found is not None:
                jobs[shape.key] = found

    # The scheduler makes no follow-up for a class whose meeting failed or never came, so on the
    # page those stages say why, instead of turning red one by one as their times pass.
    meeting = jobs.get("zoom")
    not_held = meeting is not None and meeting.status in ("failed", "cancelled")

    def blocked(shape: StageShape) -> str | None:
        if not_held and shape.job_type in FOLLOWS_MEETING and shape.key not in jobs:
            return "the meeting was not held"
        return _blocked(shape, plan, delegation)

    stages = [
        _ended(shape, meeting) if shape.key == "ended"
        else _stage_state(shape, plan, jobs.get(shape.key), now, blocked(shape))
        for shape in SHAPES
    ]

    starts_at = None
    if plan.start_time:
        for scheduled in STAGES:
            if scheduled.job_type == "lms.run_session":
                starts_at = due_at(plan, scheduled)
                break

    return {
        "classPlanId": str(plan.id),
        "group": plan.group_name,
        "title": plan.title,
        "date": plan.session_date.isoformat(),
        "startTime": plan.start_time,
        "startsAt": starts_at,
        "coordinator": {"id": str(plan.coordinator_id),
                        "name": coordinator.display_name if coordinator else None},
        "meetingUrl": plan.meeting_url,
        "planStatus": plan.status,
        "stages": stages,
        "headline": _headline(stages),
    }

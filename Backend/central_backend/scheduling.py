"""What makes a class happen at its time, with nobody at a keyboard.

On a Windows PC this was Windows Task Scheduler: one task per class, registered on its date, firing
fifteen minutes before. A server has no such thing, and without a replacement every stage in the
system waits for somebody to ask for it - which is the one thing this deployment exists to avoid.

So the backend looks at the classes it knows about and creates the jobs they are due. A class plan
is the record; a job is one stage of running it.

Three rules decide everything here:

  * **A stage is created once, ever.** The idempotency key is the plan and the stage, so a restart,
    a second pass a minute later, or two passes racing each other all reach the same one job. This
    is what stops a class being opened twice, and it does not depend on the scheduler remembering
    anything between passes.

  * **A class whose time has passed is not opened.** After an outage the queue would otherwise fill
    with this morning's classes at four in the afternoon, and every one of them would be wrong. A
    class is opened inside its window and missed outside it, and a missed class is left for a person.

  * **Only for a coordinator who is turned on.** The same rule the sign-in endpoint keeps: turning
    somebody off stops their classes at the next pass, without anything having to be cancelled.

Local times are Africa/Cairo, because that is what the LMS lists and what a coordinator reads.
Everything stored and compared is UTC.
"""

from __future__ import annotations

import asyncio
import logging
import uuid
from dataclasses import dataclass
from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from .jobs import create_job
from .notifications import class_blocked
from .models import ClassOccurrence, ClassPlan, Job, LmsAccount, RunDelegation, User, ZoomAccount
from .observability import emit
from .validation import PayloadError, validate_payload

def _zone(name: str = "Africa/Cairo") -> ZoneInfo:
    """The zone class times are written in.

    Resolved once, here, so a machine without a zone database says which one is missing instead of
    raising out of an import nobody was looking at. The `tzdata` dependency means this works on
    Windows too, where the standard library ships no zones at all.
    """
    try:
        return ZoneInfo(name)
    except Exception as problem:  # noqa: BLE001 - ZoneInfoNotFoundError and its causes
        raise RuntimeError(
            f"The time zone '{name}' is not on this machine, so no class time can be read. "
            "The `tzdata` package provides it; a slim container also needs the OS package."
        ) from problem


CAIRO = _zone()


@dataclass(frozen=True)
class Stage:
    """A stage of a class, and when it is due relative to the class's start."""

    job_type: str
    offset: timedelta
    what: str
    follows_meeting: bool = False


# What the scheduler creates, and when.
#
# The meeting opens a quarter of an hour early, which is what the Windows app did and what the
# class card means by "Opens 18:45" for a 19:00 class: people arrive before the hour and there has
# to be somebody there to let them in. The LMS session is started on the hour, because the
# dashboard is the record of what happened rather than the door.
#
# After that the times are the Windows app's own follow-up queue, stage for stage: attendance an
# hour and a half in, from what the meeting showed until then; the late joiners three hours in,
# from everything it showed; the session completed after that correction; and Zoom's own report
# and recording once Zoom has published them, which those stages wait for rather than fail on.
#
# The order in this tuple is the order the jobs are made, and a worker's LMS lane takes them first
# come first served - so the late joiners are always written before the session is completed.
STAGES: tuple[Stage, ...] = (
    Stage("class.run", timedelta(minutes=-15), "open the meeting and hold it"),
    Stage("lms.run_session", timedelta(0), "press Run Session as the class starts"),
    Stage("lms.attendance", timedelta(minutes=90), "write up who was there an hour and a half in", follows_meeting=True),
    Stage("lms.late_joiners", timedelta(minutes=180), "move the people who came late to Joined", follows_meeting=True),
    Stage("lms.complete", timedelta(minutes=190), "mark the session complete after the correction", follows_meeting=True),
    Stage("zoom.report", timedelta(minutes=200), "read Zoom's participants report once it is published", follows_meeting=True),
    Stage("zoom.recording", timedelta(minutes=210), "find the Zoom recording once it is processed", follows_meeting=True),
)


# How late a stage may still be created. A class that came due while the server was down is
# reopened inside this window and left alone outside it: fifteen minutes is long enough to survive
# a deployment and short enough that nothing opens a class that is effectively over.
GRACE = timedelta(minutes=15)

# How far ahead to look. A stage due in the next minute is created now rather than at the exact
# second, so a pass that runs slightly late still creates it.
LOOKAHEAD = timedelta(minutes=1)


def due_at(plan: ClassPlan, stage: Stage) -> datetime | None:
    """When this stage of this class is due, in UTC. None for a class with no time of its own."""
    if not plan.start_time:
        return None
    try:
        local = time.fromisoformat(plan.start_time)
    except ValueError:
        return None
    return datetime.combine(plan.session_date, local, tzinfo=CAIRO).astimezone(UTC) + stage.offset


def idempotency_key(plan_id: uuid.UUID, stage: Stage) -> str:
    """One key per class per stage, for all time. This is what stops a class running twice."""
    return f"plan:{plan_id}:{stage.job_type}"


async def ensure_occurrence(session: AsyncSession, plan: ClassPlan, payload: dict, now: datetime) -> ClassOccurrence:
    """Create one restart-safe occurrence for a plan."""
    occurrence = (await session.execute(
        select(ClassOccurrence).where(ClassOccurrence.class_plan_id == plan.id).with_for_update()
    )).scalar_one_or_none()
    scheduled_start = due_at(plan, Stage("occurrence", timedelta(), ""))
    if occurrence is None:
        zoom_id = payload.get("zoomAccountId")
        occurrence = ClassOccurrence(
            id=uuid.uuid4(), class_plan_id=plan.id, group_name=plan.group_name,
            session_date=plan.session_date, scheduled_start=scheduled_start,
            scheduled_end=scheduled_start + timedelta(hours=3) if scheduled_start else None,
            zoom_account_id=uuid.UUID(zoom_id) if zoom_id else None,
            lms_account_id=uuid.UUID(payload["lmsAccountId"]) if payload.get("lmsAccountId") else None,
            zoom_meeting_url=plan.meeting_url, state="scheduled", created_at=now, updated_at=now,
        )
        session.add(occurrence)
    elif occurrence.lms_account_id is None and payload.get("lmsAccountId"):
        # Occurrences made before the account binding migration are safe to backfill only from the
        # scheduler's already-authorised class payload; never infer a profile from a sheet row.
        occurrence.lms_account_id = uuid.UUID(payload["lmsAccountId"])
        occurrence.updated_at = now
    return occurrence


class Scheduler:
    """Turns the classes that are due into jobs. Started beside the sweeper; safe to run twice."""

    def __init__(
        self,
        sessionmaker: async_sessionmaker[AsyncSession],
        clock,  # noqa: ANN001 - the app's Clock, as the sweeper takes it
        interval_seconds: int = 30,
        notifier=None,  # noqa: ANN001 - the app's Notifier; None says nothing to anybody
    ) -> None:
        self._sessionmaker = sessionmaker
        self._clock = clock
        self._interval = interval_seconds
        self.notifier = notifier
        # Which classes have already been said to need somebody, so a pass every minute is not a
        # notice every minute.
        self._told: set[str] = set()

    async def schedule_once(self) -> dict[str, int]:
        """One pass. Returns what it did, for the log and for a test to read."""
        now = self._clock()
        counts = {"created": 0, "missed": 0}

        async with self._sessionmaker() as session, session.begin():
            # Only the coordinators turned on, and only their planned classes. A class already
            # opened, skipped or finished is not looked at again.
            enabled = select(RunDelegation.coordinator_id).where(RunDelegation.enabled.is_(True))
            plans = (await session.execute(
                select(ClassPlan)
                .where(
                    ClassPlan.status == "planned",
                    ClassPlan.coordinator_id.in_(enabled),
                    # A day either side, so the window is found whatever the local offset is.
                    ClassPlan.session_date.between(
                        (now - timedelta(days=1)).date(), (now + timedelta(days=1)).date()),
                )
                # Each plan is the scheduler's unit of work. Locking it until the jobs and
                # occurrence commit lets a second scheduler instance safely skip it; the unique
                # occurrence and job keys remain the final guard if a process dies mid-pass.
                .order_by(ClassPlan.id)
                .with_for_update(skip_locked=True)
                .limit(500)
            )).scalars().all()

            for plan in plans:
                # Record the scheduled class as soon as it enters the scheduler horizon, even
                # when the first action is not due yet. This makes the occurrence survive a
                # restart before the meeting-open minute and gives every eventual stage one
                # durable parent. The one-per-plan constraint and row lock make this restart- and
                # multi-instance-safe.
                payload = await self._payload(session, plan)
                occurrence = None
                if due_at(plan, Stage("occurrence", timedelta(), "")) is not None:
                    occurrence = await ensure_occurrence(session, plan, payload or {}, now)

                for stage in STAGES:
                    when = due_at(plan, stage)
                    if when is None:
                        continue
                    if when > now + LOOKAHEAD:
                        continue                       # not yet
                    if when < now - GRACE:
                        # A short outage must not lose a class merely because its usual
                        # pre-class dispatch minute passed. Recover the single class.run job
                        # while the scheduled class is still in progress; its plan/stage key
                        # guarantees this can never create a second meeting job.
                        if (stage.job_type == "class.run" and occurrence is not None
                                and occurrence.scheduled_end is not None and now < occurrence.scheduled_end):
                            pass
                        else:
                            # Too late to be worth opening. Said once per pass, so somebody can see
                            # it happened rather than wondering why a class never ran.
                            counts["missed"] += 1
                            emit("schedule.missed", level=logging.WARNING, group=plan.group_name,
                                 date=plan.session_date.isoformat(), startTime=plan.start_time,
                                 jobType=stage.job_type, lateBySeconds=int((now - when).total_seconds()))
                            continue

                    # What follows the meeting follows from it. A class whose meeting was never
                    # made, or failed to open, has no attendance to write and no report to read -
                    # so those stages are not made at all, rather than made to fail. Run Session is
                    # not one of them: the LMS session is started whether or not there is a link.
                    if stage.follows_meeting and not await self._meeting_held(session, plan):
                        continue

                    if payload is None:
                        self._say_blocked(plan, stage.job_type,
                                          "no LMS sign-in is chosen for this coordinator")
                        continue
                    if (why := self._can_run(stage, payload)) is not None:
                        emit("schedule.cannot_run", level=logging.WARNING, group=plan.group_name,
                             date=plan.session_date.isoformat(), jobType=stage.job_type, reason=why)
                        self._say_blocked(plan, stage.job_type, why)
                        continue

                    # Through the same validator an API caller goes through, so the scheduler
                    # cannot make a payload the rest of the system would refuse, and the defaults
                    # it fills in (how long to hold a meeting) are the documented ones.
                    try:
                        payload = validate_payload(stage.job_type, payload)
                    except PayloadError as problem:
                        emit("schedule.bad_payload", level=logging.ERROR, group=plan.group_name,
                             date=plan.session_date.isoformat(), jobType=stage.job_type, reason=str(problem))
                        continue

                    job, created = await create_job(
                        session,
                        job_type=stage.job_type,
                        payload=payload,
                        idempotency_key=idempotency_key(plan.id, stage),
                        now=now,
                        max_attempts=3,
                    )
                    # Existing jobs can predate the occurrence migration. Backfilling the link here
                    # makes restarts safe without ever creating a second job for the class stage.
                    if job.occurrence_id is None:
                        job.occurrence_id = occurrence.id
                    if created:
                        counts["created"] += 1
                        emit("schedule.created", group=plan.group_name, jobType=stage.job_type,
                             date=plan.session_date.isoformat(), startTime=plan.start_time)

        return counts

    @staticmethod
    async def _meeting_held(session: AsyncSession, plan: ClassPlan) -> bool:
        """Whether this class's meeting was made and did not fail - the condition for its follow-ups."""
        meeting = (await session.execute(
            select(Job.status).where(Job.idempotency_key == idempotency_key(plan.id, STAGES[0])))).scalar_one_or_none()
        return meeting is not None and meeting not in ("failed", "cancelled")

    def _say_blocked(self, plan: ClassPlan, job_type: str, why: str) -> None:
        """Tells somebody, once. A pass happens every minute; a class that cannot open is one piece
        of news, not sixty an hour. Kept in memory, so a restart may repeat it once - which is the
        right way round: a notice too many beats a class nobody hears about."""
        key = f"{plan.id}:{job_type}"
        if key in self._told:
            return
        self._told.add(key)
        class_blocked(self.notifier, plan, job_type, why)

    async def _payload(self, session: AsyncSession, plan: ClassPlan) -> dict | None:
        return await class_payload(session, plan)

    @staticmethod
    def _can_run(stage: Stage, payload: dict) -> str | None:
        return why_not(stage, payload)

    async def run(self, stop: asyncio.Event) -> None:
        """Until asked to stop. A failed pass is logged and the next one still happens."""
        while not stop.is_set():
            try:
                counts = await self.schedule_once()
                if counts["created"] or counts["missed"]:
                    emit("schedule.pass", **counts)
            except asyncio.CancelledError:
                raise
            except Exception as problem:  # noqa: BLE001 - one bad pass must not end the scheduler
                emit("schedule.error", level=logging.ERROR, error=type(problem).__name__)
            try:
                await asyncio.wait_for(stop.wait(), timeout=self._interval)
            except TimeoutError:
                pass


# Building a class's payload and saying why a stage cannot run are module-level, because a person
# pressing "run this now" on the dashboard must get the payload the scheduler would have made
# rather than a second copy of the same rules.


async def class_payload(session: AsyncSession, plan: ClassPlan) -> dict | None:
    """The stage payload for this class, or None when the class cannot say whose it is.

    A class with no LMS account named would run under whichever account the machine last used,
    and write one coordinator's class up under another's name. There is no sensible default, so
    the class is left alone and said about.
    """
    delegation = await session.get(RunDelegation, plan.coordinator_id)
    account_id = delegation.lms_account_id if delegation else None
    if account_id is None:
        emit("schedule.no_account", level=logging.WARNING, group=plan.group_name,
             date=plan.session_date.isoformat(),
             reason="the coordinator is turned on but no LMS account is chosen for them")
        return None

    account = await session.get(LmsAccount, account_id)
    if account is None or account.user_id != plan.coordinator_id:
        emit("schedule.no_account", level=logging.WARNING, group=plan.group_name,
             date=plan.session_date.isoformat(),
             reason="the chosen LMS account is not that coordinator's, or is gone")
        return None

    payload: dict = {
        "classPlanId": str(plan.id),
        "group": plan.group_name,
        "date": plan.session_date.isoformat(),
        "coordinatorId": str(plan.coordinator_id),
        "lmsAccountId": str(account.id),
        "dryRun": False,
    }
    # The Zoom account the meeting is opened by: the coordinator's account that hosts this
    # group, by the same rule the run plan shows - the one kept for the group, then the one
    # named after it, then their only account. Never another group's: a meeting opened by the
    # wrong account is the wrong room, and the students are in it before anyone notices. A
    # group none of their accounts hosts gets no Zoom stages, and the page says why.
    from .delegated_runs import _group_zoom, _zoom_accounts

    theirs = (await _zoom_accounts(session, [plan.coordinator_id])).get(plan.coordinator_id, [])
    if (zoom := _group_zoom(theirs, plan.group_name)) is not None:
        payload["zoomAccountId"] = str(zoom.id)
    if plan.start_time:
        payload["startTime"] = plan.start_time
    if plan.title:
        payload["title"] = plan.title
    if plan.meeting_url:
        payload["meetingUrl"] = plan.meeting_url
    return payload

def why_not(stage: Stage, payload: dict) -> str | None:
    """Why this stage cannot be created from this class, or None.

    The server refuses the same payloads the validator would, but earlier and with the class
    named: a job that is going to be rejected is better not made, and "this class has no Zoom
    link yet" is something a person can act on.
    """
    if stage.job_type == "class.run" and "meetingUrl" not in payload:
        return "no Zoom link on the class"
    if stage.job_type.startswith(("class.", "zoom.")) and "zoomAccountId" not in payload:
        return "none of the coordinator's Zoom accounts hosts this group"
    return None

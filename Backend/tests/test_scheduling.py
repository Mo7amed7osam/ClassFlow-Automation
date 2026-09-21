"""What makes a class happen at its time, and what stops it happening twice or too late.

These are the rules a server running unattended depends on, so they are tested against a real
database rather than reasoned about: a class opened twice is two meetings, and a class opened four
hours late is worse than one that never opened at all.
"""

from __future__ import annotations

import json
import secrets
import uuid
from datetime import UTC, date, datetime, timedelta
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from central_backend.auth import AuthSettings
from central_backend.main import create_app
from central_backend.scheduling import CAIRO, GRACE, STAGES, Scheduler, due_at, idempotency_key
from central_backend.user_data import SecretBox
from test_dashboard import ADMIN_PASSWORD, COORDINATOR_PASSWORD, make_user, run_sql

RUN_LMS = next(s for s in STAGES if s.job_type == "lms.run_session")   # on the hour
OPEN_MEETING = next(s for s in STAGES if s.job_type == "class.run")    # a quarter of an hour before
STAGE = RUN_LMS


@pytest.fixture
def box() -> SecretBox:
    return SecretBox(secrets.token_bytes(32))


@pytest.fixture
def app(settings, clock, box):  # noqa: ANN001, ANN201
    if not run_sql(settings.database_url, "SELECT 1 FROM users WHERE role = 'admin'"):
        make_user(settings.database_url, "admin", ADMIN_PASSWORD, role="admin", display_name="The Admin")
    built = create_app(settings, clock=clock, run_background=False, configure_logs=False,
                       auth_settings=AuthSettings(), dashboard_dist=Path("__no_dashboard_build__"),
                       secret_box=box)
    with TestClient(built) as client:
        yield client


def a_class(client: TestClient, *, at: str = "19:00", on: date | None = None,
            enabled: bool = True, with_account: bool = True, with_zoom: bool = False) -> tuple[str, str]:
    """A coordinator turned on, with an LMS account, and one planned class. (planId, coordinatorId)"""
    url = client.app.state.settings.database_url
    coordinator = make_user(url, f"omar{uuid.uuid4().hex[:8]}", COORDINATOR_PASSWORD)
    account_id = uuid.uuid4()
    if with_account:
        run_sql(url, """INSERT INTO lms_accounts (id, user_id, label, email, role, password_encrypted,
                                                  active, created_at, updated_at)
                        VALUES ($1::uuid, $2::uuid, 'Omar', 'omar@lms.example.com', 'coordinator',
                                'not-a-real-ciphertext', true, now(), now())""",
                account_id, uuid.UUID(coordinator))
    zoom_id = uuid.uuid4()
    if with_zoom:
        run_sql(url, """INSERT INTO zoom_accounts (id, user_id, account_id, label, zoom_email,
                                                   group_name, active, created_at, updated_at)
                        VALUES ($1::uuid, $2::uuid, 'CAI5_AIS4_S7', 'S7', 'omar@zoom.example.com',
                                'CAI5_AIS4_S7', true, now(), now())""",
                zoom_id, uuid.UUID(coordinator))
    run_sql(url, """INSERT INTO run_delegations (coordinator_id, enabled, lms_account_id,
                                                 zoom_account_id, created_at, updated_at)
                    VALUES ($1::uuid, $2, $3::uuid, $4::uuid, now(), now())""",
            uuid.UUID(coordinator), enabled, account_id if with_account else None,
            zoom_id if with_zoom else None)
    plan_id = uuid.uuid4()
    run_sql(url, """INSERT INTO class_plans (id, coordinator_id, group_name, session_date, start_time,
                                             source, status, imported_at, created_at, updated_at)
                    VALUES ($1::uuid, $2::uuid, 'CAI5_AIS4_S7', $3::date, $4, 'lms', 'planned',
                            now(), now(), now())""",
            plan_id, uuid.UUID(coordinator), on or date(2026, 9, 20), at)
    if with_zoom:
        run_sql(url, "UPDATE class_plans SET meeting_url = $2 WHERE id = $1::uuid",
                plan_id, "https://zoom.us/j/91473108490")
    return str(plan_id), coordinator


def at_local(day: date, hhmm: str) -> datetime:
    """That local moment, in UTC - the clock the scheduler is compared against."""
    hour, minute = (int(p) for p in hhmm.split(":"))
    return datetime(day.year, day.month, day.day, hour, minute, tzinfo=CAIRO).astimezone(UTC)


def jobs_of(client: TestClient, job_type: str = "lms.run_session") -> list:
    """The jobs of that type, with the payload read back as the dict it was written as."""
    rows = run_sql(client.app.state.settings.database_url,
                   "SELECT id, payload, idempotency_key FROM jobs WHERE type = $1", job_type)
    return [{**r, "payload": json.loads(r["payload"]) if isinstance(r["payload"], str) else r["payload"]}
            for r in rows]


def test_a_class_becomes_a_job_at_its_own_time(app, clock):
    plan, coordinator = a_class(app)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)

    # An hour before: nothing yet.
    clock.now = at_local(date(2026, 9, 20), "18:00")
    assert app.portal.call(scheduler.schedule_once) == {"created": 0, "missed": 0}
    assert jobs_of(app) == []

    # At the class's own time: one job, carrying whose class it is.
    clock.now = at_local(date(2026, 9, 20), "19:00")
    assert (app.portal.call(scheduler.schedule_once))["created"] == 1

    rows = jobs_of(app)
    assert len(rows) == 1
    payload = rows[0]["payload"]
    assert payload["group"] == "CAI5_AIS4_S7"
    assert payload["coordinatorId"] == coordinator
    assert payload["classPlanId"] == plan
    assert payload["lmsAccountId"]          # never left for the machine to guess
    assert payload["dryRun"] is False


def test_the_same_class_is_never_opened_twice(app, clock):
    """Pass after pass, and across a restart: one job. This is what stops two meetings."""
    a_class(app)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    clock.now = at_local(date(2026, 9, 20), "19:00")

    assert (app.portal.call(scheduler.schedule_once))["created"] == 1
    for _ in range(4):
        assert (app.portal.call(scheduler.schedule_once))["created"] == 0

    # A scheduler that has just started, remembering nothing, reaches the same answer.
    assert (app.portal.call(Scheduler(app.app.state.sessionmaker, clock).schedule_once))["created"] == 0
    assert len(jobs_of(app)) == 1


def test_a_class_whose_time_has_passed_is_not_opened_hours_later(app, clock):
    """After an outage the queue must not fill with the morning's classes."""
    a_class(app)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)

    clock.now = at_local(date(2026, 9, 20), "19:00") + GRACE + timedelta(minutes=1)
    counts = app.portal.call(scheduler.schedule_once)

    # The two stages due by now are past their window: the meeting, due a quarter of an hour before
    # the class, and the LMS session due on the hour. The follow-ups are not due yet, so they are
    # neither made nor missed - and they are never made for a class whose meeting was not held.
    assert counts == {"created": 0, "missed": 2}
    assert jobs_of(app) == []
    assert jobs_of(app, "class.run") == []


def test_a_class_is_still_opened_just_inside_the_window(app, clock):
    """A deployment that took a few minutes must not lose the class it was in the middle of."""
    a_class(app)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)

    clock.now = at_local(date(2026, 9, 20), "19:00") + GRACE - timedelta(minutes=1)

    assert (app.portal.call(scheduler.schedule_once))["created"] == 1


def test_a_coordinator_who_is_turned_off_has_no_classes_opened(app, clock):
    a_class(app, enabled=False)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    clock.now = at_local(date(2026, 9, 20), "19:00")

    assert (app.portal.call(scheduler.schedule_once))["created"] == 0
    assert jobs_of(app) == []


def test_a_class_with_no_account_chosen_is_left_alone_rather_than_guessed_at(app, clock):
    """Running under whichever account came to hand writes one coordinator's class up under
    another's name, and nothing downstream could tell."""
    a_class(app, with_account=False)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    clock.now = at_local(date(2026, 9, 20), "19:00")

    assert (app.portal.call(scheduler.schedule_once))["created"] == 0
    assert jobs_of(app) == []


def test_a_class_that_already_ran_is_not_opened_again(app, clock):
    plan, _ = a_class(app)
    run_sql(app.app.state.settings.database_url,
            "UPDATE class_plans SET status = 'opened' WHERE id = $1::uuid", uuid.UUID(plan))
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    clock.now = at_local(date(2026, 9, 20), "19:00")

    assert (app.portal.call(scheduler.schedule_once))["created"] == 0


def test_a_class_with_no_time_of_its_own_is_not_scheduled(app, clock):
    """The LMS lists some classes without a time. There is no moment to open one at."""
    url = app.app.state.settings.database_url
    plan, coordinator = a_class(app)
    run_sql(url, "UPDATE class_plans SET start_time = NULL WHERE id = $1::uuid", uuid.UUID(plan))
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    clock.now = at_local(date(2026, 9, 20), "19:00")

    assert (app.portal.call(scheduler.schedule_once))["created"] == 0


def test_a_class_time_is_read_in_cairo_not_in_utc():
    """A 19:00 class is 19:00 where the coordinator is. Read as UTC it would open three hours late
    in summer and two in winter, every day, and nothing in the logs would say why."""

    class Plan:
        session_date = date(2026, 9, 20)      # summer: Cairo is UTC+3
        start_time = "19:00"

    assert due_at(Plan(), STAGE) == datetime(2026, 9, 20, 16, 0, tzinfo=UTC)

    class Winter:
        session_date = date(2026, 1, 20)      # winter: UTC+2
        start_time = "19:00"

    assert due_at(Winter(), STAGE) == datetime(2026, 1, 20, 17, 0, tzinfo=UTC)


def test_the_key_a_class_is_created_under_is_the_class_and_the_stage():
    plan = uuid.uuid4()
    assert idempotency_key(plan, STAGE) == f"plan:{plan}:lms.run_session"
    assert idempotency_key(plan, STAGE) != idempotency_key(uuid.uuid4(), STAGE)


# ===================================================== opening the meeting, a quarter hour early


def test_the_meeting_opens_before_the_class_and_the_lms_session_on_the_hour(app, clock):
    """People arrive before the hour, and somebody has to be there to let them in. The dashboard is
    the record of what happened, so it is started when the class actually starts."""
    a_class(app, with_zoom=True)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)

    # A quarter of an hour before: the meeting, and only the meeting.
    clock.now = at_local(date(2026, 9, 20), "18:45")
    assert (app.portal.call(scheduler.schedule_once))["created"] == 1
    assert len(jobs_of(app, "class.run")) == 1
    assert jobs_of(app, "lms.run_session") == []

    payload = jobs_of(app, "class.run")[0]["payload"]
    assert payload["meetingUrl"] == "https://zoom.us/j/91473108490"
    assert payload["zoomAccountId"]          # named, never guessed
    assert payload["durationMinutes"] == 180

    # On the hour: the LMS session, and the meeting is not opened a second time.
    clock.now = at_local(date(2026, 9, 20), "19:00")
    assert (app.portal.call(scheduler.schedule_once))["created"] == 1
    assert len(jobs_of(app, "class.run")) == 1
    assert len(jobs_of(app, "lms.run_session")) == 1


def test_a_class_with_no_zoom_link_has_no_meeting_opened(app, clock):
    """The LMS lists when a class is, not how to join it. A class still waiting for its link is
    said about rather than turned into a job that would be refused."""
    a_class(app, with_zoom=False)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)

    clock.now = at_local(date(2026, 9, 20), "18:45")
    assert (app.portal.call(scheduler.schedule_once))["created"] == 0
    assert jobs_of(app, "class.run") == []

    # Its LMS session is still started: that half needs no link.
    clock.now = at_local(date(2026, 9, 20), "19:00")
    assert (app.portal.call(scheduler.schedule_once))["created"] == 1
    assert len(jobs_of(app, "lms.run_session")) == 1


def test_the_meeting_is_opened_once_however_many_passes_run(app, clock):
    a_class(app, with_zoom=True)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    clock.now = at_local(date(2026, 9, 20), "18:45")

    for _ in range(5):
        app.portal.call(scheduler.schedule_once)

    assert len(jobs_of(app, "class.run")) == 1


def test_a_held_class_gets_the_whole_windows_timeline(app, clock):
    """The Windows app's follow-up queue, stage for stage, at its own times."""
    plan, _ = a_class(app, with_zoom=True)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    day = date(2026, 9, 20)

    clock.now = at_local(day, "18:45")
    app.portal.call(scheduler.schedule_once)
    # The meeting is being held.
    run_sql(app.app.state.settings.database_url,
            "UPDATE jobs SET status = 'running' WHERE type = 'class.run'")

    for hhmm, job_type in (("19:00", "lms.run_session"), ("20:30", "lms.attendance"),
                           ("22:00", "lms.late_joiners"), ("22:10", "lms.complete"),
                           ("22:20", "zoom.report"), ("22:30", "zoom.recording")):
        clock.now = at_local(day, hhmm)
        assert jobs_of(app, job_type) == [], f"{job_type} before {hhmm}"
        app.portal.call(scheduler.schedule_once)
        rows = jobs_of(app, job_type)
        assert len(rows) == 1, f"{job_type} at {hhmm}"
        assert rows[0]["idempotency_key"] == f"plan:{plan}:{job_type}"

    # The Zoom stages carry the account whose pages they read; the LMS ones the account they write as.
    assert jobs_of(app, "zoom.report")[0]["payload"]["zoomAccountId"]
    assert jobs_of(app, "lms.late_joiners")[0]["payload"]["lmsAccountId"]


def test_a_class_whose_meeting_failed_gets_no_follow_ups(app, clock):
    """No attendance to write and no report to read: nothing is made to fail on the class card."""
    a_class(app, with_zoom=True)
    scheduler = Scheduler(app.app.state.sessionmaker, clock)
    day = date(2026, 9, 20)

    clock.now = at_local(day, "18:45")
    app.portal.call(scheduler.schedule_once)
    run_sql(app.app.state.settings.database_url,
            "UPDATE jobs SET status = 'failed' WHERE type = 'class.run'")

    for hhmm in ("19:00", "20:30", "22:00", "22:10", "22:20", "22:30"):
        clock.now = at_local(day, hhmm)
        app.portal.call(scheduler.schedule_once)

    # Run Session still happens: the LMS session is started whether or not the meeting opened.
    assert len(jobs_of(app, "lms.run_session")) == 1
    for job_type in ("lms.attendance", "lms.late_joiners", "lms.complete", "zoom.report", "zoom.recording"):
        assert jobs_of(app, job_type) == [], job_type

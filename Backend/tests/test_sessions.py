"""The Sessions page: every class and where it stands.

What is worth testing here is not the drawing but the reading. A stage's state is read off the
jobs that were made for the class, so the page and the queue can never disagree; a stage nothing
implements says so rather than showing as grey beside one that works; and a coordinator sees their
own classes and nobody else's however they ask.
"""

from __future__ import annotations

import json
import secrets
import uuid
from datetime import date, datetime, timedelta
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from central_backend.auth import AuthSettings
from central_backend.main import create_app
from central_backend.scheduling import CAIRO, STAGES, Scheduler, idempotency_key
from central_backend.sessions import SHAPES
from central_backend.user_data import SecretBox
from test_dashboard import ADMIN_PASSWORD, COORDINATOR_PASSWORD, DASH, login, make_user, run_sql

DAY = date(2026, 9, 20)


@pytest.fixture
def box() -> SecretBox:
    return SecretBox(secrets.token_bytes(32))


@pytest.fixture
def dash(settings, clock, box):  # noqa: ANN001, ANN201
    if not run_sql(settings.database_url, "SELECT 1 FROM users WHERE role = 'admin'"):
        make_user(settings.database_url, "admin", ADMIN_PASSWORD, role="admin", display_name="The Admin")
    app = create_app(settings, clock=clock, run_background=False, configure_logs=False,
                     auth_settings=AuthSettings(), dashboard_dist=Path("__no_dashboard_build__"),
                     secret_box=box)
    with TestClient(app) as client:
        yield client


def as_user(client: TestClient, username: str, password: str = COORDINATOR_PASSWORD) -> TestClient:
    client.cookies.clear()
    assert login(client, username, password).status_code == 200
    return client


def a_class(client: TestClient, *, group: str = "CAI5_AIS4_S7", username: str | None = None,
            at: str = "19:00", on: date = DAY, link: bool = True) -> tuple[str, str]:
    """A coordinator turned on, with both accounts and one planned class. (planId, coordinatorId)"""
    url = client.app.state.settings.database_url
    coordinator = make_user(url, username or f"omar{uuid.uuid4().hex[:8]}", COORDINATOR_PASSWORD)
    lms, zoom = uuid.uuid4(), uuid.uuid4()
    run_sql(url, """INSERT INTO lms_accounts (id, user_id, label, email, role, password_encrypted,
                                              active, created_at, updated_at)
                    VALUES ($1::uuid, $2::uuid, 'L', 'omar@lms.example.com', 'coordinator',
                            'x', true, now(), now())""", lms, uuid.UUID(coordinator))
    run_sql(url, """INSERT INTO zoom_accounts (id, user_id, account_id, label, zoom_email, group_name,
                                               active, created_at, updated_at)
                    VALUES ($1::uuid, $2::uuid, $3, 'Z', 'omar@zoom.example.com', $3, true, now(), now())""",
            zoom, uuid.UUID(coordinator), group)
    run_sql(url, """INSERT INTO run_delegations (coordinator_id, enabled, lms_account_id, zoom_account_id,
                                                 created_at, updated_at)
                    VALUES ($1::uuid, true, $2::uuid, $3::uuid, now(), now())""",
            uuid.UUID(coordinator), lms, zoom)
    plan = uuid.uuid4()
    run_sql(url, """INSERT INTO class_plans (id, coordinator_id, group_name, session_date, start_time,
                                             meeting_url, source, status, imported_at, created_at, updated_at)
                    VALUES ($1::uuid, $2::uuid, $3, $4::date, $5, $6, 'lms', 'planned',
                            now(), now(), now())""",
            plan, uuid.UUID(coordinator), group, on, at,
            "https://zoom.us/j/91473108490" if link else None)
    return str(plan), coordinator


def at_local(day: date, hhmm: str) -> datetime:
    hour, minute = (int(p) for p in hhmm.split(":"))
    return datetime(day.year, day.month, day.day, hour, minute, tzinfo=CAIRO)


def sessions(client: TestClient, **params) -> dict:  # noqa: ANN003
    response = client.get("/api/v1/dashboard/sessions", params=params or {"from": DAY.isoformat()})
    assert response.status_code == 200, response.text
    assert response.headers["cache-control"] == "no-store"
    return response.json()


def stage(row: dict, key: str) -> dict:
    return next(s for s in row["stages"] if s["key"] == key)


def test_a_class_carries_every_stage_of_the_card(dash, clock):
    a_class(dash)
    clock.now = at_local(DAY, "12:00")
    as_user(dash, "admin", ADMIN_PASSWORD)

    body = sessions(dash)
    assert len(body["classes"]) == 1
    row = body["classes"][0]

    # The same eleven the Windows app draws, in the same order.
    assert [s["key"] for s in row["stages"]] == [s.key for s in SHAPES]
    assert [s["label"] for s in row["stages"]] == [
        "Zoom", "Run", "Attendance", "Late joiners", "Complete", "Ended",
        "Zoom report", "Zoom recording", "Drive", "Material", "Assignment"]
    assert row["group"] == "CAI5_AIS4_S7"
    assert row["startTime"] == "19:00"


def test_a_stage_nothing_implements_says_so_rather_than_looking_unstarted(dash, clock):
    """Grey for "not built" and grey for "not yet" read the same, and one of them is a lie."""
    a_class(dash)
    clock.now = at_local(DAY, "12:00")
    as_user(dash, "admin", ADMIN_PASSWORD)
    row = sessions(dash)["classes"][0]

    for key in ("material", "assignment"):
        assert stage(row, key)["state"] == "missing", key
        assert "not built" in stage(row, key)["detail"]

    # And the ones that do exist are not called missing.
    for key in ("zoom", "run", "attendance", "complete", "ended", "drive", "zoomReport", "zoomRecording"):
        assert stage(row, key)["state"] != "missing", key


def test_a_stage_follows_the_job_that_was_made_for_it(dash, clock):
    """The page reads the queue rather than keeping its own record, so the two cannot disagree."""
    plan, _ = a_class(dash)
    clock.now = at_local(DAY, "12:00")
    as_user(dash, "admin", ADMIN_PASSWORD)

    # Before its time: later.
    assert stage(sessions(dash)["classes"][0], "zoom")["state"] == "later"

    # The scheduler makes the job a quarter of an hour before: waiting for a worker.
    clock.now = at_local(DAY, "18:45")
    dash.portal.call(Scheduler(dash.app.state.sessionmaker, clock).schedule_once)
    assert stage(sessions(dash)["classes"][0], "zoom")["state"] == "waiting"

    # A worker takes it, then finishes it.
    key = idempotency_key(uuid.UUID(plan), next(s for s in STAGES if s.job_type == "class.run"))
    url = dash.app.state.settings.database_url
    run_sql(url, "UPDATE jobs SET status = 'running' WHERE idempotency_key = $1", key)
    assert stage(sessions(dash)["classes"][0], "zoom")["state"] == "running"

    run_sql(url, "UPDATE jobs SET status = 'succeeded', finished_at = now() WHERE idempotency_key = $1", key)
    done = stage(sessions(dash)["classes"][0], "zoom")
    assert done["state"] == "done"
    assert done["detail"].startswith("Done ")


def test_a_failed_stage_carries_the_reason(dash, clock):
    plan, _ = a_class(dash)
    clock.now = at_local(DAY, "18:45")
    as_user(dash, "admin", ADMIN_PASSWORD)
    dash.portal.call(Scheduler(dash.app.state.sessionmaker, clock).schedule_once)

    key = idempotency_key(uuid.UUID(plan), next(s for s in STAGES if s.job_type == "class.run"))
    run_sql(dash.app.state.settings.database_url,
            """UPDATE jobs SET status = 'failed',
                               error = '{"code":"needsVerification","message":"Zoom asked for a code","retryable":false}'::jsonb
               WHERE idempotency_key = $1""", key)

    failed = stage(sessions(dash)["classes"][0], "zoom")
    assert failed["state"] == "failed"
    assert failed["detail"] == "Zoom asked for a code"
    assert failed["retryable"] is False
    assert sessions(dash)["classes"][0]["headline"] == "needsAttention"
    assert sessions(dash)["counters"]["needAttention"] == 1


def test_a_class_with_no_zoom_link_says_which_stage_cannot_run_and_why(dash, clock):
    """The LMS lists when a class is, not how to join it. The card has to say that plainly."""
    a_class(dash, link=False)
    clock.now = at_local(DAY, "12:00")
    as_user(dash, "admin", ADMIN_PASSWORD)
    row = sessions(dash)["classes"][0]

    blocked = stage(row, "zoom")
    assert blocked["state"] == "blocked"
    assert "no Zoom link" in blocked["detail"]
    assert row["headline"] == "blocked"
    assert sessions(dash)["counters"]["blocked"] == 1

    # Its LMS half is not blocked by a missing Zoom link.
    assert stage(row, "run")["state"] in ("later", "due")


def test_a_stage_whose_time_passed_with_nothing_run_is_not_shown_as_fine(dash, clock):
    a_class(dash)
    clock.now = at_local(DAY, "23:30")            # hours after the class
    as_user(dash, "admin", ADMIN_PASSWORD)

    zoom = stage(sessions(dash)["classes"][0], "zoom")

    assert zoom["state"] == "failed"
    assert "its time passed" in zoom["detail"]


def test_a_coordinator_sees_their_own_classes_and_nobody_elses(dash, clock):
    a_class(dash, group="CAI5_AIS4_S7", username="omar")
    a_class(dash, group="CAI5_AIS4_S8", username="sara")
    clock.now = at_local(DAY, "12:00")

    as_user(dash, "admin", ADMIN_PASSWORD)
    assert {r["group"] for r in sessions(dash)["classes"]} == {"CAI5_AIS4_S7", "CAI5_AIS4_S8"}

    as_user(dash, "omar")
    mine = sessions(dash)["classes"]
    assert {r["group"] for r in mine} == {"CAI5_AIS4_S7"}

    # Asking for somebody else's by name does not widen it.
    other = dash.get("/api/v1/dashboard/sessions",
                     params={"from": DAY.isoformat(), "group": "CAI5_AIS4_S8"}).json()
    assert other["classes"] == []


def test_the_counters_are_what_the_cards_say(dash, clock):
    a_class(dash, group="A", username="one")
    a_class(dash, group="B", username="two", link=False)
    clock.now = at_local(DAY, "12:00")
    as_user(dash, "admin", ADMIN_PASSWORD)

    body = sessions(dash)
    assert body["counters"]["classesToday"] == 2
    assert body["counters"]["blocked"] == 1
    assert sorted(body["groups"]) == ["A", "B"]


def test_the_window_defaults_to_a_week_and_is_bounded(dash, clock):
    a_class(dash, on=DAY)
    a_class(dash, on=DAY + timedelta(days=3), username="later")
    a_class(dash, on=DAY + timedelta(days=30), username="muchlater")
    clock.now = at_local(DAY, "12:00")
    as_user(dash, "admin", ADMIN_PASSWORD)

    week = sessions(dash)
    assert week["from"] == DAY.isoformat()
    assert len(week["classes"]) == 2          # today and in three days, not the one next month

    month = sessions(dash, **{"from": DAY.isoformat(), "to": (DAY + timedelta(days=40)).isoformat()})
    assert len(month["classes"]) == 3


def test_the_page_needs_a_signed_in_person(dash):
    dash.cookies.clear()
    assert dash.get("/api/v1/dashboard/sessions").status_code == 401


def test_ended_is_read_from_how_the_meeting_was_held(dash, clock):
    """There is no job to end a meeting: the one holding it ends it, and says whether it could."""
    plan, _ = a_class(dash)
    url = dash.app.state.settings.database_url
    clock.now = at_local(DAY, "18:45")
    dash.portal.call(Scheduler(dash.app.state.sessionmaker, clock).schedule_once)
    key = idempotency_key(uuid.UUID(plan), STAGES[0])
    as_user(dash, "admin", ADMIN_PASSWORD)

    assert stage(sessions(dash)["classes"][0], "ended")["state"] == "later"

    run_sql(url, "UPDATE jobs SET status = 'running' WHERE idempotency_key = $1", key)
    assert stage(sessions(dash)["classes"][0], "ended")["state"] == "waiting"

    # Held and closed.
    run_sql(url, """UPDATE jobs SET status = 'succeeded', finished_at = now(),
                    result = '{"endedTheMeeting": true}'::jsonb WHERE idempotency_key = $1""", key)
    assert stage(sessions(dash)["classes"][0], "ended")["state"] == "done"

    # Held, but left open: not a quiet tick.
    run_sql(url, """UPDATE jobs SET result = '{"endedTheMeeting": false,
                    "warning": "no End button (is this account the host?)"}'::jsonb WHERE idempotency_key = $1""", key)
    ended = stage(sessions(dash)["classes"][0], "ended")
    assert ended["state"] == "failed"
    assert "host" in ended["detail"]


def test_the_follow_ups_of_a_meeting_that_failed_say_so(dash, clock):
    """Not a row of red as their times pass: the scheduler makes none of them, and the card says why."""
    plan, _ = a_class(dash)
    url = dash.app.state.settings.database_url
    clock.now = at_local(DAY, "18:45")
    dash.portal.call(Scheduler(dash.app.state.sessionmaker, clock).schedule_once)
    run_sql(url, "UPDATE jobs SET status = 'failed' WHERE idempotency_key = $1",
            idempotency_key(uuid.UUID(plan), STAGES[0]))

    clock.now = at_local(DAY, "23:30")
    as_user(dash, "admin", ADMIN_PASSWORD)
    row = sessions(dash)["classes"][0]
    for key in ("attendance", "lateJoiners", "complete", "zoomReport", "zoomRecording"):
        assert stage(row, key)["state"] == "blocked", key
        assert stage(row, key)["detail"] == "the meeting was not held"
    assert stage(row, "ended")["detail"] == "the meeting was not held"


def test_live_sessions_show_the_worker_owned_meeting(dash, clock):
    """The web's Live page reads the same long class.run job the Windows Meetings page follows."""
    plan, _ = a_class(dash)
    clock.now = at_local(DAY, "18:45")
    dash.portal.call(Scheduler(dash.app.state.sessionmaker, clock).schedule_once)
    run_sql(dash.app.state.settings.database_url, "UPDATE jobs SET status = 'running' WHERE idempotency_key = $1",
            idempotency_key(uuid.UUID(plan), STAGES[0]))
    as_user(dash, "admin", ADMIN_PASSWORD)

    response = dash.get("/api/v1/dashboard/live")
    assert response.status_code == 200
    body = response.json()
    assert body["count"] == 1
    live = body["items"][0]
    assert (live["type"], live["status"], live["group"], live["attendanceSessionId"]) == (
        "class.run", "running", "CAI5_AIS4_S7", None)

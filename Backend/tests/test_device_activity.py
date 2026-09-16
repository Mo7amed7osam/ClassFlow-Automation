"""What each PC did by itself, reported to the server and read back on the dashboard."""

from __future__ import annotations

import uuid

import pytest

from conftest import client_headers, register
from test_dashboard import DASH, build, login


@pytest.fixture
def dash(settings, clock):  # noqa: ANN001, ANN201
    with build(settings, clock) as client:
        yield client


def event(**overrides) -> dict:  # noqa: ANN003
    body = {
        "eventId": f"win:{uuid.uuid4()}",
        "at": "2026-09-16T19:35:00+03:00",
        "kind": "lms.TakeAttendance",
        "outcome": "done",
        "group": "CAI5_AIS4_S7",
        "date": "2026-09-16",
        "summary": "Attendance taken on the LMS (21 present).",
        "detail": {"present": 21},
    }
    body.update(overrides)
    return body


def send(client, token: str, *events: dict):  # noqa: ANN001, ANN201
    return client.post("/api/v1/devices/activity", json={"events": list(events) or [event()]},
                       headers={"Authorization": f"Bearer {token}"})


def test_a_pc_reports_what_it_did_and_the_dashboard_shows_it(dash):  # noqa: ANN001
    assert login(dash).status_code == 200
    _, token = register(dash, name="Mona's laptop")
    done = event()

    assert send(dash, token, done).status_code == 200
    # The PC was away and sends the same note again: it is kept once, not twice.
    assert send(dash, token, done).json() == {"accepted": 1}

    items = dash.get("/api/v1/dashboard/activity").json()["items"]
    assert len(items) == 1
    assert items[0]["summary"] == "Attendance taken on the LMS (21 present)."
    assert items[0]["group"] == "CAI5_AIS4_S7"
    assert items[0]["outcome"] == "done"
    assert items[0]["device"] == "Mona's laptop"
    assert items[0]["detail"] == {"present": 21}


def test_only_a_device_may_report_and_a_time_zone_is_required(dash):  # noqa: ANN001
    assert login(dash).status_code == 200
    _, token = register(dash)
    # The n8n key speaks for no PC.
    assert dash.post("/api/v1/devices/activity", json={"events": [event()]}, headers=client_headers()).status_code == 403
    assert dash.post("/api/v1/devices/activity", json={"events": [event()]}).status_code == 401
    # The app's own validation answers 400 with what was wrong.
    assert send(dash, token, event(at="2026-09-16T19:35:00")).status_code == 400
    assert send(dash, token, event(outcome="maybe")).status_code == 400
    assert send(dash, token, event(unknown="x")).status_code == 400


def test_a_coordinator_sees_only_their_own_groups(dash, settings):  # noqa: ANN001
    from test_dashboard import ADMIN_PASSWORD, make_user, run_sql

    assert login(dash).status_code == 200
    _, token = register(dash)
    assert send(dash, token, event(group="CAI5_AIS4_S7"), event(eventId=f"win:{uuid.uuid4()}", group="CAI5_AIS4_S8")).status_code == 200

    make_user(settings.database_url, "omar", ADMIN_PASSWORD, role="coordinator", display_name="Omar")
    run_sql(settings.database_url, "INSERT INTO groups (id, name, created_at) VALUES ($1, 'CAI5_AIS4_S7', now()) ON CONFLICT DO NOTHING", uuid.uuid4())
    run_sql(settings.database_url, """
        INSERT INTO user_groups (user_id, group_id)
        SELECT u.id, g.id FROM users u, groups g WHERE u.username = 'omar' AND g.name = 'CAI5_AIS4_S7'
        ON CONFLICT DO NOTHING
        """)
    assert login(dash, "omar", ADMIN_PASSWORD).status_code == 200

    groups = {item["group"] for item in dash.get("/api/v1/dashboard/activity").json()["items"]}
    assert groups == {"CAI5_AIS4_S7"}

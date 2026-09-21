"""The two things a cloud worker asks for beyond its jobs: its LMS companion, and a class's attendance.

Both are handed to a device on its own token, and both are narrow: a companion is made once, and
attendance is read only through a job the device holds. These tests are the narrowness.
"""

from __future__ import annotations

import json
import uuid
from datetime import UTC, datetime

from conftest import register
from test_dashboard import run_sql

GROUP = "CAI5_AIS4_S7"
DAY = "2026-09-20"


def bearer(token: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token}"}


# ---------------------------------------------------------------------------------- the companion


def test_a_worker_enrols_its_lms_companion_with_a_token_the_server_gives_it(client):
    parent, token = register(client, "cloud-worker-1", ["zoom_web", "lms"])

    answer = client.post("/api/v1/agent/companion-enrollment", headers=bearer(token))
    assert answer.status_code == 201, answer.text
    body = answer.json()
    assert body["name"] == "cloud-worker-1-lms"
    assert answer.headers["cache-control"] == "no-store"

    # The token is an ordinary enrolment token: the companion registers the way any device does.
    companion, _ = register(client, body["name"], ["lms"], token=body["enrollmentToken"])
    assert companion != parent


def test_a_device_gets_one_companion_and_no_more(client):
    """A leaked device token must not become a way to mint devices."""
    _, token = register(client, "cloud-worker-1", ["zoom_web", "lms"])

    first = client.post("/api/v1/agent/companion-enrollment", headers=bearer(token)).json()
    # Unspent: a second one is refused while the first is waiting.
    assert client.post("/api/v1/agent/companion-enrollment", headers=bearer(token)).status_code == 409

    register(client, first["name"], ["lms"], token=first["enrollmentToken"])
    # Spent, and the companion is enrolled: still refused.
    again = client.post("/api/v1/agent/companion-enrollment", headers=bearer(token))
    assert again.status_code == 409
    assert "already has its LMS companion" in again.json()["details"]


def test_a_revoked_companion_can_be_replaced(client):
    """How an operator recovers a lane whose state volume was lost."""
    _, token = register(client, "cloud-worker-1", ["zoom_web", "lms"])
    first = client.post("/api/v1/agent/companion-enrollment", headers=bearer(token)).json()
    companion, _ = register(client, first["name"], ["lms"], token=first["enrollmentToken"])

    run_sql(client.app.state.settings.database_url,
            "UPDATE devices SET revoked_at = now() WHERE id = $1::uuid", uuid.UUID(companion))

    assert client.post("/api/v1/agent/companion-enrollment", headers=bearer(token)).status_code == 201


def test_a_companion_does_not_get_a_companion_of_its_own(client):
    _, token = register(client, "cloud-worker-1", ["zoom_web", "lms"])
    first = client.post("/api/v1/agent/companion-enrollment", headers=bearer(token)).json()
    _, companion_token = register(client, first["name"], ["lms"], token=first["enrollmentToken"])

    assert client.post("/api/v1/agent/companion-enrollment", headers=bearer(companion_token)).status_code == 409


def test_nobody_without_a_device_token_gets_a_companion(client):
    assert client.post("/api/v1/agent/companion-enrollment").status_code == 401
    assert client.post("/api/v1/agent/companion-enrollment", headers=bearer("zaad_nonsense")).status_code == 401


# ---------------------------------------------------------------------------------- attendance


def a_job(client, device_id: str, job_type: str = "lms.attendance", status: str = "running",
          start: str = "19:00") -> str:
    job_id = str(uuid.uuid4())
    payload = {"classPlanId": str(uuid.uuid4()), "group": GROUP, "date": DAY, "startTime": start,
               "coordinatorId": str(uuid.uuid4()), "lmsAccountId": str(uuid.uuid4()), "dryRun": False}
    run_sql(client.app.state.settings.database_url,
            """INSERT INTO jobs (id, type, device_id, payload, status, attempts, max_attempts,
                                 available_at, created_at, updated_at)
               VALUES ($1::uuid, $2, $3::uuid, $4::jsonb, $5, 1, 3, now(), now(), now())""",
            job_id, job_type, device_id, json.dumps(payload), status)
    return job_id


def a_roster(client, *names: str) -> None:
    url = client.app.state.settings.database_url
    for order, name in enumerate(names, start=1):
        run_sql(url, """INSERT INTO students (id, group_name, full_name, order_index, aliases, active,
                                              created_at, updated_at)
                        VALUES ($1::uuid, $2, $3, $4, '[]'::jsonb, true, now(), now())""",
                uuid.uuid4(), GROUP, name, order)


def a_snapshot(client, token: str, *names: str, start: str = "19:00") -> None:
    answer = client.post("/api/v1/attendance/snapshots", headers=bearer(token), json={
        "session": {"group": GROUP, "date": DAY, "startTime": start},
        "capturedAt": datetime(2026, 9, 20, 16, 30, tzinfo=UTC).isoformat(),
        "source": "web", "trigger": "interval", "participants": list(names)})
    assert answer.status_code == 201, answer.text


def test_the_lms_lane_reads_who_was_present_by_their_roster_names(client):
    """What the Windows app hands its attendance step: the roster names of the students found."""
    meeting_id, meeting_token = register(client, "cloud-worker-1", ["zoom_web"])
    lms_id, lms_token = register(client, "cloud-worker-1-lms", ["lms"])
    a_roster(client, "Mona Adel Hassan", "Omar Khaled Said", "Sara Nabil Fawzy")
    # The meeting lane saw two of them, under the names Zoom shows.
    a_snapshot(client, meeting_token, "Mona Adel Hassan", "Omar Khaled Said", "Mohab (Host)")

    job = a_job(client, lms_id)
    answer = client.get(f"/api/v1/agent/jobs/{job}/attendance", headers=bearer(lms_token))

    assert answer.status_code == 200, answer.text
    body = answer.json()
    assert body["present"] == ["Mona Adel Hassan", "Omar Khaled Said"]
    assert body["students"] == 3
    assert body["snapshots"] == 1
    assert meeting_id != lms_id


def test_a_class_nobody_collected_anything_for_is_refused_rather_than_written_as_absent(client):
    lms_id, lms_token = register(client, "cloud-worker-1-lms", ["lms"])
    a_roster(client, "Mona Adel Hassan")

    answer = client.get(f"/api/v1/agent/jobs/{a_job(client, lms_id)}/attendance", headers=bearer(lms_token))
    assert answer.status_code == 409
    assert "Nothing was collected" in answer.json()["details"]


def test_a_group_with_no_roster_is_named_rather_than_matched_to_nobody(client):
    _, meeting_token = register(client, "cloud-worker-1", ["zoom_web"])
    lms_id, lms_token = register(client, "cloud-worker-1-lms", ["lms"])
    a_snapshot(client, meeting_token, "Mona Adel Hassan")

    answer = client.get(f"/api/v1/agent/jobs/{a_job(client, lms_id)}/attendance", headers=bearer(lms_token))
    assert answer.status_code == 409
    assert "no students on the server" in answer.json()["details"]


def test_attendance_is_read_only_through_a_job_this_device_is_running(client):
    """A device learns a class's attendance only while it holds a job to write that class up."""
    _, meeting_token = register(client, "cloud-worker-1", ["zoom_web"])
    lms_id, lms_token = register(client, "cloud-worker-1-lms", ["lms"])
    other_id, other_token = register(client, "another-worker", ["lms"])
    a_roster(client, "Mona Adel Hassan")
    a_snapshot(client, meeting_token, "Mona Adel Hassan")

    theirs = a_job(client, other_id)
    # Another device's job: the same answer as no such job.
    assert client.get(f"/api/v1/agent/jobs/{theirs}/attendance", headers=bearer(lms_token)).status_code == 404
    assert client.get(f"/api/v1/agent/jobs/{uuid.uuid4()}/attendance", headers=bearer(lms_token)).status_code == 404

    # A finished job, or one that does not write attendance.
    done = a_job(client, lms_id, status="succeeded")
    assert client.get(f"/api/v1/agent/jobs/{done}/attendance", headers=bearer(lms_token)).status_code == 409
    other_stage = a_job(client, lms_id, job_type="lms.complete")
    assert client.get(f"/api/v1/agent/jobs/{other_stage}/attendance", headers=bearer(lms_token)).status_code == 409

    # And no token at all.
    assert client.get(f"/api/v1/agent/jobs/{theirs}/attendance").status_code == 401
    assert other_token  # the other device's own job stays readable to it
    assert client.get(f"/api/v1/agent/jobs/{theirs}/attendance", headers=bearer(other_token)).status_code == 200


def test_late_joiners_read_every_snapshot_of_the_class(client):
    """The correction three hours in sees whoever came after the first write-up."""
    _, meeting_token = register(client, "cloud-worker-1", ["zoom_web"])
    lms_id, lms_token = register(client, "cloud-worker-1-lms", ["lms"])
    a_roster(client, "Mona Adel Hassan", "Omar Khaled Said")
    a_snapshot(client, meeting_token, "Mona Adel Hassan")
    a_snapshot(client, meeting_token, "Mona Adel Hassan", "Omar Khaled Said")

    job = a_job(client, lms_id, job_type="lms.late_joiners")
    body = client.get(f"/api/v1/agent/jobs/{job}/attendance", headers=bearer(lms_token)).json()
    assert body["present"] == ["Mona Adel Hassan", "Omar Khaled Said"]
    assert body["snapshots"] == 2

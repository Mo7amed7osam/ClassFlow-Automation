"""Job creation, validation, status, idempotency and cancellation (the n8n side)."""

from __future__ import annotations

import uuid

import pytest

from conftest import DRIVE_LINK, client_headers, get_job, post_job, recording_payload


def test_creating_a_job_answers_202_with_its_id_and_queued(client):
    response = post_job(client)
    assert response.status_code == 202
    body = response.json()
    assert body["status"] == "queued"
    uuid.UUID(body["jobId"])
    assert response.headers["cache-control"] == "no-store"


def test_a_job_can_be_read_back_with_its_status(client):
    job_id = post_job(client).json()["jobId"]
    job = get_job(client, job_id)
    assert job["jobId"] == job_id
    assert job["type"] == "recording.process"
    assert job["status"] == "queued"
    assert job["deviceId"] is None
    assert job["payload"] == {**recording_payload(), "dryRun": False}
    assert job["result"] is None and job["error"] is None
    assert job["createdAt"].endswith("Z")
    assert job["startedAt"] is None and job["finishedAt"] is None


def test_an_unknown_job_is_404(client):
    assert client.get(f"/api/v1/jobs/{uuid.uuid4()}", headers=client_headers()).status_code == 404
    assert client.get("/api/v1/jobs/not-a-uuid", headers=client_headers()).status_code == 404


def test_the_payload_is_normalised(client):
    job_id = post_job(client, recording_payload(group="  CAI5_AIS4_S7 ", recordLink=f"  {DRIVE_LINK} ", startTime="18:30")).json()["jobId"]
    payload = get_job(client, job_id)["payload"]
    assert payload["group"] == "CAI5_AIS4_S7"
    assert payload["recordLink"] == DRIVE_LINK
    assert payload["startTime"] == "18:30"
    assert payload["replaceExisting"] is False and payload["dryRun"] is False


@pytest.mark.parametrize("payload, fragment", [
    ({"recordLink": DRIVE_LINK, "date": "2026-09-03"}, "'group' is required"),
    (recording_payload(group="a/b"), "'group' may hold"),
    (recording_payload(recordLink=None), "'recordLink' is required"),
    (recording_payload(recordLink="C:\\Recordings\\x.mp4"), "Google Drive link"),
    (recording_payload(recordLink="http://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"), "Google Drive link"),
    (recording_payload(recordLink="https://drive.google.com.evil.example/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"), "Google Drive link"),
    (recording_payload(recordLink="https://user:pw@drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"), "Google Drive link"),
    (recording_payload(recordLink="https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"), "Google Drive link"),
    (recording_payload(date=None), "'date' is required"),
    (recording_payload(date=""), "'date' is required"),
    (recording_payload(date="03/09/2026"), "yyyy-MM-dd"),
    (recording_payload(date="2026-02-30"), "yyyy-MM-dd"),
    (recording_payload(startTime="7pm"), "HH:mm"),
    (recording_payload(replaceExisting="false"), "true or false"),
    ({**recording_payload(), "headed": True}, "Unknown field"),
    ({**recording_payload(), "profile": "s7"}, "Unknown field"),
])
def test_a_bad_payload_is_refused_with_a_reason(client, payload, fragment):
    response = post_job(client, payload)
    assert response.status_code == 400, response.text
    body = response.json()
    assert body["error"] == "Invalid request"
    assert fragment in body["details"]


def test_an_unknown_job_type_or_extra_field_is_refused(client):
    r = client.post("/api/v1/jobs", json={"type": "zoom.join", "payload": recording_payload()}, headers=client_headers())
    assert r.status_code == 400 and "Unknown job type" in r.json()["details"]
    r = client.post("/api/v1/jobs", json={"type": "recording.process", "payload": recording_payload(), "x": 1},
                    headers=client_headers())
    assert r.status_code == 400


def test_a_validation_error_never_echoes_what_was_sent(client):
    r = client.post("/api/v1/jobs", json={"type": ["recording.process"], "payload": DRIVE_LINK}, headers=client_headers())
    assert r.status_code == 400
    assert DRIVE_LINK not in r.text
    assert "1AbCdEfGhIjKlMnOpQrStUvWxYz012345" not in r.text


def test_a_repeated_idempotency_key_returns_the_same_job(client):
    first = post_job(client, key="sheet-row-42")
    again = post_job(client, key="sheet-row-42")
    assert first.status_code == 202
    assert again.status_code == 200
    assert again.json()["jobId"] == first.json()["jobId"]
    assert again.json()["duplicate"] is True


def test_an_idempotency_key_reused_for_a_different_job_is_a_conflict(client):
    post_job(client, key="sheet-row-42")
    conflict = post_job(client, recording_payload(group="OTHER_GROUP"), key="sheet-row-42")
    assert conflict.status_code == 409


def test_without_an_idempotency_key_each_post_is_a_new_job(client):
    assert post_job(client).json()["jobId"] != post_job(client).json()["jobId"]


def test_a_queued_job_can_be_cancelled_and_is_then_never_assigned(client):
    job_id = post_job(client).json()["jobId"]
    r = client.post(f"/api/v1/jobs/{job_id}/cancel", headers=client_headers())
    assert r.status_code == 200 and r.json()["status"] == "cancelled"
    assert get_job(client, job_id)["finishedAt"] is not None
    again = client.post(f"/api/v1/jobs/{job_id}/cancel", headers=client_headers())
    assert again.status_code == 200 and again.json()["status"] == "cancelled"   # repeating it is harmless
    assert client.post(f"/api/v1/jobs/{uuid.uuid4()}/cancel", headers=client_headers()).status_code == 404


def test_health_needs_no_key_and_reveals_nothing(client):
    r = client.get("/health")
    assert r.status_code == 200
    assert r.json() == {"status": "ok"}


# ============================================================ the stages of a class

PLAN = "11111111-2222-3333-4444-555555555555"
COORDINATOR = "66666666-7777-8888-9999-000000000000"
LMS_ACCOUNT = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
ZOOM_ACCOUNT = "cccccccc-dddd-eeee-ffff-aaaaaaaaaaaa"
SOMEBODY_ELSE = "99999999-8888-7777-6666-555555555555"


@pytest.fixture
def a_class(client):  # noqa: ANN001, ANN201
    """The coordinator this class belongs to, with an LMS and a Zoom account, and a second
    coordinator with an account of their own.

    These are rows rather than ids made up in the test because the server now checks that a class
    names its own coordinator's accounts, and a check against the database has to have one.
    """
    from test_dashboard import run_sql

    url = client.app.state.settings.database_url
    for user_id, username in ((COORDINATOR, "omar"), (SOMEBODY_ELSE, "mona")):
        run_sql(url, "INSERT INTO users (id, username, display_name, password_hash, role, status, "
                     "created_at, updated_at) VALUES ($1::uuid, $2, $2, 'x', 'coordinator', 'active', "
                     "now(), now())", user_id, username)
    run_sql(url, "INSERT INTO lms_accounts (id, user_id, label, email, role, password_encrypted, active, "
                 "created_at, updated_at) VALUES ($1::uuid, $2::uuid, 'main', 'omar@lms.example.com', "
                 "'coordinator', 'x', true, now(), now())", LMS_ACCOUNT, COORDINATOR)
    run_sql(url, "INSERT INTO zoom_accounts (id, user_id, account_id, label, zoom_email, active, "
                 "created_at, updated_at) VALUES ($1::uuid, $2::uuid, 'omar', 'main', "
                 "'omar@zoom.example.com', true, now(), now())", ZOOM_ACCOUNT, COORDINATOR)
    # Mona's own, for the test that a class may not borrow it.
    run_sql(url, "INSERT INTO lms_accounts (id, user_id, label, email, role, password_encrypted, active, "
                 "created_at, updated_at) VALUES (gen_random_uuid(), $1::uuid, 'hers', "
                 "'mona@lms.example.com', 'coordinator', 'x', true, now(), now())", SOMEBODY_ELSE)
    return url


def stage_payload(**overrides):
    payload = {
        "classPlanId": PLAN,
        "group": "CAI5_AIS4_S7",
        "date": "2026-09-20",
        "startTime": "19:00",
        "coordinatorId": COORDINATOR,
        "meetingUrl": "https://zoom.us/j/1234567890",
    }
    payload.update(overrides)
    return {k: v for k, v in payload.items() if v is not None}


def post_stage(client, job_type, payload=None, key=None):  # noqa: ANN001, ANN201
    headers = client_headers()
    if key is not None:
        headers["Idempotency-Key"] = key
    return client.post("/api/v1/jobs", json={"type": job_type, "payload": payload or stage_payload()},
                       headers=headers)


@pytest.mark.parametrize("job_type", ["class.run", "class.end", "zoom.report", "zoom.recording"])
def test_every_zoom_stage_of_a_class_is_accepted(client, a_class, job_type):
    response = post_stage(client, job_type)
    assert response.status_code == 202, response.text
    job = get_job(client, response.json()["jobId"])
    assert job["type"] == job_type
    assert job["status"] == "queued"
    assert job["payload"]["classPlanId"] == PLAN
    assert job["payload"]["coordinatorId"] == COORDINATOR
    assert job["payload"]["dryRun"] is False


@pytest.mark.parametrize("job_type", ["lms.run_session", "lms.attendance", "lms.complete"])
def test_an_lms_stage_must_say_whose_sign_in_writes_it_up(client, a_class, job_type):
    """The rule that matters: a class is written up under its own coordinator's account, never
    under whoever the machine last used."""
    refused = post_stage(client, job_type, stage_payload(lmsAccountId=None))
    assert refused.status_code == 400
    assert "lmsAccountId" in refused.json()["details"]

    accepted = post_stage(client, job_type, stage_payload(lmsAccountId=LMS_ACCOUNT))
    assert accepted.status_code == 202, accepted.text
    assert get_job(client, accepted.json()["jobId"])["payload"]["lmsAccountId"] == LMS_ACCOUNT


def test_a_class_may_not_name_another_coordinators_account(client, a_class):
    """Whose account it is, not just whether the id is the right shape.

    Accepting this would mean the payload decides: the class says it is Omar's, the account is
    Mona's, and the stage runs under Mona's sign-in. It is refused where the class is made, so
    whoever made it is told, rather than at class time when nobody is watching.
    """
    from test_dashboard import run_sql

    hers = run_sql(a_class, "SELECT id FROM lms_accounts WHERE user_id = $1::uuid", SOMEBODY_ELSE)[0]["id"]

    refused = post_stage(client, "lms.run_session", stage_payload(lmsAccountId=str(hers)))
    assert refused.status_code == 400, refused.text
    assert "different coordinator" in refused.json()["details"]

    # And an account that is not there at all is named as missing rather than silently accepted.
    gone = post_stage(client, "lms.run_session", stage_payload(lmsAccountId=str(uuid.uuid4())))
    assert gone.status_code == 400
    assert "no LMS account" in gone.json()["details"]


def test_opening_a_meeting_needs_somewhere_to_open(client):
    refused = post_stage(client, "class.run", stage_payload(meetingUrl=None))
    assert refused.status_code == 400
    assert "meetingUrl" in refused.json()["details"]

    # Ending one does not: it finds the meeting the worker already has.
    assert post_stage(client, "class.end", stage_payload(meetingUrl=None)).status_code == 202


def test_a_meeting_is_held_for_a_bounded_time(client):
    """A worker that lost touch with the backend must not sit in an empty meeting for ever,
    holding the only slot it has."""
    held = post_stage(client, "class.run")
    assert get_job(client, held.json()["jobId"])["payload"]["durationMinutes"] == 180

    given = post_stage(client, "class.run", stage_payload(durationMinutes=90))
    assert get_job(client, given.json()["jobId"])["payload"]["durationMinutes"] == 90

    for bad in (0, -5, 601, "90", 12.5):
        assert post_stage(client, "class.run", stage_payload(durationMinutes=bad)).status_code == 400


def test_a_class_always_says_whose_it_is(client):
    for missing in ("coordinatorId", "classPlanId"):
        refused = post_stage(client, "class.end", stage_payload(**{missing: None}))
        assert refused.status_code == 400, f"{missing} was accepted as missing"
        assert missing in refused.json()["details"]


@pytest.mark.parametrize("payload,fragment", [
    (stage_payload(classPlanId="not-a-uuid"), "UUID"),
    (stage_payload(coordinatorId="1234"), "UUID"),
    (stage_payload(date="20-09-2026"), "yyyy-MM-dd"),
    (stage_payload(startTime="7pm"), "HH:mm"),
    (stage_payload(group=""), "'group' is required"),
    (stage_payload(meetingUrl="http://zoom.us/j/1"), "https"),
    (stage_payload(meetingUrl="https://user:pw@zoom.us/j/1"), "user name"),
])
def test_a_bad_stage_payload_is_refused_with_a_reason(client, payload, fragment):
    response = post_stage(client, "class.end", payload)
    assert response.status_code == 400
    assert fragment in response.json()["details"]


def test_a_stage_payload_refuses_a_field_it_does_not_know(client):
    response = post_stage(client, "class.end", stage_payload(**{}) | {"engine": "web"})
    assert response.status_code == 400
    assert "engine" in response.json()["details"]


def test_the_same_stage_of_the_same_class_is_created_once(client):
    """Idempotency is what stops a retried caller opening the same meeting twice."""
    first = post_stage(client, "class.run", key="class-run-plan-1")
    second = post_stage(client, "class.run", key="class-run-plan-1")
    assert first.status_code == 202 and second.status_code == 200
    assert first.json()["jobId"] == second.json()["jobId"]


def test_a_stage_goes_only_to_a_device_that_can_run_it(client):
    """A recording-only agent must never be handed a class stage, and the routing is by
    capability, not by hope."""
    from central_backend.validation import JOB_TYPES

    assert JOB_TYPES["recording.process"] == "recording_processing"
    assert {JOB_TYPES[t] for t in ("class.run", "class.end", "zoom.report")} == {"zoom_web"}
    assert {JOB_TYPES[t] for t in ("lms.run_session", "lms.attendance", "lms.complete")} == {"lms"}

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

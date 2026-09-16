"""Dashboard recording operations: edit, attach to the LMS through a job, and the job's outcome
written back to the recording."""

from __future__ import annotations

import asyncio
import uuid

import asyncpg
import pytest

from conftest import (
    CLIENT_KEY,
    DRIVE_ID,
    DRIVE_LINK,
    client_headers,
    connect,
    expect,
    heartbeat,
    hello,
    post_job,
    recording_payload,
    register,
    run_job,
)
from test_dashboard import DASH, build, login, set_lms_status

ZOOM_LINK = "https://us06web.zoom.us/rec/share/FAKE-TOKEN-FOR-TESTS.NotARealLink"
OTHER_DRIVE_LINK = "https://drive.google.com/file/d/1ZyXwVuTsRqPoNmLkJiHgFeDcBa987654/view"


@pytest.fixture
def dash(settings, clock):  # noqa: ANN001, ANN201
    with build(settings, clock) as client:
        yield client


def make_recording(client, **overrides) -> str:  # noqa: ANN001, ANN003
    body = {"group": "CAI5_AIS4_S7", "date": "2026-09-11", "startTime": "18:00", "fileName": "session.mp4",
            "type": "recording", "link": DRIVE_LINK}
    body.update(overrides)
    body = {k: v for k, v in body.items() if v is not None}
    response = client.post("/api/v1/recordings/sync", json=body, headers=client_headers())
    assert response.status_code in (200, 201), response.text
    return response.json()["recording"]["id"]


def edit(client, recording_id: str, body: dict, headers=DASH):  # noqa: ANN001, ANN201
    return client.patch(f"/api/v1/dashboard/recordings/{recording_id}", json=body, headers=headers)


def attach(client, recording_id: str, body: dict | None = None, headers=DASH):  # noqa: ANN001, ANN201
    kwargs = {"json": body} if body is not None else {}
    return client.post(f"/api/v1/dashboard/recordings/{recording_id}/attach", headers=headers, **kwargs)


def details(client, recording_id: str) -> dict:  # noqa: ANN001
    response = client.get(f"/api/v1/dashboard/recordings/{recording_id}")
    assert response.status_code == 200, response.text
    return response.json()


def query(client, sql: str, *args) -> list:  # noqa: ANN001, ANN002
    url = client.app.state.settings.database_url.replace("postgresql+asyncpg://", "postgresql://")

    async def run() -> list:
        conn = await asyncpg.connect(url)
        try:
            return await conn.fetch(sql, *args)
        finally:
            await conn.close()

    return asyncio.run(run())


def audit_rows(client, recording_id: str) -> list:  # noqa: ANN001
    return query(client, "SELECT username, action, recording_id, details, created_at FROM admin_audit_log "
                         "WHERE recording_id = $1 ORDER BY id", uuid.UUID(recording_id))


def job_count(client) -> int:  # noqa: ANN001
    return query(client, "SELECT count(*) AS n FROM jobs")[0]["n"]


# =========================================================================== edit


def test_editing_needs_the_dashboard_login_and_header(dash):
    rid = make_recording(dash)
    assert edit(dash, rid, {"fileName": "x.mp4"}).status_code == 401                              # no session
    assert edit(dash, rid, {"fileName": "x.mp4"}, headers={**DASH, "X-API-Key": CLIENT_KEY}).status_code == 401
    login(dash)
    assert edit(dash, rid, {"fileName": "x.mp4"}, headers={}).status_code == 403                 # no header
    assert details(dash, rid)["recording"]["fileName"] == "session.mp4"
    assert audit_rows(dash, rid) == []


def test_an_edit_changes_only_the_fields_sent_and_is_audited(dash, clock):
    rid = make_recording(dash)
    login(dash)
    clock.advance(60)
    response = edit(dash, rid, {"fileName": "session-final.mp4", "startTime": "18:30"})
    assert response.status_code == 200, response.text
    body = response.json()
    assert (body["id"], body["group"], body["date"]) == (rid, "CAI5_AIS4_S7", "2026-09-11")
    assert (body["fileName"], body["startTime"], body["source"], body["lmsStatus"]) == ("session-final.mp4", "18:30", "drive", "pending")
    assert body["driveLink"] == DRIVE_LINK                                                         # untouched
    assert body["updatedAt"] == "2026-09-12T10:01:00Z"
    assert body["changed"] == ["fileName", "startTime"]
    assert body["linkStatus"]["label"] == "Drive · pending"

    [row] = audit_rows(dash, rid)
    assert (row["username"], row["action"], str(row["recording_id"])) == ("admin", "recording.update", rid)
    assert row["created_at"].isoformat().startswith("2026-09-12T10:01:00")
    assert '"fileName"' in row["details"] and '"startTime"' in row["details"]


def test_an_invalid_link_is_refused_and_nothing_changes(dash):
    rid = make_recording(dash)
    login(dash)
    for bad in ("https://example.com/video.mp4", "C:\\Recordings\\x.mp4", "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"):
        response = edit(dash, rid, {"link": bad})
        assert response.status_code == 400
        assert "'link' must be" in response.json()["details"]
    assert details(dash, rid)["recording"]["driveLink"] == DRIVE_LINK
    assert audit_rows(dash, rid) == []


def test_a_new_link_resets_the_lms_status_to_pending(dash, clock):
    rid = make_recording(dash)
    set_lms_status(dash, rid, "attached")
    login(dash)
    clock.advance(30)
    body = edit(dash, rid, {"link": ZOOM_LINK}).json()
    assert (body["zoomLink"], body["driveLink"], body["source"]) == (ZOOM_LINK, None, "zoom")
    assert body["lmsStatus"] == "pending" and body["lmsUpdatedAt"] == "2026-09-12T10:00:30Z"
    assert body["changed"] == ["driveLink", "lmsStatus", "zoomLink"]
    assert body["linkStatus"]["label"] == "Zoom only · pending"

    [row] = audit_rows(dash, rid)
    assert "rec/share" not in row["details"] and "FAKE-TOKEN" not in row["details"]            # a preview only
    assert '"lmsStatus": "pending"' in row["details"]


def test_lms_status_can_be_set_by_hand_and_moving_onto_another_session_is_a_conflict(dash):
    rid = make_recording(dash)
    make_recording(dash, date="2026-09-12")
    login(dash)
    assert edit(dash, rid, {"lmsStatus": "attached"}).json()["lmsStatus"] == "attached"
    assert edit(dash, rid, {"lmsStatus": "done"}).status_code == 400
    assert edit(dash, rid, {"date": "2026-09-12"}).status_code == 409
    assert edit(dash, rid, {"driveLink": DRIVE_LINK}).status_code == 400                         # not an allowed field
    assert edit(dash, str(uuid.uuid4()), {"fileName": "x"}).status_code == 404
    assert edit(dash, "not-an-id", {"fileName": "x"}).status_code == 404


def test_an_edit_that_changes_nothing_writes_no_audit(dash):
    rid = make_recording(dash)
    login(dash)
    body = edit(dash, rid, {"fileName": "session.mp4"}).json()
    assert body["changed"] == []
    assert audit_rows(dash, rid) == []


# =========================================================================== attach


def test_attaching_needs_the_dashboard_login_and_header(dash):
    rid = make_recording(dash)
    assert attach(dash, rid, {"dryRun": True}).status_code == 401
    assert attach(dash, rid, {"dryRun": True}, headers={**DASH, "X-API-Key": CLIENT_KEY}).status_code == 401
    login(dash)
    assert attach(dash, rid, {"dryRun": True}, headers={}).status_code == 403
    assert job_count(dash) == 0


@pytest.mark.parametrize("link, reason", [(None, "noLink"), (ZOOM_LINK, "zoomOnly")])
def test_a_recording_without_a_drive_link_cannot_be_attached(dash, link, reason):
    rid = make_recording(dash, link=link)
    login(dash)
    response = attach(dash, rid, {"dryRun": True})
    assert response.status_code == 409
    body = response.json()
    assert body["error"] == "Cannot attach" and body["details"]["reason"] == reason
    assert "Drive link" in body["details"]["message"]
    assert job_count(dash) == 0


def test_a_dry_run_attach_creates_the_same_job_the_jobs_api_would(dash):
    rid = make_recording(dash)
    login(dash)
    response = attach(dash, rid, {"replaceExisting": False, "dryRun": True})
    assert response.status_code == 202, response.text
    body = response.json()
    assert body["recordingId"] == rid and body["status"] == "queued" and body["dryRun"] is True
    uuid.UUID(body["jobId"])

    job = dash.get(f"/api/v1/jobs/{body['jobId']}", headers=client_headers()).json()               # the jobs API, as n8n sees it
    assert job["type"] == "recording.process" and job["status"] == "queued"
    assert job["payload"] == {"group": "CAI5_AIS4_S7", "recordLink": DRIVE_LINK, "date": "2026-09-11",
                              "startTime": "18:00", "replaceExisting": False, "dryRun": True}
    assert "recordingId" not in job                                                                 # its answer is unchanged


def test_the_recording_id_is_stored_on_the_job_and_shown_on_the_recording(dash):
    rid = make_recording(dash)
    login(dash)
    job_id = attach(dash, rid, {"dryRun": True}).json()["jobId"]

    [row] = query(dash, "SELECT recording_id FROM jobs WHERE id = $1", uuid.UUID(job_id))
    assert str(row["recording_id"]) == rid
    [audit] = audit_rows(dash, rid)
    assert audit["action"] == "recording.attach" and job_id in audit["details"]

    info = details(dash, rid)
    assert info["recording"]["lastJob"]["jobId"] == job_id
    assert [j["jobId"] for j in info["jobs"]] == [job_id]
    assert info["jobs"][0]["dryRun"] is True and info["jobs"][0]["recordingId"] == rid
    assert info["audit"][0]["action"] == "recording.attach" and info["audit"][0]["username"] == "admin"
    listed = dash.get("/api/v1/dashboard/recordings").json()["items"][0]
    assert listed["lastJob"]["jobId"] == job_id and listed["lastJob"]["status"] == "queued"


def test_attach_defaults_to_a_dry_run_and_refuses_a_second_job_while_one_is_open(dash):
    rid = make_recording(dash)
    login(dash)
    first = attach(dash, rid)                                                                       # no body at all
    assert first.status_code == 202 and first.json()["dryRun"] is True
    second = attach(dash, rid, {"dryRun": False})
    assert second.status_code == 409
    assert second.json()["details"]["reason"] == "jobInProgress"
    assert second.json()["details"]["jobId"] == first.json()["jobId"]
    assert job_count(dash) == 1


def test_attach_refuses_unknown_recordings_and_fields(dash):
    login(dash)
    assert attach(dash, str(uuid.uuid4()), {"dryRun": True}).status_code == 404
    rid = make_recording(dash)
    assert attach(dash, rid, {"dryRun": True, "recordLink": DRIVE_LINK}).status_code == 400
    for loose in ("yes", "false", 0, 1):                                                           # booleans only
        assert attach(dash, rid, {"dryRun": loose}).status_code == 400
    assert job_count(dash) == 0


# =========================================================================== the job's outcome


def agent(client):  # noqa: ANN001, ANN201
    """A connected, idle agent (the context manager's socket)."""
    _, token = register(client, "PC-A")
    ws = connect(client, token)
    return ws


def run_to_failure(ws, error: dict) -> str:  # noqa: ANN001
    assign = expect(ws, "job.assign")
    job_id = assign["jobId"]
    ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
    ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
    ws.send_json({"type": "job.failed", "jobId": job_id, "error": error}); expect(ws, "ack")
    return job_id


def test_a_successful_attach_marks_the_recording_attached(dash, clock):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws); heartbeat(ws)
        clock.advance(5)
        job_id = attach(dash, rid, {"dryRun": False}).json()["jobId"]
        clock.advance(20)
        assert run_job(ws, {"alreadyExists": False, "message": "Recording link attached successfully."}) == job_id
        heartbeat(ws)
    recording = details(dash, rid)["recording"]
    assert recording["lmsStatus"] == "attached"
    assert recording["lmsUpdatedAt"] == "2026-09-12T10:00:25Z"
    assert recording["linkStatus"]["label"] == "Drive · on LMS"
    assert recording["lastJob"]["status"] == "succeeded"


def test_a_failed_attach_marks_the_recording_failed(dash, clock):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws); heartbeat(ws)
        attach(dash, rid, {"dryRun": False})
        clock.advance(10)
        run_to_failure(ws, {"code": "sessionNotFinished", "message": "Not finished yet.", "retryable": False})
        heartbeat(ws)
    recording = details(dash, rid)["recording"]
    assert recording["lmsStatus"] == "failed"
    assert recording["lmsUpdatedAt"] == "2026-09-12T10:00:10Z"
    assert recording["lastJob"]["errorCode"] == "sessionNotFinished"


def test_a_dry_run_never_changes_the_lms_status(dash):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws); heartbeat(ws)
        attach(dash, rid, {"dryRun": True})
        run_job(ws, {"alreadyExists": False, "dryRun": True})
        heartbeat(ws)
    recording = details(dash, rid)["recording"]
    assert recording["lmsStatus"] == "pending" and recording["lmsUpdatedAt"] is None
    assert recording["lastJob"]["status"] == "succeeded" and recording["lastJob"]["dryRun"] is True


def test_a_retryable_failure_does_not_mark_the_recording_failed_yet(dash):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws); heartbeat(ws)
        attach(dash, rid, {"dryRun": False})
        run_to_failure(ws, {"code": "busy", "message": "Dashboard busy.", "retryable": True, "retryAfterSeconds": 60})
        heartbeat(ws)
    recording = details(dash, rid)["recording"]
    assert recording["lmsStatus"] == "pending"
    assert recording["lastJob"]["status"] == "queued"                                               # waiting to be retried


def test_a_link_changed_while_the_job_ran_is_not_marked_attached(dash):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws); heartbeat(ws)
        attach(dash, rid, {"dryRun": False})
        edit(dash, rid, {"link": OTHER_DRIVE_LINK})                                                 # the job carries the old link
        run_job(ws)
        heartbeat(ws)
    recording = details(dash, rid)["recording"]
    assert recording["driveLink"] == OTHER_DRIVE_LINK and recording["lmsStatus"] == "pending"


def test_an_attach_whose_agent_vanished_marks_the_recording_failed(dash, clock):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws); heartbeat(ws)
        attach(dash, rid, {"dryRun": False})
        job_id = expect(ws, "job.assign")["jobId"]
        ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
        ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
    clock.advance(1900)
    assert dash.portal.call(dash.app.state.sweeper.sweep_once)["lost"] == 1
    recording = details(dash, rid)["recording"]
    assert recording["lmsStatus"] == "failed" and recording["lastJob"]["errorCode"] == "agentLost"


def test_jobs_from_the_jobs_api_never_touch_recordings(dash):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws); heartbeat(ws)
        post_job(dash, recording_payload(group="CAI5_AIS4_S7", date="2026-09-11"))                  # n8n's own job, same session
        run_job(ws)
        heartbeat(ws)
    recording = details(dash, rid)["recording"]
    assert recording["lmsStatus"] == "pending" and recording["lastJob"] is None
    assert DRIVE_ID not in str(audit_rows(dash, rid))


# =========================================================================== cancel


def cancel(client, recording_id: str, headers=DASH):  # noqa: ANN001, ANN201
    return client.post(f"/api/v1/dashboard/recordings/{recording_id}/cancel", headers=headers)


def test_cancelling_needs_the_dashboard_login_and_header(dash):
    rid = make_recording(dash)
    assert cancel(dash, rid).status_code == 401
    login(dash)
    job_id = attach(dash, rid, {"dryRun": True}).json()["jobId"]
    assert cancel(dash, rid, headers={}).status_code == 403
    assert query(dash, "SELECT status FROM jobs WHERE id = $1", uuid.UUID(job_id))[0]["status"] == "queued"


def test_a_waiting_attach_job_can_be_cancelled_and_attached_again(dash):
    rid = make_recording(dash)
    login(dash)
    job_id = attach(dash, rid, {"dryRun": False, "replaceExisting": True}).json()["jobId"]    # nobody to run it: queued
    response = cancel(dash, rid)
    assert response.status_code == 200, response.text
    assert response.json() == {"recordingId": rid, "jobId": job_id, "status": "cancelled"}

    job = dash.get(f"/api/v1/jobs/{job_id}", headers=client_headers()).json()               # as n8n sees it
    assert job["status"] == "cancelled" and job["finishedAt"] is not None
    events = [r["event_type"] for r in query(dash, "SELECT event_type FROM job_events WHERE job_id = $1 ORDER BY id", uuid.UUID(job_id))]
    assert events[-1] == "cancelled"
    info = details(dash, rid)
    assert info["recording"]["lmsStatus"] == "pending"                                      # nothing reached the LMS
    assert info["recording"]["lastJob"]["status"] == "cancelled"
    assert [(a["action"], a["username"]) for a in info["audit"]][:2] == [("recording.cancel", "admin"), ("recording.attach", "admin")]
    assert job_id in str(info["audit"][0]["details"])

    assert cancel(dash, rid).json()["details"]["reason"] == "noOpenJob"                     # nothing left to cancel
    assert attach(dash, rid, {"dryRun": True}).status_code == 202                            # free to attach again


def test_a_job_an_agent_has_taken_is_not_cancelled(dash):
    rid = make_recording(dash)
    login(dash)
    with agent(dash) as ws:
        hello(ws)
        job_id = attach(dash, rid, {"dryRun": True}).json()["jobId"]
        assert expect(ws, "job.assign")["jobId"] == job_id                                   # handed to the agent
        refused = cancel(dash, rid)
        assert refused.status_code == 409
        assert refused.json()["details"]["reason"] == "alreadyStarted" and refused.json()["details"]["jobId"] == job_id
        assert query(dash, "SELECT status FROM jobs WHERE id = $1", uuid.UUID(job_id))[0]["status"] == "assigned"
    assert [a["action"] for a in audit_rows(dash, rid)] == ["recording.attach"]


def test_cancel_refuses_unknown_recordings_and_recordings_without_a_job(dash):
    login(dash)
    assert cancel(dash, str(uuid.uuid4())).status_code == 404
    assert cancel(dash, "not-an-id").status_code == 404
    rid = make_recording(dash)
    response = cancel(dash, rid)
    assert response.status_code == 409 and response.json()["details"]["reason"] == "noOpenJob"
    assert audit_rows(dash, rid) == []


# =========================================================================== a PC joins by itself


def test_a_signed_in_person_can_enroll_their_own_pc_and_register_it(dash):  # noqa: ANN001
    """Without this a coordinator's PC waits for a hand-made token and sends nothing at all."""
    assert login(dash).status_code == 200
    response = dash.post("/api/v1/me/devices/enroll", json={"name": "Mona's laptop"}, headers=DASH)
    assert response.status_code == 200, response.text
    token = response.json()["enrollmentToken"]

    registered = dash.post("/api/v1/agents/register", json={
        "enrollmentToken": token,
        "installationId": str(uuid.uuid4()),
        "name": "Mona's laptop",
        "version": "1.0.0",
        "capabilities": ["recording_processing", "lms"],
    })
    assert registered.status_code == 201, registered.text
    assert registered.json()["deviceToken"].startswith("zaad_")

    # Single use: the same token cannot enrol a second PC.
    again = dash.post("/api/v1/agents/register", json={
        "enrollmentToken": token,
        "installationId": str(uuid.uuid4()),
        "name": "Another PC",
        "version": "1.0.0",
        "capabilities": [],
    })
    assert again.status_code == 401, again.text


def test_enrolling_a_pc_needs_a_dashboard_session(dash):  # noqa: ANN001
    assert dash.post("/api/v1/me/devices/enroll", json={}, headers=DASH).status_code == 401
    assert login(dash).status_code == 200
    # The n8n key is not a dashboard session, and the dashboard header is still required.
    assert dash.post("/api/v1/me/devices/enroll", json={}, headers=client_headers()).status_code == 403

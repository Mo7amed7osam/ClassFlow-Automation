"""Heartbeats, staleness, assignment, the job lifecycle over the WebSocket, reconnects, device
selection and duplicate delivery. The "agent" here is the test, speaking the protocol by hand."""

from __future__ import annotations

import uuid

import pytest
from starlette.websockets import WebSocketDisconnect

from conftest import (
    call,
    client_headers,
    connect,
    expect,
    get_job,
    heartbeat,
    hello,
    post_job,
    recording_payload,
    register,
    run_job,
)


def sweep(client) -> dict[str, int]:  # noqa: ANN001
    return call(client, client.app.state.sweeper.sweep_once)


def devices(client) -> dict[str, dict]:  # noqa: ANN001
    body = client.get("/api/v1/devices", headers=client_headers()).json()
    return {d["name"]: d for d in body["devices"]}


# --------------------------------------------------------------------------- heartbeat & staleness


def test_a_heartbeat_is_acknowledged_and_recorded(client, clock):
    _, token = register(client, "PC-01")
    with connect(client, token) as ws:
        hello(ws)
        clock.advance(30)
        ack = heartbeat(ws, "idle")
        assert ack["serverTime"].startswith("2026-09-12T10:00:30")
        pc = devices(client)["PC-01"]
        assert pc["status"] == "online" and pc["connected"] is True
        assert pc["agentState"] == "idle"
        assert pc["lastHeartbeat"] == "2026-09-12T10:00:30Z"


def test_one_missed_heartbeat_does_not_make_a_device_offline(client, clock):
    _, token = register(client, "PC-01")
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        clock.advance(65)  # two heartbeats missed, still inside 90 s
        assert sweep(client)["offline"] == 0
        assert devices(client)["PC-01"]["status"] == "online"
        heartbeat(ws)


def test_a_device_silent_for_90_seconds_is_offline_and_its_socket_is_closed(client, clock):
    _, token = register(client, "PC-01")
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        clock.advance(91)
        assert sweep(client)["offline"] == 1
        assert devices(client)["PC-01"]["status"] == "offline"
        with pytest.raises(WebSocketDisconnect) as closed:
            ws.receive_json()
        assert closed.value.code == 4002


def test_a_registered_but_unconnected_device_is_offline(client):
    register(client, "PC-01")
    pc = devices(client)["PC-01"]
    assert pc["status"] == "offline" and pc["connected"] is False


# --------------------------------------------------------------------------- the lifecycle


def test_a_job_goes_to_the_connected_device_and_succeeds(client, clock):
    device_id, token = register(client, "PC-01")
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        assign = expect(ws, "job.assign")
        assert assign["jobId"] == job_id
        assert assign["jobType"] == "recording.process"
        assert assign["payload"]["group"] == "AST5_DAT1_S1"
        assert get_job(client, job_id)["status"] == "assigned"

        ws.send_json({"type": "job.accepted", "jobId": job_id})
        assert expect(ws, "ack") == {"type": "ack", "jobId": job_id, "event": "job.accepted"}
        start = expect(ws, "job.start")
        assert start["payload"]["recordLink"] == assign["payload"]["recordLink"]

        clock.advance(2)
        ws.send_json({"type": "job.started", "jobId": job_id})
        expect(ws, "ack")
        running = get_job(client, job_id)
        assert running["status"] == "running" and running["deviceId"] == device_id
        assert running["startedAt"] == "2026-09-12T10:00:02Z"
        refused = client.post(f"/api/v1/jobs/{job_id}/cancel", headers=client_headers())
        assert refused.status_code == 409 and "running" in refused.json()["details"]

        clock.advance(20)
        ws.send_json({"type": "job.succeeded", "jobId": job_id,
                      "result": {"alreadyExists": False, "message": "Recording link attached successfully.",
                                 "cookie": "must-not-be-stored", "recordLink": "must-not-be-stored"}})
        expect(ws, "ack")
        done = get_job(client, job_id)
        assert done["status"] == "succeeded"
        assert done["result"] == {"alreadyExists": False, "message": "Recording link attached successfully."}
        assert done["finishedAt"] == "2026-09-12T10:00:22Z"
        assert done["attempts"] == 1


def test_a_failure_is_recorded_with_its_code(client):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
        ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
        ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
        ws.send_json({"type": "job.failed", "jobId": job_id,
                      "error": {"code": "sessionNotFinished", "message": "The session is not finished.", "retryable": False}})
        expect(ws, "ack")
    job = get_job(client, job_id)
    assert job["status"] == "failed"
    assert job["error"] == {"code": "sessionNotFinished", "message": "The session is not finished.", "retryable": False}


def test_a_retryable_failure_is_queued_again_later_until_attempts_run_out(client, clock):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        for attempt in (1, 2, 3):
            assign = expect(ws, "job.assign")
            assert assign["attempt"] == attempt
            ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
            ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
            ws.send_json({"type": "job.failed", "jobId": job_id,
                          "error": {"code": "busy", "message": "Dashboard busy.", "retryable": True, "retryAfterSeconds": 60}})
            expect(ws, "ack")
            if attempt < 3:
                assert get_job(client, job_id)["status"] == "queued"
                clock.advance(30)
                sweep(client)  # not yet available: nothing is sent
                clock.advance(31)
                heartbeat(ws)
                sweep(client)
        job = get_job(client, job_id)
        assert job["status"] == "failed" and job["attempts"] == 3 and job["error"]["code"] == "busy"


def test_a_rejected_job_goes_back_to_the_queue(client, clock):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
        ws.send_json({"type": "job.rejected", "jobId": job_id, "reason": "busy"})
        expect(ws, "ack")
        assert get_job(client, job_id)["status"] == "queued"
        clock.advance(11)
        heartbeat(ws)
        sweep(client)
        assert expect(ws, "job.assign")["jobId"] == job_id


# --------------------------------------------------------------------------- offline, expiry, orphan


def test_a_job_waits_while_no_device_is_online_and_goes_out_when_one_connects(client):
    _, token = register(client)
    job_id = post_job(client).json()["jobId"]
    assert get_job(client, job_id)["status"] == "queued"
    with connect(client, token) as ws:
        hello(ws)
        assert expect(ws, "job.assign")["jobId"] == job_id


def test_an_assignment_that_is_never_accepted_is_taken_back(client, clock):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
        clock.advance(61)
        heartbeat(ws, "busy")   # alive, but never answered the assignment
        assert sweep(client)["requeued"] == 1
        assert get_job(client, job_id)["status"] == "queued"


def test_a_running_job_whose_device_vanishes_fails_as_agent_lost(client, clock):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
        ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
        ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
    clock.advance(91)
    sweep(client)
    assert get_job(client, job_id)["status"] == "running"   # offline, but maybe still working
    clock.advance(1800)
    assert sweep(client)["lost"] == 1
    job = get_job(client, job_id)
    assert job["status"] == "failed" and job["error"]["code"] == "agentLost"


# --------------------------------------------------------------------------- reconnect & duplicates


def test_after_a_reconnect_an_unaccepted_assignment_is_offered_again(client):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
    with connect(client, token) as ws:
        hello(ws)
        assert expect(ws, "job.assign")["jobId"] == job_id


def test_after_a_reconnect_an_accepted_job_is_started_again_not_assigned(client):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
        ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
    with connect(client, token) as ws:
        hello(ws, activeJobId=job_id)
        assert expect(ws, "job.start")["jobId"] == job_id


def test_a_result_sent_again_after_a_reconnect_is_acknowledged_once_stored(client):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        post_job(client)
        job_id = run_job(ws)
    with connect(client, token) as ws:
        hello(ws)
        ws.send_json({"type": "job.succeeded", "jobId": job_id, "result": {"alreadyExists": True}})
        assert expect(ws, "ack")["jobId"] == job_id
        heartbeat(ws)
    job = get_job(client, job_id)
    assert job["status"] == "succeeded"
    assert job["result"] == {"alreadyExists": False}   # the first result stands


def test_a_duplicate_acceptance_of_a_running_job_does_not_start_it_twice(client):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
        ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
        ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
        ws.send_json({"type": "job.accepted", "jobId": job_id})
        expect(ws, "ack")
        heartbeat(ws, "busy")   # the next message is the heartbeat ack: no second job.start


def test_a_device_cannot_report_on_a_job_that_is_not_its_own(client):
    _, token_a = register(client, "PC-A")
    _, token_b = register(client, "PC-B")
    with connect(client, token_a) as a:
        hello(a)
        heartbeat(a)
        job_id = post_job(client).json()["jobId"]
        expect(a, "job.assign")
        with connect(client, token_b) as b:
            hello(b)
            heartbeat(b)
            b.send_json({"type": "job.accepted", "jobId": job_id})
            expect(b, "ack")
            assert expect(b, "job.revoke")["jobId"] == job_id
            b.send_json({"type": "job.succeeded", "jobId": job_id, "result": {"alreadyExists": False}})
            expect(b, "ack")
            heartbeat(b)
        assert get_job(client, job_id)["status"] == "assigned"


def test_a_newer_connection_replaces_the_older_one(client):
    _, token = register(client)
    with connect(client, token) as first:
        hello(first)
        heartbeat(first)
        with connect(client, token) as second:
            hello(second)
            heartbeat(second)
            with pytest.raises(WebSocketDisconnect) as closed:
                first.receive_json()
            assert closed.value.code == 4000


# --------------------------------------------------------------------------- device selection


def test_only_online_capable_idle_devices_are_chosen_least_recently_assigned_first(client, clock):
    _, token_a = register(client, "PC-A")
    _, token_b = register(client, "PC-B")
    _, token_c = register(client, "PC-C", capabilities=["zoom"])     # not capable
    with connect(client, token_a) as a, connect(client, token_b) as b, connect(client, token_c) as c:
        for ws in (a, b):
            hello(ws)
            heartbeat(ws)
        hello(c, capabilities=["zoom"])
        c.send_json({"type": "heartbeat", "status": "idle", "capabilities": ["zoom"]})
        expect(c, "heartbeat.ack")

        first = post_job(client, recording_payload(group="G1")).json()["jobId"]
        assert expect(a, "job.assign")["jobId"] == first          # never assigned; PC-A before PC-B by name
        second = post_job(client, recording_payload(group="G2")).json()["jobId"]
        assert expect(b, "job.assign")["jobId"] == second         # PC-A is busy
        third = post_job(client, recording_payload(group="G3")).json()["jobId"]
        assert get_job(client, third)["status"] == "queued"      # both busy, PC-C cannot

        for ws, job_id in ((a, first), (b, second)):
            ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
            ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
        clock.advance(1)
        b.send_json({"type": "job.succeeded", "jobId": second, "result": {"alreadyExists": False}})
        expect(b, "ack")
        assert expect(b, "job.assign")["jobId"] == third          # the free one gets it
        heartbeat(c)                                             # and PC-C was never offered anything


def test_between_two_idle_devices_the_least_recently_assigned_wins(client, clock):
    _, token_a = register(client, "PC-A")
    _, token_b = register(client, "PC-B")
    with connect(client, token_a) as a, connect(client, token_b) as b:
        for ws in (a, b):
            hello(ws)
            heartbeat(ws)
        post_job(client, recording_payload(group="G1"))
        run_job(a)
        clock.advance(1)
        post_job(client, recording_payload(group="G2"))
        run_job(b)
        clock.advance(1)
        post_job(client, recording_payload(group="G3"))
        run_job(a)                                               # A was assigned longer ago than B


def test_a_device_reporting_busy_is_not_given_work(client):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws, agentState="busy")
        heartbeat(ws, "busy")
        job_id = post_job(client).json()["jobId"]
        heartbeat(ws, "busy")                                    # no job.assign in between
        assert get_job(client, job_id)["status"] == "queued"
        ws.send_json({"type": "heartbeat", "status": "idle"})
        assert expect(ws, "heartbeat.ack")
        assert expect(ws, "job.assign")["jobId"] == job_id


def test_garbage_messages_are_answered_with_an_error_not_a_crash(client):
    _, token = register(client)
    with connect(client, token) as ws:
        hello(ws)
        ws.send_text("not json")
        assert expect(ws, "error")["code"] == "badJson"
        ws.send_json({"type": "launch.missiles"})
        assert expect(ws, "error")["code"] == "unknownMessage"
        ws.send_json({"type": "job.succeeded", "jobId": "nope"})
        assert expect(ws, "error")["code"] == "badJobId"
        ws.send_json({"type": "job.succeeded", "jobId": str(uuid.uuid4()), "result": {}})
        expect(ws, "ack")
        heartbeat(ws)

"""HTTPS enforcement, secrets kept out of logs, and settings validation."""

from __future__ import annotations

import logging

import pytest
from starlette.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from central_backend.config import ConfigurationError, Settings
from central_backend.main import create_app
from central_backend.observability import LOGGER_NAME, link_preview
from central_backend.security import hash_secret
from conftest import (
    CLIENT_KEY,
    DRIVE_ID,
    client_headers,
    connect,
    enrollment_token,
    expect,
    heartbeat,
    hello,
    make_settings,
    post_job,
    register,
)


def test_outside_development_plain_http_and_ws_are_refused(clean_db, clock):
    app = create_app(make_settings(clean_db, environment="production"), clock=clock, run_background=False, configure_logs=False)
    with TestClient(app) as plain:
        r = plain.get("/health")
        assert r.status_code == 403 and r.json() == {"error": "HTTPS required"}
        assert plain.post("/api/v1/jobs", json={}, headers=client_headers()).status_code == 403
        with pytest.raises(WebSocketDisconnect):
            with plain.websocket_connect("/ws/agent") as ws:
                ws.receive_json()
    with TestClient(app, base_url="https://testserver") as tls:
        assert tls.get("/health").status_code == 200
        assert tls.get("/docs").status_code == 404   # no API docs outside development


def test_no_secret_or_full_drive_link_reaches_the_log(client, caplog):
    caplog.set_level(logging.DEBUG, logger=LOGGER_NAME)
    enrollment = enrollment_token(client, "PC-01")
    _, device_token = register(client, "PC-01", token=enrollment)
    client.get("/api/v1/devices", headers={"X-API-Key": "zaak_wrong-key-000000000000000000000000000"})
    with connect(client, device_token) as ws:
        hello(ws)
        heartbeat(ws)
        job_id = post_job(client).json()["jobId"]
        expect(ws, "job.assign")
        ws.send_json({"type": "job.accepted", "jobId": job_id}); expect(ws, "ack"); expect(ws, "job.start")
        ws.send_json({"type": "job.started", "jobId": job_id}); expect(ws, "ack")
        ws.send_json({"type": "job.failed", "jobId": job_id,
                      "error": {"code": "lmsFailed", "message": f"see https://drive.google.com/file/d/{DRIVE_ID}/view {device_token}"}})
        expect(ws, "ack")
    with pytest.raises(WebSocketDisconnect):
        with connect(client, device_token + "x") as ws:
            ws.receive_json()

    text = caplog.text
    assert "job.created" in text and "agent.connected" in text and "job.failed" in text
    assert '"recordLink":"drive.google.com/file/d/1AbCdE..."' in text      # the preview, shortened once
    for secret in (CLIENT_KEY, enrollment, device_token, DRIVE_ID, "zaak_wrong-key"):
        assert secret not in text


def test_the_link_preview_shows_only_the_start_of_the_file_id():
    assert link_preview(f"https://drive.google.com/file/d/{DRIVE_ID}/view?usp=sharing") == "drive.google.com/file/d/1AbCdE..."
    assert link_preview(f"https://drive.google.com/open?id={DRIVE_ID}") == "drive.google.com/open?id=1AbCdE..."


def test_settings_come_from_the_environment_and_refuse_bad_values():
    good_hash = hash_secret("zaak_x")
    env = {"CENTRAL_DATABASE_URL": "postgresql+asyncpg://u@localhost/central", "CENTRAL_CLIENT_API_KEY_HASHES": good_hash}
    settings = Settings.from_env(env)
    assert settings.environment == "production" and settings.require_https
    assert settings.heartbeat_interval_seconds == 30 and settings.device_stale_seconds == 90

    for broken, fragment in [
        ({**env, "CENTRAL_DATABASE_URL": ""}, "CENTRAL_DATABASE_URL"),
        ({**env, "CENTRAL_DATABASE_URL": "postgresql://u@h/db"}, "postgresql+asyncpg"),
        ({**env, "CENTRAL_CLIENT_API_KEY_HASHES": ""}, "CENTRAL_CLIENT_API_KEY_HASHES"),
        ({**env, "CENTRAL_CLIENT_API_KEY_HASHES": "zaak_plain-key-instead-of-a-hash"}, "SHA-256"),
        ({**env, "CENTRAL_ENVIRONMENT": "staging"}, "CENTRAL_ENVIRONMENT"),
        ({**env, "CENTRAL_HEARTBEAT_INTERVAL_SECONDS": "60"}, "twice the heartbeat"),
        ({**env, "CENTRAL_DEVICE_STALE_SECONDS": "abc"}, "CENTRAL_DEVICE_STALE_SECONDS"),
    ]:
        with pytest.raises(ConfigurationError) as error:
            Settings.from_env(broken)
        assert fragment in str(error.value)
        assert "zaak_plain-key-instead-of-a-hash" not in str(error.value)

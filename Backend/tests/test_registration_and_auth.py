"""Device registration, device authentication, and the two authentication domains kept apart."""

from __future__ import annotations

import uuid

import asyncpg
import pytest
from starlette.websockets import WebSocketDisconnect

from central_backend.security import hash_secret
from conftest import (
    CLIENT_KEY,
    call,
    client_headers,
    connect,
    enrollment_token,
    heartbeat,
    hello,
    post_job,
    register,
)


async def _row(client, sql: str, *args):  # noqa: ANN001, ANN202
    url = client.app.state.settings.database_url.replace("postgresql+asyncpg://", "postgresql://")
    conn = await asyncpg.connect(url)
    try:
        return await conn.fetchrow(sql, *args)
    finally:
        await conn.close()


# --------------------------------------------------------------------------- registration


def test_registration_issues_a_device_token_and_stores_only_its_hash(client):
    installation = uuid.uuid4()
    device_id, token = register(client, "PC-01", installation_id=installation)

    assert token.startswith(f"zaad_{device_id}.")
    row = call(client, _row, client, "SELECT * FROM devices WHERE id = $1", uuid.UUID(device_id))
    assert row["installation_id"] == installation
    assert row["name"] == "PC-01"
    assert row["token_hash"] == hash_secret(token)
    assert token not in str(dict(row))
    assert "recording_processing" in row["capabilities"]


def test_an_enrollment_token_works_once(client):
    token = enrollment_token(client, "PC-01")
    register(client, "PC-01", token=token)
    second = client.post("/api/v1/agents/register", json={
        "enrollmentToken": token, "installationId": str(uuid.uuid4()), "name": "PC-02", "version": "1.0.0"})
    assert second.status_code == 401
    assert second.json()["error"] == "Unauthorized"


def test_an_expired_or_unknown_enrollment_token_is_refused(client, clock):
    token = enrollment_token(client, "PC-01", ttl_hours=1)
    clock.advance(3601)
    body = {"enrollmentToken": token, "installationId": str(uuid.uuid4()), "name": "PC-01", "version": "1.0.0"}
    assert client.post("/api/v1/agents/register", json=body).status_code == 401
    body["enrollmentToken"] = "zaae_not-a-real-token-at-all"
    assert client.post("/api/v1/agents/register", json=body).status_code == 401


def test_registering_the_same_installation_again_keeps_the_device_and_replaces_the_token(client):
    installation = uuid.uuid4()
    first_id, first_token = register(client, "PC-01", installation_id=installation)
    second_id, second_token = register(client, "PC-01", installation_id=installation)
    assert first_id == second_id
    assert first_token != second_token

    with pytest.raises(WebSocketDisconnect) as refused:
        with connect(client, first_token) as ws:
            ws.receive_json()
    assert refused.value.code == 4401
    with connect(client, second_token) as ws:
        hello(ws)
        heartbeat(ws)


def test_registration_rejects_malformed_bodies_without_echoing_them(client):
    response = client.post("/api/v1/agents/register", json={
        "enrollmentToken": "zaae_secret-value-that-must-not-echo", "installationId": "nope", "name": "PC", "version": "1"})
    assert response.status_code == 400
    assert "zaae_secret-value-that-must-not-echo" not in response.text


# --------------------------------------------------------------------------- device authentication


def test_a_registered_device_connects_and_is_welcomed(client):
    device_id, token = register(client)
    with connect(client, token) as ws:
        welcome = hello(ws)
        assert welcome["deviceId"] == device_id
        assert welcome["heartbeatIntervalSeconds"] == 30
        heartbeat(ws)  # the hello is handled before this is answered


@pytest.mark.parametrize("header", [
    None,
    "Bearer ",
    "Bearer zaad_not-a-uuid.secret",
    f"Bearer zaad_{uuid.uuid4()}.secret",
    f"Bearer {CLIENT_KEY}",
    "Basic dXNlcjpwYXNz",
])
def test_an_agent_without_a_valid_device_token_is_refused(client, header):
    register(client)
    headers = {"Authorization": header} if header else {}
    with pytest.raises(WebSocketDisconnect) as refused:
        with client.websocket_connect("/ws/agent", headers=headers) as ws:
            ws.receive_json()
    assert refused.value.code == 4401


def test_a_device_token_with_the_wrong_secret_is_refused(client):
    device_id, token = register(client)
    forged = f"zaad_{device_id}.{'x' * 43}"
    with pytest.raises(WebSocketDisconnect):
        with connect(client, forged) as ws:
            ws.receive_json()


def test_a_revoked_device_can_no_longer_connect(client):
    from central_backend.devices import revoke_device

    device_id, token = register(client)
    state = client.app.state

    async def revoke() -> None:
        async with state.sessionmaker() as session, session.begin():
            assert await revoke_device(session, uuid.UUID(device_id), state.clock())

    call(client, revoke)
    with pytest.raises(WebSocketDisconnect):
        with connect(client, token) as ws:
            ws.receive_json()


# --------------------------------------------------------------------------- client (n8n) authentication


@pytest.mark.parametrize("headers", [
    {},
    {"X-API-Key": "zaak_wrong-key-000000000000000000000000000"},
    {"Authorization": "Bearer zaak_wrong-key-000000000000000000000000000"},
])
def test_an_unauthorized_client_cannot_create_or_read_jobs(client, headers):
    assert client.post("/api/v1/jobs", json={"type": "recording.process", "payload": {}}, headers=headers).status_code == 401
    assert client.get(f"/api/v1/jobs/{uuid.uuid4()}", headers=headers).status_code == 401
    assert client.get("/api/v1/devices", headers=headers).status_code == 401


def test_the_key_is_checked_before_the_body(client):
    response = client.post("/api/v1/jobs", content=b"not json", headers={"Content-Type": "application/json"})
    assert response.status_code in (400, 401)
    response = client.post("/api/v1/jobs", json={"type": "recording.process", "payload": {}})
    assert response.status_code == 401


def test_device_and_enrollment_tokens_are_not_client_keys(client):
    _, device_token = register(client)
    enrollment = enrollment_token(client)
    for secret in (device_token, enrollment):
        assert client.get("/api/v1/devices", headers={"X-API-Key": secret}).status_code == 401
        assert client.get("/api/v1/devices", headers={"Authorization": f"Bearer {secret}"}).status_code == 401


def test_a_valid_client_key_works_in_either_header(client):
    assert client.get("/api/v1/devices", headers=client_headers()).status_code == 200
    assert client.get("/api/v1/devices", headers={"Authorization": f"Bearer {CLIENT_KEY}"}).status_code == 200
    assert post_job(client).status_code == 202

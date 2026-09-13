"""Test harness: a throwaway PostgreSQL, a fake clock, the app, and small helpers that play the agent.

The database is either CENTRAL_TEST_DATABASE_URL (a disposable database: every table is emptied
between tests) or, when that is not set, a temporary cluster created with the PostgreSQL binaries
found on this machine (CENTRAL_TEST_PG_BIN, PATH, or the usual install folders), listening on
127.0.0.1 only and deleted afterwards. No real LMS, Zoom or browser is involved anywhere.
"""

from __future__ import annotations

import asyncio
import glob
import os
import shutil
import socket
import subprocess
import tempfile
import uuid
from collections.abc import Iterator
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any

import asyncpg
import pytest
from starlette.testclient import TestClient

from central_backend.cli import migrate
from central_backend.config import Settings
from central_backend.devices import create_enrollment_token
from central_backend.main import create_app
from central_backend.security import hash_secret

CLIENT_KEY = "zaak_test-client-key-0123456789abcdefghijklmnopqrstuv"
DRIVE_ID = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345"  # made up
DRIVE_LINK = f"https://drive.google.com/file/d/{DRIVE_ID}/view?usp=sharing"


def recording_payload(**overrides: Any) -> dict[str, Any]:
    payload = {"group": "AST5_DAT1_S1", "recordLink": DRIVE_LINK, "date": "2026-09-03", "replaceExisting": False}
    payload.update(overrides)
    return payload


# --------------------------------------------------------------------------- PostgreSQL


def _pg_bin() -> Path | None:
    explicit = os.environ.get("CENTRAL_TEST_PG_BIN")
    if explicit:
        return Path(explicit)
    found = shutil.which("initdb")
    if found:
        return Path(found).parent
    patterns = [r"C:\Program Files\PostgreSQL\*\bin", "/usr/lib/postgresql/*/bin", "/opt/homebrew/opt/postgresql*/bin"]
    for pattern in patterns:
        candidates = sorted(glob.glob(pattern), key=lambda p: [int(x) for x in "".join(c if c.isdigit() else " " for c in p).split()] or [0])
        for candidate in reversed(candidates):
            if (Path(candidate) / ("initdb.exe" if os.name == "nt" else "initdb")).exists():
                return Path(candidate)
    return None


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@pytest.fixture(scope="session")
def database_url() -> Iterator[str]:
    url = os.environ.get("CENTRAL_TEST_DATABASE_URL")
    if url:
        migrate(url)
        yield url
        return

    pg_bin = _pg_bin()
    if pg_bin is None:
        pytest.skip("No PostgreSQL binaries found; set CENTRAL_TEST_DATABASE_URL or CENTRAL_TEST_PG_BIN.")
    exe = ".exe" if os.name == "nt" else ""
    root = Path(tempfile.mkdtemp(prefix="central-pg-"))
    data = root / "data"
    port = _free_port()
    subprocess.run(
        [str(pg_bin / f"initdb{exe}"), "-D", str(data), "-U", "postgres", "-A", "trust", "-E", "UTF8", "--locale=C"],
        check=True, capture_output=True,
    )
    # No pipes: the server inherits pg_ctl's handles, so a captured output would never close.
    subprocess.run(
        [str(pg_bin / f"pg_ctl{exe}"), "-D", str(data), "-l", str(root / "postgres.log"), "-w", "-t", "60",
         "-o", f"-p {port} -c listen_addresses=127.0.0.1", "start"],
        check=True, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=90,
    )
    try:
        url = f"postgresql+asyncpg://postgres@127.0.0.1:{port}/postgres"
        migrate(url)
        yield url
    finally:
        subprocess.run([str(pg_bin / f"pg_ctl{exe}"), "-D", str(data), "-m", "fast", "-w", "stop"],
                       stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=90)
        shutil.rmtree(root, ignore_errors=True)


async def _truncate(url: str) -> None:
    conn = await asyncpg.connect(url.replace("postgresql+asyncpg://", "postgresql://"))
    try:
        await conn.execute("TRUNCATE recordings, job_events, jobs, enrollment_tokens, devices RESTART IDENTITY CASCADE")
    finally:
        await conn.close()


@pytest.fixture
def clean_db(database_url: str) -> str:
    asyncio.run(_truncate(database_url))
    return database_url


# --------------------------------------------------------------------------- app


class FakeClock:
    def __init__(self) -> None:
        self.now = datetime(2026, 9, 12, 10, 0, 0, tzinfo=UTC)

    def __call__(self) -> datetime:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += timedelta(seconds=seconds)


@pytest.fixture
def clock() -> FakeClock:
    return FakeClock()


def make_settings(url: str, **overrides: Any) -> Settings:
    values: dict[str, Any] = {
        "database_url": url,
        "client_api_key_hashes": frozenset({hash_secret(CLIENT_KEY)}),
        "environment": "development",
    }
    values.update(overrides)
    return Settings(**values)


@pytest.fixture
def settings(clean_db: str) -> Settings:
    return make_settings(clean_db)


@pytest.fixture
def client(settings: Settings, clock: FakeClock) -> Iterator[TestClient]:
    app = create_app(settings, clock=clock, run_background=False, configure_logs=False)
    with TestClient(app) as test_client:
        yield test_client


# --------------------------------------------------------------------------- helpers


def call(client: TestClient, fn, *args: Any) -> Any:  # noqa: ANN001
    """Run an async function on the app's own event loop (where its database pool lives)."""
    return client.portal.call(fn, *args)


def enrollment_token(client: TestClient, label: str = "PC", ttl_hours: float = 24) -> str:
    state = client.app.state

    async def make() -> str:
        async with state.sessionmaker() as session, session.begin():
            return await create_enrollment_token(session, label, timedelta(hours=ttl_hours), state.clock())

    return call(client, make)


def register(
    client: TestClient,
    name: str = "PC-01",
    capabilities: list[str] | None = None,
    installation_id: uuid.UUID | None = None,
    token: str | None = None,
) -> tuple[str, str]:
    """(deviceId, deviceToken) for a freshly registered device."""
    response = client.post(
        "/api/v1/agents/register",
        json={
            "enrollmentToken": token or enrollment_token(client, name),
            "installationId": str(installation_id or uuid.uuid4()),
            "name": name,
            "version": "1.0.0",
            "capabilities": ["recording_processing", "lms"] if capabilities is None else capabilities,
        },
    )
    assert response.status_code == 201, response.text
    body = response.json()
    return body["deviceId"], body["deviceToken"]


def client_headers(key: str = CLIENT_KEY) -> dict[str, str]:
    return {"X-API-Key": key}


def post_job(client: TestClient, payload: dict[str, Any] | None = None, key: str | None = None):  # noqa: ANN201
    headers = client_headers()
    if key is not None:
        headers["Idempotency-Key"] = key
    return client.post("/api/v1/jobs", json={"type": "recording.process", "payload": payload or recording_payload()},
                       headers=headers)


def get_job(client: TestClient, job_id: str) -> dict[str, Any]:
    response = client.get(f"/api/v1/jobs/{job_id}", headers=client_headers())
    assert response.status_code == 200, response.text
    return response.json()


def connect(client: TestClient, device_token: str):  # noqa: ANN201
    return client.websocket_connect("/ws/agent", headers={"Authorization": f"Bearer {device_token}"})


def hello(ws, **fields: Any) -> dict[str, Any]:  # noqa: ANN001
    """Read the welcome, then say hello the way the agent does."""
    welcome = ws.receive_json()
    assert welcome["type"] == "welcome"
    ws.send_json({"type": "hello", "version": "1.0.0", "capabilities": ["recording_processing", "lms"],
                  "agentState": "idle", **fields})
    return welcome


def heartbeat(ws, state: str = "idle") -> dict[str, Any]:  # noqa: ANN001
    ws.send_json({"type": "heartbeat", "version": "1.0.0", "status": state,
                  "capabilities": ["recording_processing", "lms"]})
    ack = ws.receive_json()
    assert ack["type"] == "heartbeat.ack", ack
    return ack


def expect(ws, message_type: str) -> dict[str, Any]:  # noqa: ANN001
    message = ws.receive_json()
    assert message["type"] == message_type, message
    return message


def run_job(ws, result: dict[str, Any] | None = None) -> str:  # noqa: ANN001
    """Play a well-behaved agent through one job: accept, start, succeed. Returns the job id."""
    assign = expect(ws, "job.assign")
    job_id = assign["jobId"]
    ws.send_json({"type": "job.accepted", "jobId": job_id})
    assert expect(ws, "ack")["event"] == "job.accepted"
    assert expect(ws, "job.start")["jobId"] == job_id
    ws.send_json({"type": "job.started", "jobId": job_id})
    assert expect(ws, "ack")["event"] == "job.started"
    ws.send_json({"type": "job.succeeded", "jobId": job_id, "result": result or {"alreadyExists": False}})
    assert expect(ws, "ack")["event"] == "job.succeeded"
    return job_id

"""The dashboard: sign-in and sessions, the read-only data endpoints (as the admin), and the web page.

Roles, registration, account management and what a coordinator may see: test_roles.py."""

from __future__ import annotations

import asyncio
import logging
import uuid
from pathlib import Path

import asyncpg
import pytest
from starlette.testclient import TestClient

from central_backend.auth import AuthSettings, hash_password, parse_legacy_admin_users, verify_password
from central_backend.main import create_app
from central_backend.observability import LOGGER_NAME
from central_backend.security import hash_secret
from conftest import (
    CLIENT_KEY,
    DRIVE_ID,
    DRIVE_LINK,
    client_headers,
    connect,
    expect,
    heartbeat,
    hello,
    make_settings,
    post_job,
    recording_payload,
    register,
    run_job,
)

ADMIN_PASSWORD = "test-only admin password 1"                      # made up, for these tests
COORDINATOR_PASSWORD = "test-only coordinator pw 1"                # made up, for these tests
ADMIN_HASH = hash_password(ADMIN_PASSWORD, n=2**10)                 # cheap cost: tests only
COORDINATOR_HASH = hash_password(COORDINATOR_PASSWORD, n=2**10)
DASH = {"X-Dashboard-Request": "1"}
ZOOM_LINK = "https://us06web.zoom.us/rec/share/FAKE-TOKEN-FOR-TESTS.NotARealLink"


def _pg(url: str) -> str:
    return url.replace("postgresql+asyncpg://", "postgresql://")


def run_sql(url: str, sql: str, *args):  # noqa: ANN002, ANN201
    """A query on the test database, outside the app (its own connection and event loop)."""

    async def run():  # noqa: ANN202
        conn = await asyncpg.connect(_pg(url))
        try:
            return await conn.fetch(sql, *args)
        finally:
            await conn.close()

    return asyncio.run(run())


def make_user(url: str, username: str, password: str = COORDINATOR_PASSWORD, *, role: str = "coordinator",
              status: str = "active", display_name: str | None = None, groups: tuple[str, ...] = ()) -> str:
    """A user straight in the database, with these groups (registered if new). Returns the user id."""
    user_id = uuid.uuid4()
    stored = {ADMIN_PASSWORD: ADMIN_HASH, COORDINATOR_PASSWORD: COORDINATOR_HASH}.get(password) or hash_password(password, n=2**10)
    run_sql(url, "INSERT INTO users (id, username, display_name, password_hash, role, status, created_at, updated_at) "
                 "VALUES ($1, $2, $3, $4, $5, $6, now(), now())",
            user_id, username, display_name or username.title(), stored, role, status)
    for group in groups:
        run_sql(url, "INSERT INTO groups (id, name, created_at) VALUES (gen_random_uuid(), $1, now()) "
                     "ON CONFLICT (name) DO NOTHING", group)
        run_sql(url, "INSERT INTO user_groups (user_id, group_id, assigned_at) SELECT $1, id, now() FROM groups WHERE name = $2",
                user_id, group)
    return str(user_id)


def build(settings, clock, *, admin: bool = True, dist: Path | None = None, **overrides) -> TestClient:  # noqa: ANN001
    """The app, with the admin account ('admin' / ADMIN_PASSWORD) already in the database."""
    if admin and not run_sql(settings.database_url, "SELECT 1 FROM users WHERE role = 'admin'"):
        make_user(settings.database_url, "admin", ADMIN_PASSWORD, role="admin", display_name="The Admin")
    app = create_app(settings, clock=clock, run_background=False, configure_logs=False,
                     auth_settings=AuthSettings(**overrides), dashboard_dist=dist or Path("__no_dashboard_build__"))
    return TestClient(app)


@pytest.fixture
def dash(settings, clock):  # noqa: ANN001, ANN201
    with build(settings, clock) as client:
        yield client


def login(client: TestClient, username: str = "admin", password: str = ADMIN_PASSWORD, headers=DASH):  # noqa: ANN001, ANN201
    return client.post("/api/v1/auth/login", json={"username": username, "password": password}, headers=headers)


def sync(client: TestClient, **body):  # noqa: ANN003, ANN201
    response = client.post("/api/v1/recordings/sync", json=body, headers=client_headers())
    assert response.status_code in (200, 201), response.text
    return response.json()["recording"]


def set_lms_status(client: TestClient, recording_id: str, status: str) -> None:
    run_sql(client.app.state.settings.database_url, "UPDATE recordings SET lms_status = $1 WHERE id = $2",
            status, uuid.UUID(recording_id))


# =========================================================================== login and sessions


def test_passwords_are_hashed_with_scrypt_and_verified():
    stored = hash_password("s3cret-enough-password", n=2**10)
    assert stored.startswith("scrypt$1024$8$1$")
    assert "s3cret" not in stored
    assert verify_password("s3cret-enough-password", stored)
    assert not verify_password("s3cret-enough-passwore", stored)
    assert stored != hash_password("s3cret-enough-password", n=2**10)      # salted


def test_a_login_sets_an_httponly_strict_session_cookie(dash):
    response = login(dash)
    assert response.status_code == 200
    assert response.json()["username"] == "admin" and response.json()["role"] == "admin"
    cookie = response.headers["set-cookie"]
    assert cookie.startswith("zaa_session=zaas_")
    for attribute in ("HttpOnly", "Path=/", "SameSite=strict", "Max-Age=28800"):
        assert attribute.lower() in cookie.lower()
    token = cookie.split(";")[0].split("=", 1)[1]
    assert token not in response.text                                        # only in the cookie

    rows = run_sql(dash.app.state.settings.database_url,
                   "SELECT s.token_hash, s.username, u.username AS owner, u.last_login_at FROM admin_sessions s "
                   "JOIN users u ON u.id = s.user_id")
    assert [(r["token_hash"], r["username"], r["owner"]) for r in rows] == [(hash_secret(token), "admin", "admin")]
    assert rows[0]["last_login_at"] is not None

    me = dash.get("/api/v1/auth/me").json()
    assert (me["username"], me["displayName"], me["role"], me["allGroups"], me["groups"]) == \
        ("admin", "The Admin", "admin", True, None)


def test_user_names_are_case_insensitive(dash):
    assert login(dash, username="  Admin ").status_code == 200


@pytest.mark.parametrize("username, password", [("admin", "wrong password"), ("nobody", ADMIN_PASSWORD)])
def test_a_wrong_password_or_unknown_user_gets_the_same_401(dash, username, password):
    response = login(dash, username, password)
    assert response.status_code == 401
    assert response.json() == {"error": "Invalid username or password"}
    assert "set-cookie" not in response.headers
    assert dash.get("/api/v1/auth/me").status_code == 401


def test_login_and_logout_need_the_dashboard_header(dash):
    assert login(dash, headers={}).status_code == 403
    assert login(dash, headers={"X-Dashboard-Request": "yes"}).status_code == 403
    assert login(dash).status_code == 200
    assert dash.post("/api/v1/auth/logout").status_code == 403
    assert dash.get("/api/v1/auth/me").status_code == 200                   # still signed in


def test_logout_ends_the_session_even_for_a_copied_cookie(dash):
    token = login(dash).headers["set-cookie"].split(";")[0].split("=", 1)[1]
    assert dash.post("/api/v1/auth/logout", headers=DASH).status_code == 200
    assert dash.get("/api/v1/auth/me").status_code == 401
    dash.cookies.set("zaa_session", token)                                  # replaying the old cookie
    assert dash.get("/api/v1/auth/me").status_code == 401


def test_a_session_expires_after_the_configured_hours(dash, clock):
    login(dash)
    clock.advance(8 * 3600 - 1)
    assert dash.get("/api/v1/auth/me").status_code == 200
    clock.advance(2)
    assert dash.get("/api/v1/auth/me").status_code == 401


def test_a_disabled_account_loses_its_session_at_once(dash):
    make_user(dash.app.state.settings.database_url, "sara")
    assert login(dash, "sara", COORDINATOR_PASSWORD).status_code == 200
    run_sql(dash.app.state.settings.database_url, "UPDATE users SET status = 'disabled' WHERE username = 'sara'")
    assert dash.get("/api/v1/auth/me").status_code == 401


def test_repeated_failures_are_throttled_even_for_the_right_password(dash, clock):
    for _ in range(5):
        assert login(dash, password="wrong").status_code == 401
    throttled = login(dash)
    assert throttled.status_code == 429
    assert int(throttled.headers["Retry-After"]) > 0
    clock.advance(901)
    assert login(dash).status_code == 200


def test_auth_settings_come_from_the_environment():
    from central_backend.config import ConfigurationError

    settings = AuthSettings.from_env({"CENTRAL_SESSION_HOURS": "4", "CENTRAL_ALLOW_REGISTRATION": "false"})
    assert settings.session_hours == 4 and settings.allow_registration is False
    assert AuthSettings.from_env({"CENTRAL_ADMIN_SESSION_HOURS": "2"}).session_hours == 2      # the old name still works
    defaults = AuthSettings.from_env({})
    assert (defaults.session_hours, defaults.allow_registration, defaults.legacy_admin_variable_set) == (8, True, False)
    assert AuthSettings.from_env({"CENTRAL_ADMIN_USERS": "admin:x"}).legacy_admin_variable_set
    # "Keep me signed in" lasts 120 days: a coordinator's PC needs the server only now and then.
    assert defaults.remember_days == 120
    assert AuthSettings.from_env({"CENTRAL_REMEMBER_DAYS": "150"}).remember_days == 150
    for broken in ({"CENTRAL_SESSION_HOURS": "1000"}, {"CENTRAL_SESSION_HOURS": "soon"},
                   {"CENTRAL_ALLOW_REGISTRATION": "maybe"}, {"CENTRAL_REMEMBER_DAYS": "365"}):
        with pytest.raises(ConfigurationError):
            AuthSettings.from_env(broken)


def test_the_old_admin_setting_is_only_read_for_the_move_into_the_database():
    stored = hash_password("x" * 12, n=2**14)
    weak = hash_password("x", n=2**10)
    assert parse_legacy_admin_users(f"Admin:{stored}, ops:{stored}") == ({"admin": stored, "ops": stored}, [])
    assert parse_legacy_admin_users(f"mo:{stored}") == ({"mo": stored}, [])                   # short names were valid there
    users, skipped = parse_legacy_admin_users(f"admin:plaintext-password,cheap:{weak},Bad!name:{stored}")
    assert users == {}
    assert skipped == ["'admin' has no scrypt hash", "'cheap' has a hash that is too weak (N < 16384)",
                       "'bad!name' is not a valid username"]
    assert not any(weak in reason or stored in reason or "plaintext" in reason for reason in skipped)
    assert parse_legacy_admin_users(None) == ({}, [])


def test_in_production_the_cookie_is_secure_and_host_prefixed(clean_db, clock):
    settings = make_settings(clean_db, environment="production")
    with build(settings, clock) as client:
        client.base_url = "https://testserver"
        response = client.post("https://testserver/api/v1/auth/login",
                               json={"username": "admin", "password": ADMIN_PASSWORD}, headers=DASH)
        assert response.status_code == 200
        cookie = response.headers["set-cookie"]
        assert cookie.startswith("__Host-zaa_session=") and "secure" in cookie.lower()


# =========================================================================== separation of the three domains


DATA_ENDPOINTS = ["/api/v1/dashboard/overview", "/api/v1/dashboard/recordings", "/api/v1/dashboard/groups",
                  "/api/v1/dashboard/agents", f"/api/v1/dashboard/agents/{uuid.uuid4()}/jobs", "/api/v1/auth/me",
                  "/api/v1/admin/users", "/api/v1/admin/groups"]


@pytest.mark.parametrize("path", DATA_ENDPOINTS)
def test_every_dashboard_endpoint_needs_a_login(dash, path):
    assert dash.get(path).status_code == 401
    assert dash.get(path, headers={"X-API-Key": CLIENT_KEY}).status_code == 401     # the n8n key is not a login
    assert dash.get(path, headers={"Authorization": f"Bearer {CLIENT_KEY}"}).status_code == 401


def test_a_dashboard_login_does_not_open_the_n8n_api(dash):
    login(dash)
    assert dash.get("/api/v1/recordings").status_code == 401
    assert dash.get("/api/v1/devices").status_code == 401
    assert dash.post("/api/v1/jobs", json={"type": "recording.process", "payload": recording_payload()}).status_code == 401


def test_no_password_or_session_token_reaches_the_log(dash, caplog):
    caplog.set_level(logging.DEBUG, logger=LOGGER_NAME)
    login(dash, password="a wrong password value")
    response = login(dash)
    token = response.headers["set-cookie"].split(";")[0].split("=", 1)[1]
    dash.post("/api/v1/auth/logout", headers=DASH)
    assert "auth.login" in caplog.text and "auth.login_failed" in caplog.text
    for secret in (ADMIN_PASSWORD, "a wrong password value", token, ADMIN_HASH):
        assert secret not in caplog.text


# =========================================================================== data


@pytest.fixture
def seeded(dash, clock):  # noqa: ANN001, ANN201
    """Four recordings, synced a minute apart: Drive, Zoom-only, no link, and one on the LMS."""
    ids = {}
    ids["s7_11"] = sync(dash, group="CAI5_AIS4_S7", date="2026-09-11", startTime="18:00", fileName="a.mp4", link=DRIVE_LINK)["id"]
    clock.advance(60)
    ids["s8_12"] = sync(dash, group="CAI5_AIS4_S8", date="2026-09-12", startTime="20:00", link=ZOOM_LINK)["id"]
    clock.advance(60)
    ids["s7_10"] = sync(dash, group="CAI5_AIS4_S7", date="2026-09-10", startTime="18:00")["id"]
    clock.advance(60)
    ids["s9_09"] = sync(dash, group="CAI5_AIS4_S9", date="2026-09-09", link=DRIVE_LINK)["id"]
    set_lms_status(dash, ids["s9_09"], "attached")
    clock.advance(60)
    sync(dash, group="CAI5_AIS4_S7", date="2026-09-11", startTime="18:00", fileName="a-final.mp4")    # s7_11 updated last
    login(dash)
    return ids


def page(client: TestClient, **params) -> dict:
    response = client.get("/api/v1/dashboard/recordings", params=params)
    assert response.status_code == 200, response.text
    return response.json()


def test_recordings_default_to_the_most_recently_updated_first(dash, seeded):
    body = page(dash)
    assert body["sort"] == "updated" and body["total"] == 4 and body["page"] == 1 and body["pageSize"] == 25
    assert [r["id"] for r in body["items"]] == [seeded["s7_11"], seeded["s9_09"], seeded["s7_10"], seeded["s8_12"]]
    first = body["items"][0]
    assert first["fileName"] == "a-final.mp4"
    for key in ("group", "date", "startTime", "fileName", "source", "lmsStatus", "updatedAt", "driveLink", "zoomLink"):
        assert key in first


def test_recordings_can_be_sorted_by_creation_or_session(dash, seeded):
    created = [r["id"] for r in page(dash, sort="created")["items"]]
    assert created == [seeded["s9_09"], seeded["s7_10"], seeded["s8_12"], seeded["s7_11"]]
    session = [(r["group"], r["date"]) for r in page(dash, sort="session")["items"]]
    assert session == [("CAI5_AIS4_S8", "2026-09-12"), ("CAI5_AIS4_S7", "2026-09-11"),
                       ("CAI5_AIS4_S7", "2026-09-10"), ("CAI5_AIS4_S9", "2026-09-09")]


def test_link_status_combines_the_link_with_the_lms_status(dash, seeded):
    labels = {r["id"]: r["linkStatus"] for r in page(dash)["items"]}
    assert labels[seeded["s7_11"]] == {"link": "drive", "lms": "pending", "label": "Drive · pending"}
    assert labels[seeded["s8_12"]] == {"link": "zoom", "lms": "pending", "label": "Zoom only · pending"}
    assert labels[seeded["s7_10"]] == {"link": "missing", "lms": "pending", "label": "Missing · pending"}
    assert labels[seeded["s9_09"]] == {"link": "drive", "lms": "attached", "label": "Drive · on LMS"}


@pytest.mark.parametrize("params, expected", [
    ({"group": "CAI5_AIS4_S7"}, {"s7_11", "s7_10"}),
    ({"date": "2026-09-12"}, {"s8_12"}),
    ({"status": "attached"}, {"s9_09"}),
    ({"status": "pending", "group": "CAI5_AIS4_S7", "date": "2026-09-10"}, {"s7_10"}),
    ({"link": "drive"}, {"s7_11", "s9_09"}),
    ({"link": "zoom"}, {"s8_12"}),
    ({"link": "missing"}, {"s7_10"}),
    ({"group": "NOPE"}, set()),
])
def test_recordings_can_be_filtered(dash, seeded, params, expected):
    body = page(dash, **params)
    names = {name for name, rid in seeded.items() if rid in {r["id"] for r in body["items"]}}
    assert names == expected and body["total"] == len(expected)


def test_recordings_are_paged_with_a_total(dash, seeded):
    first = page(dash, pageSize=3)
    second = page(dash, pageSize=3, page=2)
    assert first["total"] == second["total"] == 4
    assert len(first["items"]) == 3 and len(second["items"]) == 1
    assert {r["id"] for r in first["items"]}.isdisjoint({r["id"] for r in second["items"]})


@pytest.mark.parametrize("params", [{"sort": "name"}, {"pageSize": "101"}, {"page": "0"}, {"link": "dropbox"},
                                    {"date": "12/09/2026"}, {"status": "Pending; DROP"}])
def test_bad_recording_queries_are_refused(dash, seeded, params):
    assert dash.get("/api/v1/dashboard/recordings", params=params).status_code == 400


def test_groups_show_counts_last_date_and_lms_progress(dash, seeded):
    body = dash.get("/api/v1/dashboard/groups").json()
    assert body["count"] == 3
    by_group = {g["group"]: g for g in body["groups"]}
    assert list(by_group) == ["CAI5_AIS4_S7", "CAI5_AIS4_S8", "CAI5_AIS4_S9"]
    s7 = by_group["CAI5_AIS4_S7"]
    assert (s7["recordings"], s7["lastSessionDate"], s7["pending"], s7["onLms"], s7["missingLink"]) == (2, "2026-09-11", 2, 0, 1)
    assert by_group["CAI5_AIS4_S9"]["onLms"] == 1
    assert s7["lastUpdatedAt"].endswith("Z")


def test_overview_counts(dash, seeded):
    body = dash.get("/api/v1/dashboard/overview").json()
    assert body["recordings"] == {"total": 4, "pending": 3, "onLms": 1, "missingLink": 1}
    assert body["agents"] == {"total": 0, "online": 0, "busy": 0}
    assert body["jobs"]["queued"] == 0


def test_agents_show_status_heartbeat_and_assigned_jobs(dash, clock):
    login(dash)
    _, token_a = register(dash, "PC-A")
    register(dash, "PC-B")                                                   # registered, never connected
    with connect(dash, token_a) as ws:
        hello(ws)
        heartbeat(ws)
        post_job(dash)
        job_id = run_job(ws)                                                 # one finished job
        post_job(dash, recording_payload(group="CAI5_AIS4_S8"))
        assign = expect(ws, "job.assign")
        ws.send_json({"type": "job.accepted", "jobId": assign["jobId"]}); expect(ws, "ack"); expect(ws, "job.start")
        ws.send_json({"type": "job.started", "jobId": assign["jobId"]}); expect(ws, "ack")

        body = dash.get("/api/v1/dashboard/agents").json()
        assert body["count"] == 2 and body["online"] == 1
        a, b = body["agents"]
        assert (a["name"], a["status"], a["connected"]) == ("PC-A", "online", True)
        assert a["lastHeartbeat"] == "2026-09-12T10:00:00Z"
        assert a["jobsLast24h"] == {"succeeded": 1, "failed": 0}
        running = a["activeJobs"]
        assert len(running) == 1
        assert running[0]["status"] == "running" and running[0]["group"] == "CAI5_AIS4_S8"
        assert "payload" not in running[0] and DRIVE_ID not in str(body)       # no links on the agents page
        assert "installationId" not in a
        assert (b["name"], b["status"], b["connected"], b["activeJobs"]) == ("PC-B", "offline", False, [])

        overview = dash.get("/api/v1/dashboard/overview").json()
        assert overview["agents"]["online"] == 1 and overview["jobs"]["running"] == 1
        assert overview["jobs"]["succeededLast24h"] == 1

        jobs = dash.get(f"/api/v1/dashboard/agents/{a['deviceId']}/jobs").json()
        assert [j["status"] for j in jobs["jobs"]] == ["running", "succeeded"]  # active first
        assert jobs["jobs"][1]["jobId"] == job_id and jobs["jobs"][1]["alreadyExists"] is False
        heartbeat(ws, "busy")


def test_unknown_agents_are_404(dash):
    login(dash)
    assert dash.get(f"/api/v1/dashboard/agents/{uuid.uuid4()}/jobs").status_code == 404
    assert dash.get("/api/v1/dashboard/agents/not-an-id/jobs").status_code == 404


def test_the_dashboard_only_reads(dash, seeded):
    for path in ("/api/v1/dashboard/recordings", "/api/v1/dashboard/groups", "/api/v1/dashboard/agents"):
        assert dash.post(path, headers=DASH).status_code == 405
        assert dash.delete(path, headers=DASH).status_code == 405


# =========================================================================== the web page


@pytest.fixture
def built(tmp_path: Path) -> Path:
    dist = tmp_path / "dist"
    (dist / "assets").mkdir(parents=True)
    (dist / "index.html").write_text("<!doctype html><title>Dashboard</title><div id=root></div>", encoding="utf-8")
    (dist / "assets" / "app-1234.js").write_text("console.log('app')", encoding="utf-8")
    (tmp_path / "secret.txt").write_text("outside the build", encoding="utf-8")
    return dist


def test_the_built_dashboard_is_served_with_security_headers(settings, clock, built):
    with build(settings, clock, dist=built) as client:
        assert client.get("/dashboard", follow_redirects=False).headers["location"] == "/dashboard/"
        index = client.get("/dashboard/")
        assert index.status_code == 200 and "<div id=root>" in index.text
        csp = index.headers["content-security-policy"]
        assert "default-src 'self'" in csp and "frame-ancestors 'none'" in csp and "script-src 'self'" in csp
        assert index.headers["x-frame-options"] == "DENY"
        assert index.headers["referrer-policy"] == "no-referrer"
        assert index.headers["cache-control"] == "no-cache"

        asset = client.get("/dashboard/assets/app-1234.js")
        assert asset.status_code == 200 and "immutable" in asset.headers["cache-control"]

        for route in ("/dashboard/agents", "/dashboard/recordings", "/dashboard/groups/CAI5_AIS4_S7"):
            assert "<div id=root>" in client.get(route).text                 # the app's own pages survive reload
        assert client.get("/dashboard/assets/missing.js").status_code == 404
        assert client.get("/dashboard/favicon.ico").status_code == 404
        leak = client.get("/dashboard/assets/..%2f..%2fsecret.txt")
        assert leak.status_code == 404 and "outside the build" not in leak.text


def test_without_a_build_there_is_no_dashboard_page(dash):
    assert dash.get("/dashboard/").status_code == 404
    assert dash.get("/health").status_code == 200

"""What users keep on the server: their LMS accounts (passwords encrypted), the shared settings, and
"keep me signed in" sessions that live in the database."""

from __future__ import annotations

import secrets
from datetime import timedelta
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from central_backend.auth import AuthSettings
from central_backend.main import create_app
from central_backend.user_data import SecretBox
from test_dashboard import ADMIN_PASSWORD, COORDINATOR_PASSWORD, DASH, login, make_user, run_sql

LMS_PASSWORD = "made-up LMS password for tests"


def build(settings, clock, box: SecretBox | None) -> TestClient:  # noqa: ANN001
    if not run_sql(settings.database_url, "SELECT 1 FROM users WHERE role = 'admin'"):
        make_user(settings.database_url, "admin", ADMIN_PASSWORD, role="admin", display_name="The Admin")
    app = create_app(settings, clock=clock, run_background=False, configure_logs=False, auth_settings=AuthSettings(),
                     dashboard_dist=Path("__no_dashboard_build__"), secret_box=box)
    return TestClient(app)


@pytest.fixture
def box() -> SecretBox:
    return SecretBox(secrets.token_bytes(32))


@pytest.fixture
def dash(settings, clock, box):  # noqa: ANN001, ANN201
    with build(settings, clock, box) as client:
        yield client


def as_user(client: TestClient, username: str, password: str) -> TestClient:
    client.cookies.clear()
    assert login(client, username, password).status_code == 200
    return client


def add(client: TestClient, email: str, **extra):  # noqa: ANN003, ANN201
    return client.post("/api/v1/me/lms-accounts", headers=DASH,
                       json={"label": extra.pop("label", ""), "email": email, "password": extra.pop("password", LMS_PASSWORD), **extra})


# =========================================================================== LMS accounts


def test_a_users_lms_accounts_are_theirs_encrypted_and_one_is_in_use(dash):
    as_user(dash, "admin", ADMIN_PASSWORD)
    first = add(dash, "coordinator@example.com", label="Coordinator")
    assert first.status_code == 201, first.text
    second = add(dash, "admin@example.com", role="admin", label="Admin")
    listed = dash.get("/api/v1/me/lms-accounts").json()
    assert listed["canKeepPasswords"] is True
    assert [(a["email"], a["active"]) for a in listed["accounts"]] == [("admin@example.com", True), ("coordinator@example.com", False)]
    assert "password" not in str(listed)

    stored = run_sql(dash.app.state.settings.database_url, "SELECT password_encrypted FROM lms_accounts")
    assert all(LMS_PASSWORD not in row["password_encrypted"] for row in stored)          # never in clear

    used = dash.post(f"/api/v1/me/lms-accounts/{first.json()['id']}/use", headers=DASH)
    assert used.json()["active"] is True
    secret = dash.post(f"/api/v1/me/lms-accounts/{first.json()['id']}/secret", headers=DASH)
    assert secret.json() == {"id": first.json()["id"], "email": "coordinator@example.com", "password": LMS_PASSWORD}
    assert secret.headers["cache-control"] == "no-store"

    # Saving the same email again updates it rather than adding a second one.
    again = add(dash, "Coordinator@Example.com", password="a new made-up password", label="Coord")
    assert again.status_code == 200 and again.json()["id"] == first.json()["id"]
    assert dash.post(f"/api/v1/me/lms-accounts/{first.json()['id']}/secret", headers=DASH).json()["password"] == "a new made-up password"
    assert dash.delete(f"/api/v1/me/lms-accounts/{second.json()['id']}", headers=DASH).status_code == 200
    assert len(dash.get("/api/v1/me/lms-accounts").json()["accounts"]) == 1


def test_nobody_sees_or_uses_another_users_lms_account(dash):
    make_user(dash.app.state.settings.database_url, "sara", COORDINATOR_PASSWORD)
    as_user(dash, "admin", ADMIN_PASSWORD)
    mine = add(dash, "admin@example.com").json()["id"]
    as_user(dash, "sara", COORDINATOR_PASSWORD)
    assert dash.get("/api/v1/me/lms-accounts").json()["accounts"] == []
    assert dash.post(f"/api/v1/me/lms-accounts/{mine}/secret", headers=DASH).status_code == 404
    assert dash.post(f"/api/v1/me/lms-accounts/{mine}/use", headers=DASH).status_code == 404
    assert dash.delete(f"/api/v1/me/lms-accounts/{mine}", headers=DASH).status_code == 404


def test_every_write_and_the_secret_need_the_header_and_a_sign_in(dash):
    as_user(dash, "admin", ADMIN_PASSWORD)
    account = add(dash, "admin@example.com").json()["id"]
    assert dash.post(f"/api/v1/me/lms-accounts/{account}/secret").status_code == 403
    assert dash.post("/api/v1/me/lms-accounts", json={"email": "x@example.com", "password": "p"}).status_code == 403
    dash.cookies.clear()
    assert dash.get("/api/v1/me/lms-accounts").status_code == 401
    assert dash.post(f"/api/v1/me/lms-accounts/{account}/secret", headers=DASH).status_code == 401


def test_without_a_key_the_server_refuses_to_keep_passwords(settings, clock):  # noqa: ANN001
    with build(settings, clock, None) as client:
        as_user(client, "admin", ADMIN_PASSWORD)
        assert client.get("/api/v1/me/lms-accounts").json()["canKeepPasswords"] is False
        assert add(client, "admin@example.com").status_code == 503


def test_a_sealed_password_is_bound_to_its_row():
    box = SecretBox(secrets.token_bytes(32))
    sealed = box.seal("secret", "user:row-1")
    assert box.open(sealed, "user:row-1") == "secret"
    with pytest.raises(Exception):
        box.open(sealed, "user:row-2")
    with pytest.raises(Exception):
        SecretBox(secrets.token_bytes(32)).open(sealed, "user:row-1")
    assert SecretBox.from_env({"CENTRAL_SECRETS_KEY": SecretBox.new_key()}) is not None
    assert SecretBox.from_env({}) is None


# =========================================================================== shared settings


def test_the_admin_writes_shared_settings_everyone_reads_them(dash):
    make_user(dash.app.state.settings.database_url, "sara", COORDINATOR_PASSWORD)
    as_user(dash, "admin", ADMIN_PASSWORD)
    value = {"url": "https://docs.google.com/spreadsheets/d/FAKEsheetFAKEsheetFAKE/edit", "tabs": {}}
    assert dash.put("/api/v1/settings/recordingsSheet", json={"value": value}, headers=DASH).status_code == 200
    assert dash.put("/api/v1/settings/somethingElse", json={"value": {}}, headers=DASH).status_code == 404
    as_user(dash, "sara", COORDINATOR_PASSWORD)
    assert dash.get("/api/v1/settings/recordingsSheet").json()["value"] == value
    assert dash.put("/api/v1/settings/recordingsSheet", json={"value": {}}, headers=DASH).status_code == 403


# =========================================================================== keep me signed in


def test_keep_me_signed_in_is_a_long_session_in_the_database(dash, clock):  # noqa: ANN001
    plain = login(dash, "admin", ADMIN_PASSWORD)
    remembered = dash.post("/api/v1/auth/login", headers=DASH, json={"username": "admin", "password": ADMIN_PASSWORD, "remember": True})
    assert remembered.json()["remembered"] is True
    rows = run_sql(dash.app.state.settings.database_url, "SELECT expires_at - created_at AS life FROM admin_sessions ORDER BY expires_at")
    assert [r["life"] for r in rows] == [timedelta(hours=8), timedelta(days=120)]
    assert plain.json()["remembered"] is False
    # Nine days later only the remembered session still works.
    clock.advance(timedelta(days=9).total_seconds())
    assert dash.get("/api/v1/auth/me").status_code == 200

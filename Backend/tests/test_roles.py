"""Roles: the one admin and the coordinators. Registration and approval, the admin's account and
group management, and above all that a coordinator sees and changes only their own groups."""

from __future__ import annotations

import asyncio
import logging
import uuid
from datetime import date

import asyncpg
import pytest
from starlette.testclient import TestClient

from central_backend.observability import LOGGER_NAME
from conftest import DRIVE_LINK, client_headers, connect, hello, register
from test_dashboard import (
    ADMIN_PASSWORD,
    COORDINATOR_PASSWORD,
    DASH,
    ZOOM_LINK,
    build,
    login,
    make_user,
    run_sql,
    sync,
)

A, B, C = "CAI5_AIS4_S7", "CAI5_AIS4_S8", "CAI5_AIS4_S9"
NEW_PASSWORD = "a different test password"           # made up, for these tests


@pytest.fixture
def dash(settings, clock):  # noqa: ANN001, ANN201
    with build(settings, clock) as client:
        yield client


def url(client: TestClient) -> str:
    return client.app.state.settings.database_url


def sign_up(client: TestClient, username: str = "sara", password: str = COORDINATOR_PASSWORD,
            display_name: str = "Sara", headers=DASH):  # noqa: ANN001, ANN201
    return client.post("/api/v1/auth/register", json={"username": username, "displayName": display_name,
                                                      "password": password}, headers=headers)


def user_id(client: TestClient, username: str) -> str:
    return str(run_sql(url(client), "SELECT id FROM users WHERE username = $1", username)[0]["id"])


def group_id(client: TestClient, name: str) -> str:
    return str(run_sql(url(client), "SELECT id FROM groups WHERE name = $1", name)[0]["id"])


def as_user(client: TestClient, username: str, password: str = COORDINATOR_PASSWORD) -> TestClient:
    client.cookies.clear()
    response = login(client, username, password)
    assert response.status_code == 200, response.text
    return client


def audit_rows(client: TestClient) -> list:
    return run_sql(url(client), "SELECT username, user_id, action, details FROM admin_audit_log ORDER BY id")


# =========================================================================== registration and approval


def test_a_registration_waits_for_approval_then_signs_in(dash):
    response = sign_up(dash, "  Sara.K ", display_name=" Sara K ")
    assert response.status_code == 201
    assert response.json()["status"] == "pending" and response.json()["username"] == "sara.k"
    assert "set-cookie" not in response.headers

    refused = login(dash, "sara.k", COORDINATOR_PASSWORD)
    assert refused.status_code == 403
    assert refused.json() == {"error": "Account awaiting approval", "details": {"reason": "pending"}}
    # A wrong password on a pending account says nothing about the account.
    assert login(dash, "sara.k", "not the password").status_code == 401

    as_user(dash, "admin", ADMIN_PASSWORD)
    listed = dash.get("/api/v1/admin/users").json()
    assert [u["username"] for u in listed["users"]] == ["sara.k", "admin"]        # pending first
    assert listed["counts"] == {"pending": 1, "active": 1, "rejected": 0, "disabled": 0}
    assert dash.get("/api/v1/admin/users", params={"status": "pending"}).json()["count"] == 1
    sara = listed["users"][0]
    assert (sara["displayName"], sara["role"], sara["status"], sara["groups"]) == ("Sara K", "coordinator", "pending", [])
    assert "password" not in str(listed).lower()

    approved = dash.post(f"/api/v1/admin/users/{sara['id']}/approve", headers=DASH)
    assert approved.status_code == 200 and approved.json()["status"] == "active"
    assert approved.json()["approvedAt"] is not None

    as_user(dash, "sara.k")
    me = dash.get("/api/v1/auth/me").json()
    assert (me["role"], me["allGroups"], me["groups"]) == ("coordinator", False, [])


def test_a_rejected_registration_cannot_sign_in(dash):
    sign_up(dash)
    as_user(dash, "admin", ADMIN_PASSWORD)
    sara = user_id(dash, "sara")
    assert dash.post(f"/api/v1/admin/users/{sara}/reject", headers=DASH).json()["status"] == "rejected"
    assert dash.post(f"/api/v1/admin/users/{sara}/reject", headers=DASH).status_code == 409   # only pending ones
    dash.cookies.clear()
    refused = login(dash, "sara", COORDINATOR_PASSWORD)
    assert refused.status_code == 403 and refused.json()["details"] == {"reason": "rejected"}
    # A rejection can be undone by approving.
    as_user(dash, "admin", ADMIN_PASSWORD)
    assert dash.post(f"/api/v1/admin/users/{sara}/approve", headers=DASH).status_code == 200
    as_user(dash, "sara")


@pytest.mark.parametrize("body, status", [
    ({"username": "ab", "displayName": "X", "password": COORDINATOR_PASSWORD}, 400),          # too short a name
    ({"username": "has space", "displayName": "X", "password": COORDINATOR_PASSWORD}, 400),
    ({"username": "sara2", "displayName": "X", "password": "short"}, 400),
    ({"username": "sara.password.x", "displayName": "X", "password": "SARA.PASSWORD.X"}, 400),   # the name
    ({"username": "sara3", "displayName": "   ", "password": COORDINATOR_PASSWORD}, 400),
    ({"username": "sara4", "displayName": "X", "password": COORDINATOR_PASSWORD, "role": "admin"}, 400),
    ({"username": "admin", "displayName": "X", "password": COORDINATOR_PASSWORD}, 409),       # taken
])
def test_bad_registrations_are_refused(dash, body, status):
    assert dash.post("/api/v1/auth/register", json=body, headers=DASH).status_code == status
    assert run_sql(url(dash), "SELECT count(*) AS n FROM users")[0]["n"] == 1


def test_registration_needs_the_header_and_can_be_closed(dash, settings, clock):
    assert sign_up(dash, headers={}).status_code == 403
    with build(settings, clock, allow_registration=False) as closed:
        response = sign_up(closed)
        assert response.status_code == 403 and response.json()["error"] == "Registration is closed"


def test_registrations_are_rate_limited_per_address(dash, clock):
    for n in range(5):
        assert sign_up(dash, f"user{n}x").status_code == 201
    limited = sign_up(dash, "user9x")
    assert limited.status_code == 429 and int(limited.headers["Retry-After"]) > 0
    clock.advance(3601)
    assert sign_up(dash, "user9x").status_code == 201


# =========================================================================== the admin's account management


def test_the_admin_creates_a_coordinator_with_groups(dash):
    sync(dash, group=A, date="2026-09-10")
    sync(dash, group=B, date="2026-09-10")
    as_user(dash, "admin", ADMIN_PASSWORD)
    created = dash.post("/api/v1/admin/users", headers=DASH, json={
        "username": "Omar", "displayName": "Omar A.", "password": COORDINATOR_PASSWORD, "groupIds": [group_id(dash, A)]})
    assert created.status_code == 201, created.text
    body = created.json()
    assert (body["username"], body["status"], body["role"]) == ("omar", "active", "coordinator")
    assert [g["name"] for g in body["groups"]] == [A]

    assert dash.post("/api/v1/admin/users", headers=DASH, json={
        "username": "omar", "displayName": "Again", "password": COORDINATOR_PASSWORD}).status_code == 409
    assert dash.post("/api/v1/admin/users", headers=DASH, json={
        "username": "x-role", "displayName": "X", "password": COORDINATOR_PASSWORD, "role": "admin"}).status_code == 400
    assert dash.post("/api/v1/admin/users", headers=DASH, json={
        "username": "ghost", "displayName": "X", "password": COORDINATOR_PASSWORD,
        "groupIds": [str(uuid.uuid4())]}).status_code == 400
    assert dash.post("/api/v1/admin/users", json={
        "username": "nohdr", "displayName": "X", "password": COORDINATOR_PASSWORD}).status_code == 403

    as_user(dash, "omar")
    assert [g["group"] for g in dash.get("/api/v1/dashboard/groups").json()["groups"]] == [A]
    actions = [(r["username"], r["action"]) for r in audit_rows(dash)]
    assert actions == [("admin", "user.create")]
    assert audit_rows(dash)[0]["user_id"] is not None


def test_there_is_only_one_admin(dash):
    with pytest.raises(asyncpg.UniqueViolationError):
        make_user(url(dash), "second-admin", role="admin")
    # And nothing in the API can make one: there is no role field anywhere.
    as_user(dash, "admin", ADMIN_PASSWORD)
    coordinator = make_user(url(dash), "omar")
    assert dash.patch(f"/api/v1/admin/users/{coordinator}", json={"role": "admin"}, headers=DASH).status_code == 400


def test_the_admin_cannot_lock_themselves_out(dash):
    as_user(dash, "admin", ADMIN_PASSWORD)
    me = user_id(dash, "admin")
    assert dash.patch(f"/api/v1/admin/users/{me}", json={"status": "disabled"}, headers=DASH).status_code == 409
    for action in ("approve", "reject"):
        assert dash.post(f"/api/v1/admin/users/{me}/{action}", headers=DASH).status_code == 409
    assert dash.post(f"/api/v1/admin/users/{me}/password", json={"password": NEW_PASSWORD}, headers=DASH).status_code == 409
    assert dash.put(f"/api/v1/admin/users/{me}/groups", json={"groupIds": []}, headers=DASH).status_code == 409
    renamed = dash.patch(f"/api/v1/admin/users/{me}", json={"displayName": "Boss"}, headers=DASH)
    assert renamed.status_code == 200 and renamed.json()["displayName"] == "Boss"
    assert dash.get("/api/v1/auth/me").json()["role"] == "admin"


def test_disabling_signs_the_coordinator_out_at_once_and_enabling_lets_them_back(dash):
    omar = make_user(url(dash), "omar", groups=(A,))
    as_user(dash, "omar")
    omar_cookie = dict(dash.cookies)
    as_user(dash, "admin", ADMIN_PASSWORD)
    disabled = dash.patch(f"/api/v1/admin/users/{omar}", json={"status": "disabled"}, headers=DASH)
    assert disabled.status_code == 200 and disabled.json()["status"] == "disabled"
    assert run_sql(url(dash), "SELECT count(*) AS n FROM admin_sessions WHERE user_id = $1", uuid.UUID(omar))[0]["n"] == 0

    dash.cookies.clear()
    dash.cookies.update(omar_cookie)
    assert dash.get("/api/v1/auth/me").status_code == 401
    assert login(dash, "omar", COORDINATOR_PASSWORD).json()["details"] == {"reason": "disabled"}

    as_user(dash, "admin", ADMIN_PASSWORD)
    assert dash.patch(f"/api/v1/admin/users/{omar}", json={"status": "active"}, headers=DASH).status_code == 200
    as_user(dash, "omar")
    assert [a["action"] for a in audit_rows(dash)] == ["user.disable", "user.enable"]


def test_a_pending_account_is_approved_not_enabled(dash):
    pending = make_user(url(dash), "newbie", status="pending")
    as_user(dash, "admin", ADMIN_PASSWORD)
    assert dash.patch(f"/api/v1/admin/users/{pending}", json={"status": "active"}, headers=DASH).status_code == 409


def test_a_password_reset_signs_the_coordinator_out(dash):
    omar = make_user(url(dash), "omar")
    as_user(dash, "omar")
    as_user(dash, "admin", ADMIN_PASSWORD)
    assert dash.post(f"/api/v1/admin/users/{omar}/password", json={"password": "short"}, headers=DASH).status_code == 400
    assert dash.post(f"/api/v1/admin/users/{omar}/password", json={"password": NEW_PASSWORD}, headers=DASH).status_code == 200
    assert run_sql(url(dash), "SELECT count(*) AS n FROM admin_sessions WHERE user_id = $1", uuid.UUID(omar))[0]["n"] == 0
    dash.cookies.clear()
    assert login(dash, "omar", COORDINATOR_PASSWORD).status_code == 401
    as_user(dash, "omar", NEW_PASSWORD)
    assert NEW_PASSWORD not in str(audit_rows(dash))


def test_anyone_changes_their_own_password_and_other_sessions_end(dash, settings, clock):
    make_user(url(dash), "omar")
    with build(settings, clock) as other_browser:
        as_user(other_browser, "omar")
        as_user(dash, "omar")
        body = {"currentPassword": "wrong one", "newPassword": NEW_PASSWORD}
        assert dash.post("/api/v1/auth/password", json=body, headers=DASH).status_code == 400
        body["currentPassword"] = COORDINATOR_PASSWORD
        assert dash.post("/api/v1/auth/password", json=body, headers=DASH).status_code == 200
        assert dash.get("/api/v1/auth/me").status_code == 200              # this browser stays in
        assert other_browser.get("/api/v1/auth/me").status_code == 401     # the other is signed out
    dash.cookies.clear()
    as_user(dash, "omar", NEW_PASSWORD)


def test_group_assignments_count_at_once(dash):
    for group in (A, B, C):
        sync(dash, group=group, date="2026-09-10")
    omar = make_user(url(dash), "omar", groups=(A,))
    as_user(dash, "omar")
    omar_cookie = dict(dash.cookies)
    assert dash.get("/api/v1/dashboard/recordings").json()["total"] == 1

    as_user(dash, "admin", ADMIN_PASSWORD)
    assigned = dash.put(f"/api/v1/admin/users/{omar}/groups", headers=DASH,
                        json={"groupIds": [group_id(dash, B), group_id(dash, C), group_id(dash, C)]})
    assert assigned.status_code == 200 and [g["name"] for g in assigned.json()["groups"]] == [B, C]
    assert dash.put(f"/api/v1/admin/users/{omar}/groups", json={"groupIds": ["not-a-uuid"]}, headers=DASH).status_code == 400

    dash.cookies.clear()
    dash.cookies.update(omar_cookie)                                         # the same session as before
    assert {r["group"] for r in dash.get("/api/v1/dashboard/recordings").json()["items"]} == {B, C}
    assert [g["name"] for g in dash.get("/api/v1/auth/me").json()["groups"]] == [B, C]


def test_coordinators_cannot_use_the_admin_tools_or_see_agents(dash):
    make_user(url(dash), "omar", groups=(A,))
    register(dash, "PC-A")
    as_user(dash, "omar")
    someone = user_id(dash, "admin")
    for path in ("/api/v1/admin/users", f"/api/v1/admin/users/{someone}", "/api/v1/admin/groups",
                 "/api/v1/dashboard/agents", f"/api/v1/dashboard/agents/{uuid.uuid4()}/jobs"):
        assert dash.get(path).status_code == 403, path
    assert dash.post("/api/v1/admin/users", headers=DASH, json={
        "username": "sneaky", "displayName": "S", "password": COORDINATOR_PASSWORD}).status_code == 403
    assert dash.post(f"/api/v1/admin/users/{someone}/approve", headers=DASH).status_code == 403
    assert dash.post("/api/v1/admin/groups", json={"name": "X1"}, headers=DASH).status_code == 403
    assert run_sql(url(dash), "SELECT count(*) AS n FROM users")[0]["n"] == 2


# =========================================================================== groups


def test_the_admin_registers_labels_and_archives_groups(dash):
    sync(dash, group=A, date="2026-09-10")
    make_user(url(dash), "omar", groups=(A,))
    as_user(dash, "admin", ADMIN_PASSWORD)
    created = dash.post("/api/v1/admin/groups", json={"name": "NEW_GROUP_1", "displayName": "New group"}, headers=DASH)
    assert created.status_code == 201 and created.json()["displayName"] == "New group"
    assert dash.post("/api/v1/admin/groups", json={"name": "NEW_GROUP_1"}, headers=DASH).status_code == 409
    assert dash.post("/api/v1/admin/groups", json={"name": "bad/name"}, headers=DASH).status_code == 400

    listed = {g["group"]: g for g in dash.get("/api/v1/admin/groups").json()["groups"]}
    assert listed["NEW_GROUP_1"]["recordings"] == 0 and listed["NEW_GROUP_1"]["lastSessionDate"] is None
    assert [c["username"] for c in listed[A]["coordinators"]] == ["omar"]

    a_id = listed[A]["id"]
    archived = dash.patch(f"/api/v1/admin/groups/{a_id}", json={"archived": True, "displayName": "Old S7"}, headers=DASH)
    assert archived.json() == {"id": a_id, "name": A, "displayName": "Old S7", "archived": True}
    assert dash.patch(f"/api/v1/admin/groups/{a_id}", json={"archived": "yes"}, headers=DASH).status_code == 400
    assert dash.patch(f"/api/v1/admin/groups/{a_id}", json={"name": "RENAMED"}, headers=DASH).status_code == 400
    # The admin still sees it (marked); assigning it again is refused.
    assert {g["group"]: g["archived"] for g in dash.get("/api/v1/dashboard/groups").json()["groups"]}[A] is True
    omar = user_id(dash, "omar")
    assert dash.put(f"/api/v1/admin/users/{omar}/groups", json={"groupIds": [a_id]}, headers=DASH).status_code == 400

    as_user(dash, "omar")                                                    # an archived group drops out of view
    assert dash.get("/api/v1/dashboard/groups").json()["groups"] == []
    assert dash.get("/api/v1/dashboard/recordings").json()["total"] == 0


def test_n8n_registers_new_groups_by_itself(dash):
    sync(dash, group="BRAND_NEW_S1", date="2026-09-10")
    sync(dash, group="BRAND_NEW_S1", date="2026-09-11")
    rid = sync(dash, group=A, date="2026-09-10")["id"]
    moved = dash.patch(f"/api/v1/recordings/{rid}", json={"group": "MOVED_TO_S2"}, headers=client_headers())
    assert moved.status_code == 200
    names = [r["name"] for r in run_sql(url(dash), "SELECT name FROM groups ORDER BY name")]
    assert names == ["BRAND_NEW_S1", A, "MOVED_TO_S2"]
    # n8n's own groups list is still built from the recordings, as before.
    n8n = dash.get("/api/v1/groups", headers=client_headers()).json()
    assert n8n == {"groups": ["BRAND_NEW_S1", "MOVED_TO_S2"], "count": 2}


# =========================================================================== what a coordinator sees


@pytest.fixture
def scoped(dash, clock):  # noqa: ANN001, ANN201
    """Recordings in A (two), B and C; omar coordinates A, nada coordinates B, idle has no group."""
    ids = {
        "a1": sync(dash, group=A, date="2026-09-10", startTime="18:00", link=DRIVE_LINK)["id"],
        "a2": sync(dash, group=A, date="2026-09-11", startTime="18:00", link=ZOOM_LINK)["id"],
        "b1": sync(dash, group=B, date="2026-09-10", startTime="20:00", link=DRIVE_LINK)["id"],
        "c1": sync(dash, group=C, date="2026-09-09")["id"],
    }
    make_user(url(dash), "omar", groups=(A,))
    make_user(url(dash), "nada", groups=(B,))
    make_user(url(dash), "idle")                                             # no groups at all
    return ids


def test_a_coordinator_sees_only_their_groups_everywhere(dash, scoped):
    as_user(dash, "omar")
    everything = dash.get("/api/v1/dashboard/recordings").json()
    assert {r["id"] for r in everything["items"]} == {scoped["a1"], scoped["a2"]} and everything["total"] == 2
    for params in ({"group": B}, {"group": C}, {"date": "2026-09-09"}, {"link": "missing"}):
        assert dash.get("/api/v1/dashboard/recordings", params=params).json()["total"] == 0, params
    for key in ("b1", "c1"):
        assert dash.get(f"/api/v1/dashboard/recordings/{scoped[key]}").status_code == 404
    assert dash.get(f"/api/v1/dashboard/recordings/{scoped['a1']}").status_code == 200

    assert [g["group"] for g in dash.get("/api/v1/dashboard/groups").json()["groups"]] == [A]
    overview = dash.get("/api/v1/dashboard/overview").json()
    assert overview["recordings"] == {"total": 2, "pending": 2, "onLms": 0, "missingLink": 0}
    assert (overview["agents"], overview["jobs"], overview["groups"]) == (None, None, 1)
    me = dash.get("/api/v1/auth/me").json()
    assert [g["name"] for g in me["groups"]] == [A]

    text = " ".join(dash.get(p).text for p in ("/api/v1/dashboard/recordings", "/api/v1/dashboard/groups",
                                               "/api/v1/dashboard/overview", "/api/v1/auth/me"))
    assert B not in text and C not in text


def test_a_coordinator_without_groups_sees_nothing(dash, scoped):
    as_user(dash, "idle")
    assert dash.get("/api/v1/dashboard/recordings").json() == {"items": [], "total": 0, "page": 1, "pageSize": 25,
                                                               "sort": "updated"}
    assert dash.get("/api/v1/dashboard/groups").json()["groups"] == []
    assert dash.get("/api/v1/dashboard/overview").json()["recordings"]["total"] == 0


def test_a_coordinator_cannot_change_other_groups_recordings(dash, scoped):
    as_user(dash, "omar")
    b1 = scoped["b1"]
    assert dash.patch(f"/api/v1/dashboard/recordings/{b1}", json={"fileName": "x.mp4"}, headers=DASH).status_code == 404
    assert dash.post(f"/api/v1/dashboard/recordings/{b1}/attach", json={"dryRun": True}, headers=DASH).status_code == 404
    assert run_sql(url(dash), "SELECT count(*) AS n FROM jobs")[0]["n"] == 0
    assert run_sql(url(dash), "SELECT file_name FROM recordings WHERE id = $1", uuid.UUID(b1))[0]["file_name"] is None

    # Their own: yes; moving it into someone else's group: no.
    a1 = scoped["a1"]
    edited = dash.patch(f"/api/v1/dashboard/recordings/{a1}", json={"fileName": "mine.mp4"}, headers=DASH)
    assert edited.status_code == 200 and edited.json()["changed"] == ["fileName"]
    moved = dash.patch(f"/api/v1/dashboard/recordings/{a1}", json={"group": B}, headers=DASH)
    assert moved.status_code == 403
    assert dash.patch(f"/api/v1/dashboard/recordings/{a1}", json={"group": "SOMEWHERE_NEW"}, headers=DASH).status_code == 403
    assert run_sql(url(dash), "SELECT group_name FROM recordings WHERE id = $1", uuid.UUID(a1))[0]["group_name"] == A
    rows = audit_rows(dash)
    assert [(r["username"], r["action"]) for r in rows] == [("omar", "recording.update")]
    assert str(rows[0]["user_id"]) == user_id(dash, "omar")


def test_a_coordinator_cancels_only_their_own_groups_jobs(dash, scoped):
    as_user(dash, "admin", ADMIN_PASSWORD)
    for key in ("a1", "b1"):
        assert dash.post(f"/api/v1/dashboard/recordings/{scoped[key]}/attach", json={"dryRun": True}, headers=DASH).status_code == 202
    as_user(dash, "omar")
    assert dash.post(f"/api/v1/dashboard/recordings/{scoped['b1']}/cancel", headers=DASH).status_code == 404
    assert run_sql(url(dash), "SELECT status FROM jobs WHERE recording_id = $1", uuid.UUID(scoped["b1"]))[0]["status"] == "queued"
    mine = dash.post(f"/api/v1/dashboard/recordings/{scoped['a1']}/cancel", headers=DASH)
    assert mine.status_code == 200 and mine.json()["status"] == "cancelled"
    assert [(r["username"], r["action"]) for r in audit_rows(dash)][-1] == ("omar", "recording.cancel")


def test_a_coordinator_attaches_their_own_recording(dash, scoped):
    _, token = register(dash, "PC-A")
    as_user(dash, "omar")
    with connect(dash, token) as ws:
        hello(ws)
        response = dash.post(f"/api/v1/dashboard/recordings/{scoped['a1']}/attach", json={"dryRun": True}, headers=DASH)
        assert response.status_code == 202, response.text
        assert ws.receive_json()["type"] == "job.assign"
    info = dash.get(f"/api/v1/dashboard/recordings/{scoped['a1']}").json()
    assert info["jobs"][0]["dryRun"] is True and info["audit"][0]["username"] == "omar"


def test_a_coordinator_moves_a_recording_between_their_own_groups(dash, scoped):
    run_sql(url(dash), "INSERT INTO user_groups (user_id, group_id, assigned_at) SELECT u.id, g.id, now() "
                       "FROM users u, groups g WHERE u.username = 'omar' AND g.name = $1", C)
    as_user(dash, "omar")
    moved = dash.patch(f"/api/v1/dashboard/recordings/{scoped['a1']}", json={"group": C}, headers=DASH)
    assert moved.status_code == 200 and moved.json()["group"] == C


def test_the_admin_still_sees_and_changes_everything(dash, scoped):
    as_user(dash, "admin", ADMIN_PASSWORD)
    assert dash.get("/api/v1/dashboard/recordings").json()["total"] == 4
    assert len(dash.get("/api/v1/dashboard/groups").json()["groups"]) == 3
    assert dash.get("/api/v1/dashboard/overview").json()["agents"] == {"total": 0, "online": 0, "busy": 0}
    moved = dash.patch(f"/api/v1/dashboard/recordings/{scoped['b1']}", json={"group": "ADMIN_MADE_S1"}, headers=DASH)
    assert moved.status_code == 200
    assert run_sql(url(dash), "SELECT 1 FROM groups WHERE name = 'ADMIN_MADE_S1'")        # registered on the way


def test_the_n8n_api_ignores_roles_entirely(dash, scoped):
    assert dash.get("/api/v1/recordings", headers=client_headers()).json()["count"] == 4
    as_user(dash, "omar")
    assert dash.get("/api/v1/recordings").status_code == 401                 # a dashboard session is not a key


# =========================================================================== operator side


def test_create_admin_from_the_command_line(clean_db, monkeypatch, capsys):
    from central_backend import cli
    from central_backend.auth import hash_password

    monkeypatch.setenv("CENTRAL_DATABASE_URL", clean_db)
    answers = iter([ADMIN_PASSWORD, ADMIN_PASSWORD])
    monkeypatch.setattr("getpass.getpass", lambda prompt="": next(answers))
    assert cli.main(["create-admin", "--username", "Boss", "--display-name", "The Boss"]) == 0
    out = capsys.readouterr()
    assert "Admin account 'boss' created" in out.out and ADMIN_PASSWORD not in out.out + out.err
    rows = run_sql(clean_db, "SELECT username, display_name, role, status, password_hash FROM users")
    assert [(r["username"], r["display_name"], r["role"], r["status"]) for r in rows] == [("boss", "The Boss", "admin", "active")]
    assert rows[0]["password_hash"].startswith("scrypt$32768$")

    answers = iter([ADMIN_PASSWORD, ADMIN_PASSWORD])
    assert cli.main(["create-admin", "--username", "other"]) == 1           # there can be only one
    assert "already an admin" in capsys.readouterr().err

    run_sql(clean_db, "DELETE FROM users")
    stored = hash_password(ADMIN_PASSWORD, n=2**14)
    monkeypatch.setenv("CENTRAL_ADMIN_USERS", f"Admin:{stored}")
    assert cli.main(["create-admin", "--from-env"]) == 0
    assert "no longer read" in capsys.readouterr().out
    assert [(r["username"], r["password_hash"]) for r in run_sql(clean_db, "SELECT username, password_hash FROM users")] \
        == [("admin", stored)]


def test_the_import_never_guesses_which_old_admin_moves(clean_db, monkeypatch, capsys):
    """CENTRAL_ADMIN_USERS could hold several admins; now there is one. The operator picks it, and
    every entry left behind is named."""
    from central_backend import cli
    from central_backend.auth import hash_password

    monkeypatch.setenv("CENTRAL_DATABASE_URL", clean_db)
    stored = hash_password(ADMIN_PASSWORD, n=2**14)
    imported = lambda: [r["username"] for r in run_sql(clean_db, "SELECT username FROM users")]  # noqa: E731
    monkeypatch.setenv("CENTRAL_ADMIN_USERS", f"admin:{stored},ops:{stored},bad!name:{stored}")

    assert cli.main(["create-admin", "--from-env"]) == 1                     # several: no guess
    err = capsys.readouterr().err
    assert "has 2 accounts (admin, ops)" in err and "--username" in err and "skipped 'bad!name'" in err
    assert cli.main(["create-admin", "--from-env", "--username", "nobody"]) == 1
    assert "'nobody' is not in CENTRAL_ADMIN_USERS (it has: admin, ops)" in capsys.readouterr().err
    assert imported() == []

    assert cli.main(["create-admin", "--from-env", "--username", "OPS"]) == 0
    out = capsys.readouterr()
    assert "Admin account 'ops' created" in out.out and "Not imported: admin." in out.out
    assert stored not in out.out + out.err
    assert imported() == ["ops"]

    run_sql(clean_db, "DELETE FROM users")
    monkeypatch.setenv("CENTRAL_ADMIN_USERS", f"mo:{stored}")                # a name the old setting allowed
    assert cli.main(["create-admin", "--from-env"]) == 0
    assert imported() == ["mo"]


def test_create_admin_refuses_a_mismatched_or_short_password(clean_db, monkeypatch, capsys):
    from central_backend import cli

    monkeypatch.setenv("CENTRAL_DATABASE_URL", clean_db)
    for answers in ([ADMIN_PASSWORD, ADMIN_PASSWORD + "x"], ["short", "short"]):
        it = iter(answers)
        monkeypatch.setattr("getpass.getpass", lambda prompt="", it=it: next(it))
        assert cli.main(["create-admin"]) == 1
    monkeypatch.delenv("CENTRAL_ADMIN_USERS", raising=False)
    assert cli.main(["create-admin", "--from-env"]) == 1
    assert run_sql(clean_db, "SELECT count(*) AS n FROM users")[0]["n"] == 0
    assert ADMIN_PASSWORD not in capsys.readouterr().err


def test_startup_says_what_is_missing(settings, clock, caplog, monkeypatch):
    caplog.set_level(logging.INFO, logger=LOGGER_NAME)
    monkeypatch.setenv("CENTRAL_ADMIN_USERS", "admin:scrypt$old")
    with build(settings, clock, admin=False):
        pass
    assert "auth.no_admin" in caplog.text and "auth.legacy_admin_users_ignored" in caplog.text
    assert "scrypt$old" not in caplog.text


def test_migration_0005_registers_the_groups_recordings_already_had(database_url):
    from alembic import command
    from alembic.config import Config

    from central_backend.cli import BACKEND_DIR

    name = f"m5_{uuid.uuid4().hex[:8]}"
    admin_url = database_url.replace("postgresql+asyncpg://", "postgresql://")

    async def ddl(sql: str) -> None:
        conn = await asyncpg.connect(admin_url)
        try:
            await conn.execute(sql)
        finally:
            await conn.close()

    asyncio.run(ddl(f"CREATE DATABASE {name}"))
    fresh = database_url.rsplit("/", 1)[0] + f"/{name}"
    try:
        config = Config(str(BACKEND_DIR / "alembic.ini"))
        config.set_main_option("script_location", str(BACKEND_DIR / "migrations"))
        config.attributes["database_url"] = fresh
        command.upgrade(config, "0004_recording_operations")
        for group, day in ((A, "2026-09-01"), (A, "2026-09-02"), (B, "2026-09-03")):
            run_sql(fresh, "INSERT INTO recordings (id, group_name, session_date) VALUES (gen_random_uuid(), $1, $2::date)",
                    group, date.fromisoformat(day))
        command.upgrade(config, "head")
        assert [r["name"] for r in run_sql(fresh, "SELECT name FROM groups ORDER BY name")] == [A, B]
        assert run_sql(fresh, "SELECT count(*) AS n FROM users")[0]["n"] == 0
    finally:
        asyncio.run(ddl(f"DROP DATABASE {name} WITH (FORCE)"))

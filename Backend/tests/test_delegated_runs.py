"""The classes one PC runs for several coordinators: who is turned on, their sign-ins, and the plan."""

from __future__ import annotations

import secrets
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from central_backend.auth import AuthSettings
from central_backend.main import create_app
from central_backend.user_data import SecretBox
from test_dashboard import ADMIN_PASSWORD, COORDINATOR_PASSWORD, DASH, login, make_user, run_sql

MONA_LMS = "made-up LMS password for Mona"
SAMI_LMS = "made-up LMS password for Sami"


@pytest.fixture
def box() -> SecretBox:
    return SecretBox(secrets.token_bytes(32))


@pytest.fixture
def dash(settings, clock, box):  # noqa: ANN001, ANN201
    if not run_sql(settings.database_url, "SELECT 1 FROM users WHERE role = 'admin'"):
        make_user(settings.database_url, "admin", ADMIN_PASSWORD, role="admin", display_name="The Admin")
    app = create_app(settings, clock=clock, run_background=False, configure_logs=False, auth_settings=AuthSettings(),
                     dashboard_dist=Path("__no_dashboard_build__"), secret_box=box)
    with TestClient(app) as client:
        yield client


def as_user(client: TestClient, username: str, password: str = COORDINATOR_PASSWORD) -> TestClient:
    client.cookies.clear()
    assert login(client, username, password).status_code == 200
    return client


def coordinator(client: TestClient, username: str, *, groups: tuple[str, ...], lms_email: str, lms_password: str,
                zoom: tuple[str, ...] = ()) -> str:
    """A coordinator with groups, one LMS sign-in and their own Zoom accounts, saved as themselves."""
    user_id = make_user(client.app.state.settings.database_url, username, COORDINATOR_PASSWORD, groups=groups)
    as_user(client, username)
    saved = client.post("/api/v1/me/lms-accounts", headers=DASH,
                        json={"label": username.title(), "email": lms_email, "password": lms_password})
    assert saved.status_code == 201, saved.text
    if zoom:
        # The app keeps this PC's Zoom accounts here: one per group, each with the link its
        # classes open. Nobody types a link twice.
        kept = client.put("/api/v1/me/zoom-accounts", headers=DASH, json={"accounts": [
            {"accountId": group, "label": group, "group": group, "zoomEmail": f"{username}@zoom.example.com",
             "meetingUrl": f"https://zoom.us/j/9147310849{n}", "password": f"made-up Zoom password for {username}",
             "active": n == 0}
            for n, group in enumerate(zoom)
        ]})
        assert kept.status_code == 200, kept.text
    return user_id


@pytest.fixture
def two(dash):  # noqa: ANN001, ANN201
    """Two coordinators, each with their own group and their own LMS sign-in, and the admin signed in."""
    mona = coordinator(dash, "mona", groups=("CAI5_AIS4_S7",), lms_email="mona@example.com", lms_password=MONA_LMS,
                       zoom=("CAI5_AIS4_S7",))
    sami = coordinator(dash, "sami", groups=("CAI5_AIS4_S8",), lms_email="sami@example.com", lms_password=SAMI_LMS,
                       zoom=("CAI5_AIS4_S8",))
    as_user(dash, "admin", ADMIN_PASSWORD)
    return mona, sami


def zoom_accounts(client: TestClient, coordinator_id: str):  # noqa: ANN201
    answer = client.get(f"/api/v1/admin/users/{coordinator_id}/zoom-accounts")
    assert answer.status_code == 200, answer.text
    return answer.json()["accounts"]


def zoom_id(client: TestClient, coordinator_id: str, account_id: str) -> str:
    return next(a["id"] for a in zoom_accounts(client, coordinator_id) if a["accountId"] == account_id)


def turn_on(client: TestClient, coordinator_id: str, zoom: str | None = None, **body):  # noqa: ANN003, ANN201
    """Run this coordinator's classes, optionally with one of their own Zoom accounts named."""
    if zoom is not None:
        body["zoomAccountId"] = zoom_id(client, coordinator_id, zoom)
    return client.put(f"/api/v1/admin/delegations/{coordinator_id}", headers=DASH, json={"enabled": True, **body})


def imported(client: TestClient, coordinator_id: str, classes, source: str = "lms"):  # noqa: ANN001, ANN201
    return client.post("/api/v1/admin/run-plan/import", headers=DASH,
                       json={"coordinatorId": coordinator_id, "classes": classes, "source": source})


def plan(client: TestClient, **params):  # noqa: ANN003, ANN201
    response = client.get("/api/v1/admin/run-plan", params=params)
    assert response.status_code == 200, response.text
    return response.json()


# =========================================================================== who may ask


@pytest.mark.parametrize(
    "method, path",
    [
        ("get", "/api/v1/admin/delegations"),
        ("get", "/api/v1/admin/run-plan"),
        ("get", "/api/v1/admin/users/00000000-0000-0000-0000-000000000000/lms-accounts"),
    ],
)
def test_only_the_admin_sees_any_of_this(dash, two, method, path):
    mona, _ = two
    as_user(dash, "mona")
    assert getattr(dash, method)(path).status_code == 403


def test_a_write_needs_the_dashboard_header(dash, two):
    mona, _ = two
    assert dash.put(f"/api/v1/admin/delegations/{mona}", json={"enabled": True}).status_code == 403


# =========================================================================== turning a coordinator on


def test_every_coordinator_is_listed_with_their_groups_and_sign_ins_and_none_is_run_yet(dash, two):
    mona, sami = two
    listed = dash.get("/api/v1/admin/delegations").json()["delegations"]
    assert [d["username"] for d in listed] == ["mona", "sami"]
    assert [d["enabled"] for d in listed] == [False, False]
    assert [g["name"] for g in listed[0]["groups"]] == ["CAI5_AIS4_S7"]
    assert listed[0]["lmsAccount"]["email"] == "mona@example.com"
    assert MONA_LMS not in listed[0]["lmsAccount"]["email"] and "password" not in str(listed)


def test_turning_one_on_keeps_the_lms_and_zoom_account_their_classes_go_up_under(dash, two):
    mona, _ = two
    account = dash.get(f"/api/v1/admin/users/{mona}/lms-accounts").json()["accounts"][0]["id"]
    answer = turn_on(dash, mona, zoom="CAI5_AIS4_S7", lmsAccountId=account)
    assert answer.status_code == 200, answer.text
    assert answer.json()["enabled"] is True and answer.json()["zoomAccount"] == "CAI5_AIS4_S7"

    listed = {d["username"]: d for d in dash.get("/api/v1/admin/delegations").json()["delegations"]}
    assert listed["mona"]["enabled"] is True and listed["mona"]["zoomAccount"] == "CAI5_AIS4_S7"
    assert listed["sami"]["enabled"] is False


def test_another_coordinators_lms_account_cannot_be_pinned_to_this_one(dash, two):
    mona, sami = two
    theirs = dash.get(f"/api/v1/admin/users/{sami}/lms-accounts").json()["accounts"][0]["id"]
    assert turn_on(dash, mona, lmsAccountId=theirs).status_code == 404


def test_a_coordinator_who_is_not_active_is_not_run(dash, two):
    mona, _ = two
    run_sql(dash.app.state.settings.database_url, "UPDATE users SET status = 'disabled' WHERE username = 'mona'")
    assert turn_on(dash, mona).status_code == 409


# =========================================================================== their LMS sign-in


def test_the_admin_reads_a_delegated_coordinators_sign_in_and_it_is_written_down(dash, two):
    mona, _ = two
    account = dash.get(f"/api/v1/admin/users/{mona}/lms-accounts").json()["accounts"][0]["id"]
    turn_on(dash, mona)

    secret = dash.post(f"/api/v1/admin/users/{mona}/lms-accounts/{account}/secret", headers=DASH)
    assert secret.status_code == 200, secret.text
    assert secret.json()["email"] == "mona@example.com" and secret.json()["password"] == MONA_LMS
    assert secret.headers["Cache-Control"] == "no-store"

    written = run_sql(dash.app.state.settings.database_url,
                      "SELECT username, action, details FROM admin_audit_log WHERE action = 'lms_secret.read'")
    assert len(written) == 1 and written[0]["username"] == "admin"
    assert "mona" in written[0]["details"] and MONA_LMS not in written[0]["details"]


def test_a_coordinator_who_was_not_turned_on_keeps_their_password(dash, two):
    mona, _ = two
    account = dash.get(f"/api/v1/admin/users/{mona}/lms-accounts").json()["accounts"][0]["id"]
    refused = dash.post(f"/api/v1/admin/users/{mona}/lms-accounts/{account}/secret", headers=DASH)
    assert refused.status_code == 403
    assert not run_sql(dash.app.state.settings.database_url,
                       "SELECT 1 FROM admin_audit_log WHERE action = 'lms_secret.read'")


def test_turning_a_coordinator_off_again_closes_their_sign_in(dash, two):
    mona, _ = two
    account = dash.get(f"/api/v1/admin/users/{mona}/lms-accounts").json()["accounts"][0]["id"]
    turn_on(dash, mona)
    assert dash.post(f"/api/v1/admin/users/{mona}/lms-accounts/{account}/secret", headers=DASH).status_code == 200
    dash.put(f"/api/v1/admin/delegations/{mona}", headers=DASH, json={"enabled": False})
    assert dash.post(f"/api/v1/admin/users/{mona}/lms-accounts/{account}/secret", headers=DASH).status_code == 403


def test_one_coordinators_sign_in_is_never_read_through_another(dash, two):
    mona, sami = two
    turn_on(dash, mona)
    theirs = dash.get(f"/api/v1/admin/users/{sami}/lms-accounts").json()["accounts"][0]["id"]
    assert dash.post(f"/api/v1/admin/users/{mona}/lms-accounts/{theirs}/secret", headers=DASH).status_code == 404


def test_no_lms_password_reaches_the_log(dash, two, caplog):
    mona, _ = two
    account = dash.get(f"/api/v1/admin/users/{mona}/lms-accounts").json()["accounts"][0]["id"]
    turn_on(dash, mona)
    with caplog.at_level(0):
        dash.post(f"/api/v1/admin/users/{mona}/lms-accounts/{account}/secret", headers=DASH)
    assert MONA_LMS not in caplog.text


# =========================================================================== the plan


LESSON = {"group": "CAI5_AIS4_S7", "date": "2026-09-22", "startTime": "19:00", "title": "36 · Technical"}


def test_what_the_lms_listed_becomes_the_plan(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    answer = imported(dash, mona, [LESSON, {**LESSON, "date": "2026-09-29", "title": "39 · Technical"}])
    assert answer.status_code == 201 and answer.json()["added"] == 2

    classes = plan(dash)["classes"]
    assert [(c["group"], c["date"], c["startTime"]) for c in classes] == [
        ("CAI5_AIS4_S7", "2026-09-22", "19:00"), ("CAI5_AIS4_S7", "2026-09-29", "19:00")]
    # The LMS gives no Zoom link, but her own Zoom account for the group does, so nothing is owed.
    assert not any(c["needsLink"] for c in classes)
    assert all(c["meetingUrl"] == "https://zoom.us/j/91473108490" for c in classes)
    assert all(c["zoomAccount"] == "CAI5_AIS4_S7" for c in classes)


def test_the_same_class_listed_again_is_the_same_line_and_keeps_what_a_person_put_on_it(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [LESSON])
    first = plan(dash)["classes"][0]
    dash.patch(f"/api/v1/admin/run-plan/{first['id']}", headers=DASH,
               json={"meetingUrl": "https://zoom.us/j/91473108490", "zoomAccount": "CAI5_AIS4_S7"})

    again = imported(dash, mona, [{**LESSON, "title": "36 · Technical (moved)"}])
    assert again.json() == {"coordinatorId": mona, "added": 0, "updated": 1, "total": 1}
    classes = plan(dash)["classes"]
    assert len(classes) == 1
    assert classes[0]["meetingUrl"] == "https://zoom.us/j/91473108490"
    assert classes[0]["zoomAccount"] == "CAI5_AIS4_S7"
    assert classes[0]["title"] == "36 · Technical (moved)"
    assert classes[0]["needsLink"] is False


def test_a_groups_link_carries_on_to_the_classes_listed_after_it(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [LESSON])
    first = plan(dash)["classes"][0]
    dash.patch(f"/api/v1/admin/run-plan/{first['id']}", headers=DASH,
               json={"meetingUrl": "https://zoom.us/j/91473108490", "zoomAccount": "CAI5_AIS4_S7"})

    imported(dash, mona, [{**LESSON, "date": "2026-10-06", "title": "41 · Technical"}])
    latest = [c for c in plan(dash)["classes"] if c["date"] == "2026-10-06"][0]
    assert latest["meetingUrl"] == "https://zoom.us/j/91473108490"
    assert latest["zoomAccount"] == "CAI5_AIS4_S7"


def test_one_link_can_be_written_across_a_whole_group_at_once(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [LESSON, {**LESSON, "date": "2026-09-29"}, {**LESSON, "date": "2026-10-06"}])
    first = plan(dash)["classes"][0]
    answer = dash.patch(f"/api/v1/admin/run-plan/{first['id']}", headers=DASH,
                        json={"meetingUrl": "https://zoom.us/j/91473108490", "applyToGroup": True})
    assert answer.json()["alsoInGroup"] == 2
    assert all(c["meetingUrl"] == "https://zoom.us/j/91473108490" for c in plan(dash)["classes"])


def test_a_class_can_be_skipped_and_stops_asking_for_a_link(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [LESSON])
    first = plan(dash)["classes"][0]
    dash.patch(f"/api/v1/admin/run-plan/{first['id']}", headers=DASH, json={"status": "skipped", "note": "cancelled"})
    only = plan(dash)["classes"][0]
    assert only["status"] == "skipped" and only["needsLink"] is False and only["note"] == "cancelled"
    assert plan(dash, status="planned")["classes"] == []


def test_a_link_that_is_not_one_is_refused(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [LESSON])
    first = plan(dash)["classes"][0]
    for bad in ["http://zoom.us/j/1", "not a link", "https://zoom.us/j/1 2"]:
        assert dash.patch(f"/api/v1/admin/run-plan/{first['id']}", headers=DASH,
                          json={"meetingUrl": bad}).status_code == 400


# =========================================================================== two at once


def test_two_coordinators_run_side_by_side_each_with_their_own_pair(dash, two):
    mona, sami = two
    turn_on(dash, mona, zoom="CAI5_AIS4_S7")
    turn_on(dash, sami, zoom="CAI5_AIS4_S8")
    imported(dash, mona, [LESSON])
    imported(dash, sami, [{"group": "CAI5_AIS4_S8", "date": "2026-09-22", "startTime": "19:00", "title": "33"}])

    whole = plan(dash, **{"from": "2026-09-22", "to": "2026-09-22"})
    assert [(c["group"], c["startTime"], c["zoomAccount"]) for c in whole["classes"]] == [
        ("CAI5_AIS4_S7", "19:00", "CAI5_AIS4_S7"), ("CAI5_AIS4_S8", "19:00", "CAI5_AIS4_S8")]
    pairs = {p["username"]: p["lmsAccount"]["email"] for p in whole["coordinators"]}
    assert pairs == {"mona": "mona@example.com", "sami": "sami@example.com"}
    assert "password" not in str(whole)


def test_the_plan_narrows_to_the_coordinators_asked_for(dash, two):
    mona, sami = two
    turn_on(dash, mona)
    turn_on(dash, sami)
    imported(dash, mona, [LESSON])
    imported(dash, sami, [{"group": "CAI5_AIS4_S8", "date": "2026-09-23", "startTime": "19:00"}])

    assert len(plan(dash)["classes"]) == 2
    only_mona = plan(dash, coordinator=mona)["classes"]
    assert [c["group"] for c in only_mona] == ["CAI5_AIS4_S7"]
    both = dash.get("/api/v1/admin/run-plan", params=[("coordinator", mona), ("coordinator", sami)]).json()
    assert len(both["classes"]) == 2


def test_the_plan_narrows_to_a_window_of_days(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [LESSON, {**LESSON, "date": "2026-10-06"}])
    assert [c["date"] for c in plan(dash, **{"from": "2026-10-01"})["classes"]] == ["2026-10-06"]
    assert [c["date"] for c in plan(dash, **{"to": "2026-09-30"})["classes"]] == ["2026-09-22"]
    assert dash.get("/api/v1/admin/run-plan", params={"from": "2026-10-01", "to": "2026-09-01"}).status_code == 400


def test_a_coordinator_who_was_turned_off_drops_out_of_the_plan_but_keeps_their_classes(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [LESSON])
    dash.put(f"/api/v1/admin/delegations/{mona}", headers=DASH, json={"enabled": False})
    assert plan(dash)["classes"] == []
    assert len(plan(dash, onlyDelegated=False, coordinator=mona)["classes"]) == 1


def test_the_preferred_engine_is_kept_per_class_and_can_be_cleared(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    imported(dash, mona, [{**LESSON, "preferredEngine": "web"}])
    first = plan(dash)["classes"][0]
    assert first["preferredEngine"] == "web"
    cleared = dash.patch(f"/api/v1/admin/run-plan/{first['id']}", headers=DASH, json={"preferredEngine": "auto"})
    assert cleared.json()["preferredEngine"] is None


def test_a_timetable_longer_than_a_term_is_refused(dash, two):
    mona, _ = two
    turn_on(dash, mona)
    too_many = [{**LESSON, "date": f"2026-{(i % 12) + 1:02d}-{(i % 28) + 1:02d}"} for i in range(501)]
    assert imported(dash, mona, too_many).status_code == 400


# =========================================================================== their Zoom accounts


def test_a_coordinators_own_zoom_accounts_are_what_the_admin_picks_from(dash, two):
    mona, _ = two
    listed = {d["username"]: d for d in dash.get("/api/v1/admin/delegations").json()["delegations"]}
    accounts = listed["mona"]["zoomAccounts"]
    assert [(a["accountId"], a["group"], a["meetingUrl"]) for a in accounts] == [
        ("CAI5_AIS4_S7", "CAI5_AIS4_S7", "https://zoom.us/j/91473108490")]
    assert "password" not in str(accounts)            # a Zoom sign-in is never kept here


def test_another_coordinators_zoom_account_cannot_be_pinned_to_this_one(dash, two):
    mona, sami = two
    theirs = zoom_id(dash, sami, "CAI5_AIS4_S8")
    assert dash.put(f"/api/v1/admin/delegations/{mona}", headers=DASH,
                    json={"enabled": True, "zoomAccountId": theirs}).status_code == 404


def test_the_accounts_a_pc_sends_are_the_whole_set_so_one_removed_there_goes_here(dash, two):
    mona, _ = two
    as_user(dash, "mona")
    dash.put("/api/v1/me/zoom-accounts", headers=DASH, json={"accounts": [
        {"accountId": "CAI5_AIS4_S9", "label": "S9", "group": "CAI5_AIS4_S9",
         "meetingUrl": "https://zoom.us/j/914731084999", "active": True}]})
    as_user(dash, "admin", ADMIN_PASSWORD)

    assert [a["accountId"] for a in zoom_accounts(dash, mona)] == ["CAI5_AIS4_S9"]


def test_a_coordinator_with_no_zoom_account_for_the_group_is_asked_for_its_link(dash, two):
    mona, _ = two
    as_user(dash, "mona")
    dash.put("/api/v1/me/zoom-accounts", headers=DASH, json={"accounts": []})
    as_user(dash, "admin", ADMIN_PASSWORD)
    turn_on(dash, mona)
    imported(dash, mona, [LESSON])

    only = plan(dash)["classes"][0]
    assert only["needsLink"] is True and only["meetingUrl"] is None


def test_the_group_the_zoom_account_names_is_what_matches_not_the_one_in_use(dash, two):
    mona, _ = two
    as_user(dash, "mona")
    dash.put("/api/v1/me/zoom-accounts", headers=DASH, json={"accounts": [
        {"accountId": "OTHER", "label": "Other", "group": "OTHER_GROUP",
         "meetingUrl": "https://zoom.us/j/111111111", "active": True},
        {"accountId": "S7", "label": "S7", "group": "CAI5_AIS4_S7",
         "meetingUrl": "https://zoom.us/j/222222222", "active": False}]})
    as_user(dash, "admin", ADMIN_PASSWORD)
    turn_on(dash, mona)
    imported(dash, mona, [LESSON])

    only = plan(dash)["classes"][0]
    assert only["meetingUrl"] == "https://zoom.us/j/222222222"
    assert only["zoomAccount"] == "S7"


def test_a_link_put_on_a_class_by_hand_wins_over_the_accounts(dash, two):
    mona, _ = two
    turn_on(dash, mona, zoom="CAI5_AIS4_S7")
    imported(dash, mona, [LESSON])
    first = plan(dash)["classes"][0]
    dash.patch(f"/api/v1/admin/run-plan/{first['id']}", headers=DASH,
               json={"meetingUrl": "https://zoom.us/j/333333333"})

    imported(dash, mona, [LESSON])                      # the timetable is read again
    assert plan(dash)["classes"][0]["meetingUrl"] == "https://zoom.us/j/333333333"


def test_a_zoom_account_is_only_ever_the_users_own(dash, two):
    mona, sami = two
    as_user(dash, "mona")
    assert [a["accountId"] for a in dash.get("/api/v1/me/zoom-accounts").json()["accounts"]] == ["CAI5_AIS4_S7"]
    as_user(dash, "sami")
    assert [a["accountId"] for a in dash.get("/api/v1/me/zoom-accounts").json()["accounts"]] == ["CAI5_AIS4_S8"]


def test_a_meeting_link_that_is_not_one_is_refused_when_a_pc_sends_its_accounts(dash, two):
    as_user(dash, "mona")
    refused = dash.put("/api/v1/me/zoom-accounts", headers=DASH, json={"accounts": [
        {"accountId": "S7", "label": "S7", "meetingUrl": "zoom.us/j/1"}]})
    assert refused.status_code == 400


# =========================================================================== their Zoom sign-in


def zoom_secret(client: TestClient, coordinator_id: str, account_id: str):  # noqa: ANN201
    return client.post(f"/api/v1/admin/users/{coordinator_id}/zoom-accounts/{account_id}/secret", headers=DASH)


def test_a_delegated_coordinators_zoom_sign_in_is_given_to_the_pc_that_runs_them(dash, two):
    mona, _ = two
    turn_on(dash, mona, zoom="CAI5_AIS4_S7")
    answer = zoom_secret(dash, mona, zoom_id(dash, mona, "CAI5_AIS4_S7"))

    assert answer.status_code == 200, answer.text
    assert answer.json()["email"] == "mona@zoom.example.com"
    assert answer.json()["password"] == "made-up Zoom password for mona"
    assert answer.json()["accountId"] == "CAI5_AIS4_S7"
    assert answer.headers["Cache-Control"] == "no-store"

    written = run_sql(dash.app.state.settings.database_url,
                      "SELECT username, details FROM admin_audit_log WHERE action = 'zoom_secret.read'")
    assert len(written) == 1 and "mona" in written[0]["details"]
    assert "made-up Zoom password" not in written[0]["details"]


def test_a_coordinator_who_was_not_turned_on_keeps_their_zoom_password(dash, two):
    mona, _ = two
    assert zoom_secret(dash, mona, zoom_id(dash, mona, "CAI5_AIS4_S7")).status_code == 403


def test_one_coordinators_zoom_sign_in_is_never_read_through_another(dash, two):
    mona, sami = two
    turn_on(dash, mona, zoom="CAI5_AIS4_S7")
    assert zoom_secret(dash, mona, zoom_id(dash, sami, "CAI5_AIS4_S8")).status_code == 404


def test_an_account_with_no_zoom_password_says_so_rather_than_failing(dash, two):
    mona, _ = two
    as_user(dash, "mona")
    dash.put("/api/v1/me/zoom-accounts", headers=DASH, json={"accounts": [
        {"accountId": "CAI5_AIS4_S7", "label": "S7", "group": "CAI5_AIS4_S7",
         "meetingUrl": "https://zoom.us/j/91473108490", "password": "", "active": True}]})
    as_user(dash, "admin", ADMIN_PASSWORD)
    turn_on(dash, mona, zoom="CAI5_AIS4_S7")

    assert zoom_secret(dash, mona, zoom_id(dash, mona, "CAI5_AIS4_S7")).status_code == 404


def test_the_zoom_password_is_never_in_a_listing_only_whether_there_is_one(dash, two):
    mona, _ = two
    listed = dash.get(f"/api/v1/admin/users/{mona}/zoom-accounts").json()
    assert listed["accounts"][0]["hasPassword"] is True
    assert "made-up Zoom password" not in str(listed)

    delegations = dash.get("/api/v1/admin/delegations").json()
    assert "made-up Zoom password" not in str(delegations)


def test_a_pc_without_the_password_does_not_wipe_the_one_that_is_kept(dash, two):
    """A second PC has the account but never had its Zoom password; it must leave it alone."""
    mona, _ = two
    as_user(dash, "mona")
    dash.put("/api/v1/me/zoom-accounts", headers=DASH, json={"accounts": [
        {"accountId": "CAI5_AIS4_S7", "label": "S7", "group": "CAI5_AIS4_S7",
         "meetingUrl": "https://zoom.us/j/91473108490", "active": True}]})      # no password sent

    assert dash.get("/api/v1/me/zoom-accounts").json()["accounts"][0]["hasPassword"] is True
    secret = dash.post(f"/api/v1/me/zoom-accounts/{zoom_id_for_self(dash, 'CAI5_AIS4_S7')}/secret", headers=DASH)
    assert secret.json()["password"] == "made-up Zoom password for mona"


def test_a_person_reads_their_own_zoom_sign_in_but_not_anothers(dash, two):
    mona, sami = two
    theirs = zoom_id(dash, sami, "CAI5_AIS4_S8")
    as_user(dash, "mona")
    mine = zoom_id_for_self(dash, "CAI5_AIS4_S7")
    assert dash.post(f"/api/v1/me/zoom-accounts/{mine}/secret", headers=DASH).json()["password"] == "made-up Zoom password for mona"
    assert dash.post(f"/api/v1/me/zoom-accounts/{theirs}/secret", headers=DASH).status_code == 404


def zoom_id_for_self(client: TestClient, account_id: str) -> str:
    return next(a["id"] for a in client.get("/api/v1/me/zoom-accounts").json()["accounts"] if a["accountId"] == account_id)

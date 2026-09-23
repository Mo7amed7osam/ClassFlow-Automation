"""Starting a class's stage by hand, and the switches every worker reads.

Both exist because the dashboard is where the work is watched from. A class whose meeting did not
open, or whose LMS step failed for a reason since fixed, is started from there rather than by waiting
a day; and the switches a person holds beside a meeting in the Windows app - making the instructor
co-host, ending a class when it is over - are held there for the machines, which have no window.
"""

from __future__ import annotations

from conftest import CLIENT_KEY, register
from test_dashboard import ADMIN_PASSWORD, DASH, run_sql
# The two coordinators, their accounts and an admin signed in: the same ground the run plan's own
# tests stand on, imported rather than written twice.
from test_delegated_runs import as_user, box, dash, imported, turn_on, two  # noqa: F401

CLASS = {"group": "CAI5_AIS4_S7", "date": "2026-09-23", "startTime": "19:00", "title": "Freelancing Skills"}


def a_class(dash, coordinator_id, **fields):  # noqa: ANN001, ANN201
    """One imported class of a coordinator whose classes are run here, with its Zoom link filled in."""
    answer = imported(dash, coordinator_id, [{**CLASS, **fields}])
    assert answer.status_code == 201, answer.text
    plan = dash.get("/api/v1/admin/run-plan", params={"from": "2026-09-23", "to": "2026-09-23"}).json()
    return plan["classes"][0]


def ready(dash, coordinator_id: str):  # noqa: ANN001
    """Run this coordinator's classes, under their own LMS sign-in and their own Zoom account."""
    account = dash.get(f"/api/v1/admin/users/{coordinator_id}/lms-accounts").json()["accounts"][0]["id"]
    answer = turn_on(dash, coordinator_id, zoom="CAI5_AIS4_S7", lmsAccountId=account)
    assert answer.status_code == 200, answer.text


def run(dash, plan_id: str, stage: str, headers=DASH):  # noqa: ANN001, ANN201
    return dash.post(f"/api/v1/admin/run-plan/{plan_id}/run", headers=headers, json={"stage": stage})


# =========================================================================== running a stage now


def test_the_meeting_of_a_class_is_opened_now_and_the_job_is_the_scheduler_s_own(dash, two):
    mona, _ = two
    ready(dash, mona)
    item = a_class(dash, mona, meetingUrl="https://zoom.us/j/91473108491")

    answer = run(dash, item["id"], "class.run")
    assert answer.status_code == 202, answer.text
    body = answer.json()
    assert body["created"] is True
    assert body["type"] == "class.run"
    assert body["status"] == "queued"
    assert body["group"] == "CAI5_AIS4_S7"
    assert body["date"] == "2026-09-23"

    # The payload is the one the scheduler would have made: whose class it is, which accounts, and
    # the class's own name - what tells a worker who to make co-host.
    job = dash.get(f"/api/v1/jobs/{body['jobId']}", headers={"X-API-Key": CLIENT_KEY}).json()
    assert job["payload"]["classPlanId"] == item["id"]
    assert job["payload"]["coordinatorId"] == mona
    assert job["payload"]["title"] == "Freelancing Skills"
    assert job["payload"]["meetingUrl"] == "https://zoom.us/j/91473108491"
    assert job["payload"]["zoomAccountId"]
    assert job["payload"]["lmsAccountId"]


def test_pressing_it_twice_in_a_minute_does_not_queue_the_same_stage_twice(dash, two):
    mona, _ = two
    ready(dash, mona)
    item = a_class(dash, mona, meetingUrl="https://zoom.us/j/91473108491")

    first = run(dash, item["id"], "lms.run_session")
    again = run(dash, item["id"], "lms.run_session")
    assert first.status_code == 202
    assert again.status_code == 200
    assert again.json()["created"] is False
    assert again.json()["jobId"] == first.json()["jobId"]


def test_every_stage_of_a_class_can_be_started_on_its_own(dash, two):
    mona, _ = two
    ready(dash, mona)
    item = a_class(dash, mona, meetingUrl="https://zoom.us/j/91473108491")

    for stage in ("class.run", "lms.run_session", "lms.attendance", "lms.late_joiners",
                  "lms.complete", "zoom.report", "zoom.recording"):
        answer = run(dash, item["id"], stage)
        assert answer.status_code == 202, f"{stage}: {answer.text}"
        assert answer.json()["type"] == stage


def test_a_class_with_no_zoom_link_is_refused_with_the_reason_rather_than_queued_to_fail(dash, two):
    mona, _ = two
    ready(dash, mona)
    item = a_class(dash, mona)
    # Importing carries the group's own link on, so the class has one; this is the class whose link
    # was taken off again, or whose group never had one.
    cleared = dash.patch(f"/api/v1/admin/run-plan/{item['id']}", headers=DASH, json={"meetingUrl": None})
    assert cleared.status_code == 200, cleared.text
    assert cleared.json()["needsLink"] is True

    answer = run(dash, item["id"], "class.run")
    assert answer.status_code == 409
    assert "no Zoom link" in answer.json()["details"]

    # The LMS steps of the same class need no link, so those still run.
    assert run(dash, item["id"], "lms.run_session").status_code == 202


def test_a_class_with_no_lms_sign_in_chosen_cannot_be_run(dash, two):
    mona, _ = two
    # Turned on, but with none of their LMS sign-ins named: the class would go up under whichever
    # account a machine last used, so it is refused instead.
    turn_on(dash, mona, zoom="CAI5_AIS4_S7")
    item = a_class(dash, mona, meetingUrl="https://zoom.us/j/91473108491")

    answer = run(dash, item["id"], "class.run")
    assert answer.status_code == 409
    assert "LMS account" in answer.json()["details"]


def test_an_unknown_stage_names_the_ones_there_are(dash, two):
    mona, _ = two
    ready(dash, mona)
    item = a_class(dash, mona, meetingUrl="https://zoom.us/j/91473108491")

    answer = run(dash, item["id"], "lms.everything")
    assert answer.status_code == 400
    assert "class.run" in answer.json()["details"]


def test_a_class_that_is_not_there_is_a_404(dash, two):
    ready(dash, two[0])
    assert run(dash, "00000000-0000-0000-0000-000000000000", "class.run").status_code == 404


def test_only_the_admin_may_start_a_class_and_only_from_the_dashboard(dash, two):
    mona, _ = two
    ready(dash, mona)
    item = a_class(dash, mona, meetingUrl="https://zoom.us/j/91473108491")

    # A write without the dashboard's own header is refused, as every other write is.
    assert run(dash, item["id"], "class.run", headers={}).status_code == 403
    as_user(dash, "mona")
    assert run(dash, item["id"], "class.run").status_code == 403


def test_starting_a_stage_is_written_down_with_who_did_it(dash, two):
    mona, _ = two
    ready(dash, mona)
    item = a_class(dash, mona, meetingUrl="https://zoom.us/j/91473108491")
    assert run(dash, item["id"], "class.run").status_code == 202

    rows = run_sql(dash.app.state.settings.database_url,
                   "SELECT action, details FROM admin_audit_log WHERE action = 'run_plan.stage_run'")
    assert rows, "starting a class by hand is an admin action and belongs in the audit log"


# =========================================================================== the switches


def test_a_worker_is_told_the_app_s_own_defaults_when_nothing_has_been_saved(client):
    _, token = register(client, "worker-1")
    answer = client.get("/api/v1/agent/policy", headers={"Authorization": f"Bearer {token}"})
    assert answer.status_code == 200, answer.text
    assert answer.json()["autoCoHost"] is True
    assert answer.json()["autoEnd"] is True


def test_the_admin_turns_one_off_and_every_worker_reads_it(dash, two):
    _, token = register(dash, "worker-1")
    as_user(dash, "admin", ADMIN_PASSWORD)
    saved = dash.put("/api/v1/settings/cloudPolicy", headers=DASH, json={"value": {"autoCoHost": True, "autoEnd": False}})
    assert saved.status_code == 200, saved.text

    answer = dash.get("/api/v1/agent/policy", headers={"Authorization": f"Bearer {token}"})
    assert answer.json() == {"autoCoHost": True, "autoEnd": False, "updatedAt": answer.json()["updatedAt"]}
    assert answer.json()["updatedAt"] is not None


def test_a_half_written_setting_keeps_the_defaults_for_what_it_does_not_name(dash, two):
    _, token = register(dash, "worker-1")
    as_user(dash, "admin", ADMIN_PASSWORD)
    assert dash.put("/api/v1/settings/cloudPolicy", headers=DASH, json={"value": {"autoEnd": False}}).status_code == 200

    answer = dash.get("/api/v1/agent/policy", headers={"Authorization": f"Bearer {token}"}).json()
    assert answer["autoEnd"] is False
    assert answer["autoCoHost"] is True          # not named, so a class still makes its instructor co-host

    # Nonsense in the setting is not obeyed either: it is not a switch.
    assert dash.put("/api/v1/settings/cloudPolicy", headers=DASH, json={"value": {"autoEnd": "off"}}).status_code == 200
    assert dash.get("/api/v1/agent/policy", headers={"Authorization": f"Bearer {token}"}).json()["autoEnd"] is True


def test_a_coordinator_may_read_the_switches_but_not_change_them(dash, two):
    as_user(dash, "mona")
    assert dash.get("/api/v1/settings/cloudPolicy").status_code == 200
    assert dash.put("/api/v1/settings/cloudPolicy", headers=DASH, json={"value": {"autoEnd": False}}).status_code == 403


def test_the_switches_are_not_readable_without_a_device_token(dash, two):
    assert dash.get("/api/v1/agent/policy").status_code in (401, 403)
    assert dash.get("/api/v1/agent/policy", headers={"Authorization": "Bearer zaad_not_a_token"}).status_code in (401, 403)


# =========================================================================== who teaches what


def test_the_session_roles_the_admin_saves_are_what_a_worker_reads(dash, two):
    _, token = register(dash, "worker-1")
    as_user(dash, "admin", ADMIN_PASSWORD)
    profiles = [{
        "sessionType": "Technical",
        "keywords": ["python"],
        "accounts": [],
        "people": [{"name": "Nada Instructor", "role": "Instructor", "aliases": ["Nada I."]}],
    }]
    assert dash.put("/api/v1/settings/sessionRoles", headers=DASH, json={"value": {"profiles": profiles}}).status_code == 200

    answer = dash.get("/api/v1/agent/session-roles", headers={"Authorization": f"Bearer {token}"})
    assert answer.status_code == 200, answer.text
    assert answer.json()["profiles"] == profiles


def test_a_worker_with_no_session_roles_saved_is_given_an_empty_list_rather_than_an_error(client):
    _, token = register(client, "worker-1")
    answer = client.get("/api/v1/agent/session-roles", headers={"Authorization": f"Bearer {token}"})
    assert answer.status_code == 200
    assert answer.json()["profiles"] == []

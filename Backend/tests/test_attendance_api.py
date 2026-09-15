"""Central attendance over HTTP: snapshots from the agent, rosters, matching, review, access."""

from __future__ import annotations

import csv
import io
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from starlette.testclient import TestClient

from central_backend.attendance_ai import AiAnswer
from central_backend.auth import AuthSettings
from central_backend.main import create_app
from conftest import CLIENT_KEY, client_headers, register
from test_dashboard import ADMIN_PASSWORD, DASH, build, login, make_user, run_sql, sync

A, B = "CAI5_AIS4_S7", "CAI5_AIS4_S8"
T0 = datetime(2026, 9, 14, 15, 0, tzinfo=UTC)          # 18:00 Cairo
ROSTER = "Order\tName\tEmail\n1\tMohab Osama Sayed Mohamed\tmohab@example.com\n2\tSara Khaled Ali\tsara@example.com\n" \
         "3\tAhmed Mohamed Ali\t\n4\tOmar Adel Hassan\t\n"


@pytest.fixture
def dash(settings, clock):  # noqa: ANN001, ANN201
    with build(settings, clock) as client:
        yield client


def url(client: TestClient) -> str:
    return client.app.state.settings.database_url


def as_admin(client: TestClient) -> TestClient:
    client.cookies.clear()
    assert login(client).status_code == 200
    return client


def import_roster(client: TestClient, group: str = A, text: str = ROSTER, dry: bool = False):  # noqa: ANN201
    return client.post("/api/v1/dashboard/students/import", json={"group": group, "text": text, "dryRun": dry}, headers=DASH)


def snapshot(client: TestClient, names: list[str], minute: int, *, token: str | None = None, ref: str = "win-session-1",
             group: str = A, complete: bool = True, ended: bool = False, snap_id: str | None = None, start: str | None = "18:00"):  # noqa: ANN201
    headers = {"Authorization": f"Bearer {token}"} if token else client_headers()
    body = {"clientSnapshotId": snap_id or f"{ref}-{minute}", "session": {"ref": ref, "group": group, "date": "2026-09-14", "startTime": start,
            "meetingUrl": "https://us06web.zoom.us/j/123456789?pwd=SECRET"},
            "capturedAt": (T0 + timedelta(minutes=minute)).isoformat(), "source": "desktop", "trigger": "scheduled",
            "isComplete": complete, "ended": ended, "participants": [{"name": n} for n in names]}
    return client.post("/api/v1/attendance/snapshots", json=body, headers=headers)


def details(client: TestClient, session_id: str) -> dict:
    response = client.get(f"/api/v1/dashboard/attendance/sessions/{session_id}")
    assert response.status_code == 200, response.text
    return response.json()


def record(d: dict, name: str) -> dict:
    return next(r for r in d["records"] if r["fullName"] == name)


# =========================================================================== the whole flow


def test_group_roster_zoom_snapshots_matching_and_attendance(dash):
    """Create a group, add students, simulate Zoom participants from the agent, run matching,
    and check the attendance: present / needs review / absent, join / leave / duration."""
    as_admin(dash)
    assert dash.post("/api/v1/admin/groups", json={"name": A}, headers=DASH).status_code == 201
    imported = import_roster(dash)
    assert imported.status_code == 200 and imported.json()["created"] == 4
    _, token = register(dash, "PC-LAB")

    r1 = snapshot(dash, ["Eyouth Coordinator (Host, me)", "Mohab Osama", "Session Instructor (Co-host)"], 0, token=token)
    assert r1.status_code == 201, r1.text
    sid = r1.json()["sessionId"]
    snapshot(dash, ["Mohab Osama", "مُهاب أُسامة"], 0, token=token, snap_id="dup-check")      # (a second read, same minute)
    snapshot(dash, ["Mohab Osama", "Sara Khaled", "Ahmed Mohamed", "Random Guest"], 10, token=token)
    snapshot(dash, ["Sara Khaled", "Ahmed Mohamed", "Random Guest"], 20, token=token)
    last = snapshot(dash, ["Sara Khaled"], 30, token=token, ended=True)
    assert last.json()["summary"]["students"] == 4

    d = details(dash, sid)
    assert d["session"]["group"] == A and d["session"]["status"] == "closed" and d["snapshots"]["count"] == 5
    mohab, sara, ahmed, omar = (record(d, n) for n in ("Mohab Osama Sayed Mohamed", "Sara Khaled Ali", "Ahmed Mohamed Ali", "Omar Adel Hassan"))
    assert (mohab["status"], mohab["participant"]["name"], mohab["confidence"]) == ("present", "Mohab Osama", 95)
    assert mohab["joinTime"] == "2026-09-14T15:00:00Z" and mohab["leaveTime"] == "2026-09-14T15:15:00Z"
    assert mohab["durationSeconds"] == 15 * 60
    assert sara["status"] == "present" and sara["durationSeconds"] == 25 * 60          # joined ~5, still there at the end (30)
    assert (ahmed["status"], ahmed["participant"]["name"]) == ("present", "Ahmed Mohamed")     # the only Ahmed Mohamed here
    assert omar["status"] == "absent" and omar["participant"] is None and omar["durationSeconds"] is None
    names = {p["name"]: p for p in d["participants"]}
    assert names["Eyouth Coordinator"]["ignored"] is True                               # "(Host, me)": staff, not a student
    assert names["Session Instructor"]["ignored"] is True                               # "(Co-host)" too
    assert names["Random Guest"]["assignedTo"] is None                                  # kept as unmatched
    assert d["summary"]["unmatched"] >= 1


def test_the_windows_agents_snapshots_as_its_uploader_sends_them(dash):
    """The exact shape Windows/src/ZoomAutoAdmit.CentralAgent/AttendanceUploader.cs posts: names as
    strings, reads never complete (the Zoom list may be scrolled), and a failed last read still
    ending the meeting - with no names, so nobody is taken to have left."""
    as_admin(dash)
    import_roster(dash)
    _, token = register(dash, "PC-LAB")
    ref = f"win:{uuid.uuid4()}"

    def post(minute: int, trigger: str, names: list[str], ended: bool = False):  # noqa: ANN202
        body = {"clientSnapshotId": f"{ref}:{minute}", "session": {"ref": ref, "group": A, "date": "2026-09-14", "startTime": "18:02",
                "meetingUrl": "https://us06web.zoom.us/j/81234567890"}, "capturedAt": (T0 + timedelta(minutes=minute)).isoformat(),
                "source": "desktop", "trigger": trigger, "isComplete": False, "ended": ended, "participants": names}
        response = dash.post("/api/v1/attendance/snapshots", json=body, headers={"Authorization": f"Bearer {token}"})
        assert response.status_code in (200, 201), response.text
        return response.json()

    sid = post(0, "meeting_start", ["Mohab Osama"])["sessionId"]
    post(15, "scheduled", ["Mohab Osama", "Sara Khaled"])
    post(30, "admission", ["Sara Khaled"])
    assert post(30, "admission", ["Sara Khaled"])["duplicate"] is True                  # sent twice: kept once
    post(90, "meeting_end", [], ended=True)

    d = details(dash, sid)
    assert (d["session"]["status"], d["session"]["startTime"], d["snapshots"]["count"]) == ("closed", "18:02", 4)
    mohab, sara = record(d, "Mohab Osama Sayed Mohamed"), record(d, "Sara Khaled Ali")
    assert mohab["status"] == "present" and mohab["durationSeconds"] == 15 * 60        # first to last sighting
    assert sara["status"] == "present" and sara["leaveTime"] == "2026-09-14T15:30:00Z"
    assert record(d, "Omar Adel Hassan")["status"] == "absent"


def test_ahmed_mohamed_fits_only_one_student_here_so_is_present(dash):
    # Only one "Ahmed Mohamed ..." on this roster, so the start of his name is enough.
    as_admin(dash)
    import_roster(dash)
    sid = snapshot(dash, ["Ahmed Mohamed"], 0).json()["sessionId"]
    assert record(details(dash, sid), "Ahmed Mohamed Ali")["status"] == "present"


# =========================================================================== snapshots from machines


def test_snapshots_need_a_device_token_or_the_n8n_key(dash):
    assert snapshot(dash, ["X Y"], 0, token="zaad_not-a-real-token").status_code == 401
    r = dash.post("/api/v1/attendance/snapshots", json={}, headers={})
    assert r.status_code in (400, 401)
    as_admin(dash)                                                                      # a dashboard session is not enough
    no_key = dash.post("/api/v1/attendance/snapshots", json={"session": {"group": A, "date": "2026-09-14"},
                       "capturedAt": T0.isoformat(), "participants": []})
    assert no_key.status_code == 401
    assert snapshot(dash, ["Mohab Osama"], 0).status_code == 201                       # the n8n key
    dash.cookies.clear()
    _, token = register(dash, "PC-2")
    assert dash.get("/api/v1/dashboard/attendance/sessions", headers={"Authorization": f"Bearer {token}"}).status_code == 401


def test_re_sending_a_snapshot_changes_nothing(dash):
    first = snapshot(dash, ["Mohab Osama"], 0, snap_id="same")
    again = snapshot(dash, ["Mohab Osama", "Someone Else"], 0, snap_id="same")
    assert first.status_code == 201 and again.status_code == 200 and again.json() == {"sessionId": first.json()["sessionId"], "duplicate": True}
    assert run_sql(url(dash), "SELECT count(*) AS n FROM attendance_snapshots")[0]["n"] == 1


def test_a_session_is_found_again_by_its_reference_and_registers_its_group(dash):
    s1 = snapshot(dash, ["A B"], 0, ref="ref-x", group="NEW_GROUP_S1").json()["sessionId"]
    s2 = snapshot(dash, ["A B"], 5, ref="ref-x", group="NEW_GROUP_S1").json()["sessionId"]
    assert s1 == s2
    assert run_sql(url(dash), "SELECT 1 FROM groups WHERE name = 'NEW_GROUP_S1'")
    assert snapshot(dash, ["A B"], 6, ref="ref-x", group=B).status_code == 409                # a reference cannot change group
    stored = run_sql(url(dash), "SELECT meeting_url FROM attendance_sessions WHERE id = $1", uuid.UUID(s1))[0]["meeting_url"]
    assert stored == "https://us06web.zoom.us/j/123456789"                                  # the passcode is not kept


def test_a_session_is_linked_to_the_recording_of_the_same_class(dash):
    rec = sync(dash, group=A, date="2026-09-14", startTime="18:00")["id"]
    sid = snapshot(dash, ["A B"], 0).json()["sessionId"]
    as_admin(dash)
    assert details(dash, sid)["session"]["recordingId"] == rec


@pytest.mark.parametrize("change, status", [
    ({"capturedAt": "2026-09-14T18:00:00"}, 400),                     # no time zone
    ({"session": {"group": "bad/group", "date": "2026-09-14"}}, 400),
    ({"session": {"group": A, "date": "2026-09-14", "startTime": "25:00"}}, 400),
    ({"participants": ["x"] * 1001}, 400),
    ({"unexpected": True}, 400),
])
def test_bad_snapshots_are_refused(dash, change, status):
    body = {"session": {"group": A, "date": "2026-09-14"}, "capturedAt": T0.isoformat(), "participants": ["A B"], **change}
    assert dash.post("/api/v1/attendance/snapshots", json=body, headers=client_headers()).status_code == status


# =========================================================================== review


def test_a_manual_correction_is_remembered_for_the_next_session(dash):
    as_admin(dash)
    import_roster(dash)
    s1 = snapshot(dash, ["Mo Os"], 0, ref="class-1").json()["sessionId"]
    d = details(dash, s1)
    mo_os = next(p for p in d["participants"] if p["name"] == "Mo Os")
    mohab = record(d, "Mohab Osama Sayed Mohamed")
    assert mohab["status"] == "absent"
    fixed = dash.put(f"/api/v1/dashboard/attendance/sessions/{s1}/records/{mohab['studentId']}",
                     json={"participantId": mo_os["id"]}, headers=DASH)
    assert fixed.status_code == 200, fixed.text
    fixed_row = record(fixed.json(), "Mohab Osama Sayed Mohamed")
    assert (fixed_row["status"], fixed_row["source"], fixed_row["manual"]) == ("present", "manual", True)

    s2 = snapshot(dash, ["Mo Os"], 0, ref="class-2").json()["sessionId"]                # next week, same nickname
    again = record(details(dash, s2), "Mohab Osama Sayed Mohamed")
    assert (again["status"], again["source"], again["manual"]) == ("present", "memory", False)
    aliases = dash.get(f"/api/v1/dashboard/students/{mohab['studentId']}/aliases").json()["aliases"]
    assert [(a["alias"], a["status"], a["source"]) for a in aliases] == [("Mo Os", "accepted", "manual")]


def test_marking_absent_forgets_that_name_for_that_student(dash):
    as_admin(dash)
    import_roster(dash)
    s1 = snapshot(dash, ["Omar Adel"], 0, ref="c1").json()["sessionId"]
    omar = record(details(dash, s1), "Omar Adel Hassan")
    assert omar["status"] == "present"
    dash.put(f"/api/v1/dashboard/attendance/sessions/{s1}/records/{omar['studentId']}", json={"status": "absent"}, headers=DASH)
    s2 = snapshot(dash, ["Omar Adel"], 0, ref="c2").json()["sessionId"]
    assert record(details(dash, s2), "Omar Adel Hassan")["status"] == "absent"          # rejected pair


def test_moving_a_name_to_another_student_and_resetting(dash):
    as_admin(dash)
    import_roster(dash)
    sid = snapshot(dash, ["Omar Adel"], 0).json()["sessionId"]
    d = details(dash, sid)
    name = next(p for p in d["participants"] if p["name"] == "Omar Adel")
    sara = record(d, "Sara Khaled Ali")
    moved = dash.put(f"/api/v1/dashboard/attendance/sessions/{sid}/records/{sara['studentId']}",
                     json={"participantId": name["id"], "remember": False}, headers=DASH).json()
    assert record(moved, "Sara Khaled Ali")["participant"]["name"] == "Omar Adel"
    assert record(moved, "Omar Adel Hassan")["status"] == "absent"                      # one name, one student
    reset = dash.put(f"/api/v1/dashboard/attendance/sessions/{sid}/records/{sara['studentId']}", json={"reset": True}, headers=DASH).json()
    assert record(reset, "Omar Adel Hassan")["status"] == "present"
    assert record(reset, "Sara Khaled Ali")["status"] == "absent"
    assert run_sql(url(dash), "SELECT count(*) AS n FROM student_aliases")[0]["n"] == 0  # remember: false


def test_present_by_hand_without_a_zoom_name(dash):
    as_admin(dash)
    import_roster(dash)
    sid = snapshot(dash, ["Nobody Known"], 0).json()["sessionId"]
    omar = record(details(dash, sid), "Omar Adel Hassan")
    r = dash.put(f"/api/v1/dashboard/attendance/sessions/{sid}/records/{omar['studentId']}", json={"status": "present"}, headers=DASH)
    row = record(r.json(), "Omar Adel Hassan")
    assert (row["status"], row["participant"], row["manual"]) == ("present", None, True)


def test_ignoring_a_name_and_adding_names_by_hand(dash):
    as_admin(dash)
    import_roster(dash)
    created = dash.post("/api/v1/dashboard/attendance/sessions", json={"group": A, "date": "2026-09-15", "startTime": "18:00"}, headers=DASH)
    assert created.status_code == 201
    sid = created.json()["id"]
    added = dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/participants",
                      json={"names": ["Omar Adel Hassan", "Sara Khaled Ali"]}, headers=DASH)
    assert added.status_code == 201 and added.json()["summary"]["present"] == 2
    sara_name = next(p for p in added.json()["participants"] if p["name"] == "Sara Khaled Ali")
    ignored = dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/participants/{sara_name['id']}/ignore",
                        json={"ignored": True}, headers=DASH).json()
    assert record(ignored, "Sara Khaled Ali")["status"] == "absent"


def test_finalized_sessions_are_locked_until_reopened(dash):
    as_admin(dash)
    import_roster(dash)
    sid = snapshot(dash, ["Omar Adel"], 0).json()["sessionId"]
    omar = record(details(dash, sid), "Omar Adel Hassan")
    assert dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/finalize", headers=DASH).json()["session"]["status"] == "finalized"
    put = dash.put(f"/api/v1/dashboard/attendance/sessions/{sid}/records/{omar['studentId']}", json={"status": "absent"}, headers=DASH)
    assert put.status_code == 409
    assert dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/participants", json={"names": ["X Y"]}, headers=DASH).status_code == 409
    snapshot(dash, ["Omar Adel", "Sara Khaled"], 5)                                      # stored, but the result stays
    assert record(details(dash, sid), "Sara Khaled Ali")["status"] == "absent"
    reopened = dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/reopen", headers=DASH).json()
    assert reopened["session"]["status"] == "open" and record(reopened, "Sara Khaled Ali")["status"] == "present"


def test_the_csv_export_cannot_carry_a_spreadsheet_formula(dash):
    as_admin(dash)
    import_roster(dash, text="Name\n=HYPERLINK(\"x\")\nMona Adel\n")
    sid = snapshot(dash, ["Mona Adel"], 0).json()["sessionId"]
    response = dash.get(f"/api/v1/dashboard/attendance/sessions/{sid}/export.csv")
    assert response.status_code == 200 and response.headers["content-type"].startswith("text/csv")
    rows = list(csv.reader(io.StringIO(response.content.decode("utf-8-sig"))))
    assert rows[0][:4] == ["Order", "Student", "Email", "Status"]
    students = {r[1]: r for r in rows[1:]}
    assert "'=HYPERLINK(\"x\")" in students and students["Mona Adel"][3] == "Present"


# =========================================================================== students


def test_students_are_added_edited_imported_and_deactivated(dash):
    as_admin(dash)
    preview = import_roster(dash, dry=True).json()
    assert preview["created"] == 4 and run_sql(url(dash), "SELECT count(*) AS n FROM students")[0]["n"] == 0
    assert import_roster(dash).json()["created"] == 4
    again = import_roster(dash, text="Name\tEmail\nMohab O. S. Mohamed\tmohab@example.com\n").json()
    assert (again["created"], again["updated"]) == (0, 1)                                 # found by e-mail, renamed
    listed = dash.get("/api/v1/dashboard/students", params={"group": A}).json()
    assert listed["count"] == 4
    one = next(s for s in listed["students"] if s["email"] == "sara@example.com")
    assert dash.post("/api/v1/dashboard/students", json={"group": A, "fullName": "Dup", "email": "SARA@example.com"}, headers=DASH).status_code == 409
    patched = dash.patch(f"/api/v1/dashboard/students/{one['id']}", json={"aliases": ["Sara K", "sara k"], "active": False}, headers=DASH).json()
    assert patched["aliases"] == ["Sara K"] and patched["active"] is False and set(patched["changed"]) == {"aliases", "active"}
    assert dash.get("/api/v1/dashboard/students", params={"group": A}).json()["count"] == 3
    assert dash.get("/api/v1/dashboard/students", params={"group": A, "includeInactive": "true"}).json()["count"] == 4


# =========================================================================== access


def test_a_coordinator_sees_and_changes_only_their_own_groups(dash):
    as_admin(dash)
    import_roster(dash)
    import_roster(dash, group=B, text="Name\nNour Hassan\n")
    sa = snapshot(dash, ["Omar Adel"], 0, ref="a").json()["sessionId"]
    sb = snapshot(dash, ["Nour Hassan"], 0, ref="b", group=B).json()["sessionId"]
    nour = dash.get("/api/v1/dashboard/students", params={"group": B}).json()["students"][0]
    make_user(url(dash), "omar", groups=(A,))
    dash.cookies.clear()
    assert login(dash, "omar", "test-only coordinator pw 1").status_code == 200

    assert {s["group"] for s in dash.get("/api/v1/dashboard/students").json()["students"]} == {A}
    assert [s["id"] for s in dash.get("/api/v1/dashboard/attendance/sessions").json()["items"]] == [sa]
    assert dash.get(f"/api/v1/dashboard/attendance/sessions/{sb}").status_code == 404
    assert dash.get(f"/api/v1/dashboard/attendance/sessions/{sb}/export.csv").status_code == 404
    assert dash.put(f"/api/v1/dashboard/attendance/sessions/{sb}/records/{nour['id']}", json={"status": "absent"}, headers=DASH).status_code == 404
    assert dash.patch(f"/api/v1/dashboard/students/{nour['id']}", json={"fullName": "X"}, headers=DASH).status_code == 404
    assert import_roster(dash, group=B, text="Name\nIntruder Person\n").status_code == 403
    assert dash.post("/api/v1/dashboard/students", json={"group": B, "fullName": "Intruder"}, headers=DASH).status_code == 403
    assert details(dash, sa)["session"]["group"] == A
    assert dash.post(f"/api/v1/dashboard/attendance/sessions/{sa}/finalize", headers=DASH).status_code == 200


def test_writes_need_the_dashboard_header(dash):
    as_admin(dash)
    assert dash.post("/api/v1/dashboard/students/import", json={"group": A, "text": ROSTER}).status_code == 403
    assert dash.post("/api/v1/dashboard/attendance/sessions", json={"group": A, "date": "2026-09-14"}).status_code == 403


# =========================================================================== AI


class FakeAi:
    def __init__(self, answers):  # noqa: ANN001
        self.answers, self.calls = answers, []

    async def ask(self, students, names, rejected):  # noqa: ANN001, ANN201
        self.calls.append((students, names, rejected))
        by_student = {s["name"]: s["id"] for s in students}
        by_name = {n["name"]: n["id"] for n in names}
        return [AiAnswer(by_student[s], by_name[n], c, r) for s, n, c, r in self.answers if s in by_student and n in by_name]


def test_ai_confident_answers_are_remembered_and_others_suggested(settings, clock):
    make_user(settings.database_url, "admin", ADMIN_PASSWORD, role="admin")
    ai = FakeAi([("Omar Adel Hassan", "عمر عادل", 0.95, False), ("Sara Khaled Ali", "S. K.", 0.7, True)])
    app = create_app(settings, clock=clock, run_background=False, configure_logs=False, auth_settings=AuthSettings(),
                     dashboard_dist=Path("__none__"), attendance_ai=ai)
    with TestClient(app) as client:
        as_admin(client)
        import_roster(client)
        sid = snapshot(client, ["عمر عادل", "S. K."], 0).json()["sessionId"]
        d = details(client, sid)
        assert d["aiAvailable"] is True and record(d, "Omar Adel Hassan")["status"] == "needs_review"   # first name only
        result = client.post(f"/api/v1/dashboard/attendance/sessions/{sid}/match", json={"useAi": True}, headers=DASH).json()
        omar = record(result, "Omar Adel Hassan")
        assert (omar["status"], omar["source"], omar["participant"]["name"]) == ("present", "memory", "عمر عادل")
        assert [(s["name"], s["confidence"]) for s in result["aiSuggestions"]] == [("S. K.", 70)]
        assert record(result, "Sara Khaled Ali")["status"] != "present"                    # a suggestion is not applied
        assert run_sql(settings.database_url, "SELECT alias, source FROM student_aliases")[0]["source"] == "ai"
        assert "SECRET" not in str(ai.calls)
        # every answer that replaces the page's copy keeps saying AI is there
        sara = record(result, "Sara Khaled Ali")["studentId"]
        corrected = client.put(f"/api/v1/dashboard/attendance/sessions/{sid}/records/{sara}", json={"status": "absent"}, headers=DASH).json()
        assert corrected["aiAvailable"] is True


def test_without_an_ai_key_the_ai_step_is_refused(dash):
    as_admin(dash)
    import_roster(dash)
    sid = snapshot(dash, ["Omar Adel"], 0).json()["sessionId"]
    assert details(dash, sid)["aiAvailable"] is False
    assert dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/match", json={"useAi": True}, headers=DASH).status_code == 409
    assert dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/match", headers=DASH).status_code == 200


def test_the_n8n_key_does_not_open_the_dashboard_attendance(dash):
    assert dash.get("/api/v1/dashboard/attendance/sessions", headers={"X-API-Key": CLIENT_KEY}).status_code == 401
    assert dash.get("/api/v1/dashboard/students", headers={"X-API-Key": CLIENT_KEY}).status_code == 401

@pytest.mark.parametrize("dry", [True, False])
def test_roster_row_groups_and_invalid_duplicates(dash, dry):
    as_admin(dash)
    make_user(url(dash), "rostercoord", groups=(A,))
    dash.cookies.clear()
    assert login(dash, "rostercoord", "test-only coordinator pw 1").status_code == 200
    text = f"Name,Email,Group,ID\nMona Adel,mona@example.com,{A},1\nOther Person,other@example.com,{B},2\nBad Email,bad,{A},3\nDuplicate ID,d@example.com,{A},1\n"
    result = import_roster(dash, text=text, dry=dry).json()
    assert result["created"] == 1
    assert len(result["invalidRows"]) == 2
    assert len(result["duplicateRows"]) == 1
    assert dash.get("/api/v1/dashboard/students").json()["count"] == (0 if dry else 1)
    assert import_roster(dash, group=B, dry=dry).status_code == 403


def test_coordinator_roster_lifecycle_and_revocation(dash):
    as_admin(dash)
    coordinator = make_user(url(dash), "rostercoord", groups=(A,))
    dash.cookies.clear()
    assert login(dash, "rostercoord", "test-only coordinator pw 1").status_code == 200
    created = dash.post("/api/v1/dashboard/students", json={"group": A, "fullName": "Mona Adel", "email": "mona@example.com"}, headers=DASH)
    assert created.status_code == 201
    sid = created.json()["id"]
    endpoint = f"/api/v1/dashboard/students/{sid}"
    assert dash.patch(endpoint, json={"group": B}, headers=DASH).status_code == 403
    assert dash.patch(endpoint, json={"fullName": "Mona Adel Hassan"}, headers=DASH).status_code == 200
    assert dash.patch(endpoint, json={"active": False}, headers=DASH).json()["active"] is False
    text = "Name,Email\nMona Adel Hassan,mona@example.com"
    assert import_roster(dash, text=text, dry=True).json()["updated"] == 1
    assert dash.get("/api/v1/dashboard/students").json()["count"] == 0
    assert import_roster(dash, text=text).json()["updated"] == 1
    run_sql(url(dash), "DELETE FROM user_groups WHERE user_id = $1", uuid.UUID(coordinator))
    assert dash.get("/api/v1/dashboard/students").json()["count"] == 0
    assert dash.patch(endpoint, json={"active": False}, headers=DASH).status_code == 404
    assert dash.get(endpoint + "/aliases").status_code == 404
    assert import_roster(dash, text=text, dry=True).status_code == 403
    assert import_roster(dash, text=text).status_code == 403
    assert dash.post("/api/v1/dashboard/students", json={"group": A, "fullName": "Another Student"}, headers=DASH).status_code == 403
    as_admin(dash)
    assert dash.patch(endpoint, json={"active": False}, headers=DASH).status_code == 200
    assert import_roster(dash, group=B, text=text).json()["created"] == 1


def test_import_conflicting_identity_is_previewed_without_overwriting(dash):
    as_admin(dash)
    import_roster(dash, text="Name,Email,ID\nMona Adel,mona@example.com,1\nSara Ali,sara@example.com,2")
    text = "Name,Email,ID\nMona Adel,mona@example.com,2"
    for dry in (True, False):
        result = import_roster(dash, text=text, dry=dry).json()
        assert result["created"] == result["updated"] == 0
        assert len(result["invalidRows"]) == 1
    assert dash.get("/api/v1/dashboard/students").json()["count"] == 2

@pytest.mark.parametrize("path,body", [
    ("match", {"useAi": False}), ("participants", {"names": ["Other Student"]}),
    ("finalize", None), ("reopen", None),
])
def test_coordinator_cannot_mutate_other_group_attendance(dash, path, body):
    as_admin(dash)
    sid = snapshot(dash, ["Other Student"], 0, group=B).json()["sessionId"]
    make_user(url(dash), "rostercoord", groups=(A,))
    dash.cookies.clear()
    assert login(dash, "rostercoord", "test-only coordinator pw 1").status_code == 200
    assert dash.post(f"/api/v1/dashboard/attendance/sessions/{sid}/{path}", json=body, headers=DASH).status_code == 404


def test_reimport_duplicate_target_preview_matches_commit(dash):
    as_admin(dash)
    import_roster(dash, text="Name,Email,ID\nMona Adel,mona@example.com,1")
    text = "Name,Email,ID\nMona Hassan,new@example.com,1\nMona Adel,mona@example.com,"
    preview = import_roster(dash, text=text, dry=True).json()
    saved = import_roster(dash, text=text).json()
    for key in ("created", "updated", "unchanged", "invalidRows", "duplicateRows"):
        assert preview[key] == saved[key]
    assert saved["updated"] == 1 and len(saved["duplicateRows"]) == 1

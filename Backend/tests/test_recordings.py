"""Recording management: sync (create / update), listing with filters, one recording, groups."""

from __future__ import annotations

import logging
import uuid
from datetime import date, timedelta

import pytest

from central_backend.observability import LOGGER_NAME
from conftest import DRIVE_ID, DRIVE_LINK, client_headers, post_job

ZOOM_LINK = "https://us06web.zoom.us/rec/share/FAKE-TOKEN-FOR-TESTS.NotARealLink"


def sync(client, **body):  # noqa: ANN001, ANN003, ANN201
    return client.post("/api/v1/recordings/sync", json=body, headers=client_headers())


def recording_body(**overrides):  # noqa: ANN003, ANN201
    body = {
        "group": "CAI5_AIS4_S7",
        "date": "2026-09-11",
        "startTime": "18:00",
        "fileName": "session.mp4",
        "type": "recording",
        "link": DRIVE_LINK,
    }
    body.update(overrides)
    return body


def listing(client, **params) -> list[dict]:  # noqa: ANN001, ANN003
    response = client.get("/api/v1/recordings", params=params, headers=client_headers())
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["count"] == len(body["recordings"])
    return body["recordings"]


# --------------------------------------------------------------------------- create


def test_sync_creates_a_recording(client, clock):
    response = sync(client, **recording_body())
    assert response.status_code == 201, response.text
    body = response.json()
    assert body["action"] == "created"
    recording = body["recording"]
    uuid.UUID(recording["id"])
    assert recording["group"] == "CAI5_AIS4_S7"
    assert recording["date"] == "2026-09-11"
    assert recording["startTime"] == "18:00"
    assert recording["fileName"] == "session.mp4"
    assert recording["type"] == "recording"
    assert recording["driveLink"] == DRIVE_LINK
    assert recording["zoomLink"] is None
    assert recording["source"] == "drive"
    assert recording["lmsStatus"] == "pending"
    assert recording["lmsUpdatedAt"] is None
    assert recording["createdAt"] == recording["updatedAt"] == "2026-09-12T10:00:00Z"


def test_only_group_and_date_are_required(client):
    response = sync(client, group="CAI5_AIS4_S8", date="2026-09-11")
    assert response.status_code == 201
    recording = response.json()["recording"]
    assert recording["startTime"] is None and recording["driveLink"] is None and recording["fileName"] is None
    assert recording["source"] == "zoom"                      # the default when there is no Drive link
    assert recording["lmsStatus"] == "pending"


def test_empty_sheet_cells_count_as_not_given(client):
    recording = sync(client, **recording_body(startTime="", fileName="", type=" ", link="")).json()["recording"]
    assert recording["startTime"] is None and recording["fileName"] is None
    assert recording["type"] is None and recording["driveLink"] is None


def test_a_zoom_link_is_kept_as_the_zoom_link(client):
    recording = sync(client, **recording_body(link=ZOOM_LINK)).json()["recording"]
    assert recording["zoomLink"] == ZOOM_LINK
    assert recording["driveLink"] is None
    assert recording["source"] == "zoom"


# --------------------------------------------------------------------------- validation


@pytest.mark.parametrize("body, fragment", [
    ({"date": "2026-09-11"}, "'group' is required"),
    (recording_body(group="   "), "'group' is required"),
    (recording_body(group="a/b"), "'group' may hold"),
    ({"group": "CAI5_AIS4_S7"}, "'date' is required"),
    (recording_body(date=""), "'date' is required"),
    (recording_body(date="11/09/2026"), "yyyy-MM-dd"),
    (recording_body(date="2026-02-30"), "yyyy-MM-dd"),
    (recording_body(startTime="6pm"), "HH:mm"),
    (recording_body(startTime="24:00"), "HH:mm"),
    (recording_body(link="C:\\Recordings\\session.mp4"), "'link' must be"),
    (recording_body(link="https://example.com/video.mp4"), "'link' must be"),
    (recording_body(link="http://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"), "'link' must be"),
    (recording_body(link="https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"), "'link' must be"),
])
def test_invalid_sync_bodies_are_refused_with_a_reason(client, body, fragment):
    response = sync(client, **body)
    assert response.status_code == 400, response.text
    assert response.json()["error"] == "Invalid request"
    assert fragment in str(response.json()["details"])
    assert listing(client) == []


def test_unknown_fields_are_refused(client):
    response = sync(client, **recording_body(), recordLink=DRIVE_LINK)
    assert response.status_code == 400


def test_the_recordings_api_needs_the_client_key(client):
    wrong = {"X-API-Key": "zaak_wrong-key-000000000000000000000000000"}
    assert client.post("/api/v1/recordings/sync", json=recording_body()).status_code == 401
    assert client.post("/api/v1/recordings/sync", json=recording_body(), headers=wrong).status_code == 401
    assert client.get("/api/v1/recordings").status_code == 401
    assert client.get(f"/api/v1/recordings/{uuid.uuid4()}").status_code == 401
    assert client.get("/api/v1/groups", headers=wrong).status_code == 401


# --------------------------------------------------------------------------- update (sync of a duplicate)


def test_syncing_the_same_session_again_updates_it(client, clock):
    first = sync(client, **recording_body(fileName="part1.mp4")).json()["recording"]
    clock.advance(60)
    response = sync(client, **recording_body(fileName="session-final.mp4", type="recording-final"))
    assert response.status_code == 200
    body = response.json()
    assert body["action"] == "updated"
    updated = body["recording"]

    assert updated["id"] == first["id"]
    assert updated["fileName"] == "session-final.mp4"
    assert updated["type"] == "recording-final"
    assert updated["createdAt"] == first["createdAt"]
    assert updated["updatedAt"] == "2026-09-12T10:01:00Z"
    assert len(listing(client)) == 1


def test_fields_left_out_of_an_update_keep_their_value(client):
    first = sync(client, **recording_body()).json()["recording"]
    updated = sync(client, group="CAI5_AIS4_S7", date="2026-09-11", startTime="18:00", link=ZOOM_LINK).json()["recording"]
    assert updated["id"] == first["id"]
    assert updated["fileName"] == "session.mp4"
    assert updated["driveLink"] == DRIVE_LINK                 # kept
    assert updated["zoomLink"] == ZOOM_LINK                   # added
    assert updated["source"] == "drive"                       # the Drive copy still wins


def test_a_different_start_time_is_a_different_session(client):
    sync(client, **recording_body(startTime="18:00"))
    sync(client, **recording_body(startTime="20:00"))
    assert len(listing(client, group="CAI5_AIS4_S7")) == 2


def test_a_session_without_start_time_is_one_slot_for_the_day(client):
    first = sync(client, group="CAI5_AIS4_S7", date="2026-09-11").json()["recording"]
    again = sync(client, group="CAI5_AIS4_S7", date="2026-09-11", link=DRIVE_LINK)
    assert again.status_code == 200
    assert again.json()["recording"]["id"] == first["id"]
    assert len(listing(client)) == 1


def test_a_changed_link_puts_the_recording_back_to_pending(client):
    first = sync(client, **recording_body()).json()["recording"]
    state = client.app.state

    async def mark_attached() -> None:
        from central_backend.models import Recording

        async with state.sessionmaker() as session, session.begin():
            recording = await session.get(Recording, uuid.UUID(first["id"]))
            recording.lms_status = "attached"

    client.portal.call(mark_attached)
    same = sync(client, **recording_body()).json()["recording"]
    assert same["lmsStatus"] == "attached"                    # nothing changed: status kept
    other_link = "https://drive.google.com/file/d/1ZyXwVuTsRqPoNmLkJiHgFeDcBa987654/view"
    changed = sync(client, **recording_body(link=other_link)).json()["recording"]
    assert changed["driveLink"] == other_link
    assert changed["lmsStatus"] == "pending"


# --------------------------------------------------------------------------- fetch and filter


@pytest.fixture
def three_recordings(client):  # noqa: ANN001, ANN201
    ids = {}
    for group, day, start in [
        ("CAI5_AIS4_S7", "2026-09-11", "18:00"),
        ("CAI5_AIS4_S7", "2026-09-12", "18:00"),
        ("CAI5_AIS4_S8", "2026-09-11", "20:00"),
    ]:
        ids[(group, day)] = sync(client, **recording_body(group=group, date=day, startTime=start)).json()["recording"]["id"]
    return ids


def test_all_recordings_are_returned_newest_date_first(client, three_recordings):
    recordings = listing(client)
    assert [(r["group"], r["date"]) for r in recordings] == [
        ("CAI5_AIS4_S7", "2026-09-12"),
        ("CAI5_AIS4_S7", "2026-09-11"),
        ("CAI5_AIS4_S8", "2026-09-11"),
    ]


def test_recordings_can_be_filtered_by_group(client, three_recordings):
    s7 = listing(client, group="CAI5_AIS4_S7")
    assert len(s7) == 2 and {r["group"] for r in s7} == {"CAI5_AIS4_S7"}
    assert listing(client, group="NO_SUCH_GROUP") == []


def test_recordings_can_be_filtered_by_date_and_status(client, three_recordings):
    on_the_11th = listing(client, date="2026-09-11")
    assert {r["group"] for r in on_the_11th} == {"CAI5_AIS4_S7", "CAI5_AIS4_S8"}
    assert len(listing(client, group="CAI5_AIS4_S8", date="2026-09-11")) == 1
    assert len(listing(client, status="pending")) == 3
    assert listing(client, status="attached") == []


@pytest.mark.parametrize("params", [{"date": "yesterday"}, {"status": "Pending; DROP"}, {"limit": "0"}])
def test_bad_filters_are_refused(client, params):
    assert client.get("/api/v1/recordings", params=params, headers=client_headers()).status_code == 400


def test_one_recording_can_be_read_by_id(client, three_recordings):
    recording_id = three_recordings[("CAI5_AIS4_S8", "2026-09-11")]
    response = client.get(f"/api/v1/recordings/{recording_id}", headers=client_headers())
    assert response.status_code == 200
    assert response.json()["group"] == "CAI5_AIS4_S8"
    assert response.json()["startTime"] == "20:00"
    assert client.get(f"/api/v1/recordings/{uuid.uuid4()}", headers=client_headers()).status_code == 404
    assert client.get("/api/v1/recordings/not-an-id", headers=client_headers()).status_code == 404


def test_groups_lists_each_group_once_in_order(client, three_recordings):
    response = client.get("/api/v1/groups", headers=client_headers())
    assert response.status_code == 200
    assert response.json() == {"groups": ["CAI5_AIS4_S7", "CAI5_AIS4_S8"], "count": 2}


def test_groups_is_empty_without_recordings(client):
    assert client.get("/api/v1/groups", headers=client_headers()).json() == {"groups": [], "count": 0}


# --------------------------------------------------------------------------- side effects


def test_the_log_shows_a_link_preview_only(client, caplog):
    caplog.set_level(logging.INFO, logger=LOGGER_NAME)
    sync(client, **recording_body())
    assert "recording.created" in caplog.text
    assert DRIVE_ID not in caplog.text
    assert "drive.google.com/file/d/1AbCdE..." in caplog.text


def test_syncing_recordings_creates_no_jobs_and_jobs_still_work(client):
    sync(client, **recording_body())
    created = post_job(client)
    assert created.status_code == 202
    job = client.get(f"/api/v1/jobs/{created.json()['jobId']}", headers=client_headers()).json()
    assert job["status"] == "queued"


# --------------------------------------------------------------------------- latest


LATEST_FIELDS = {"id", "group", "date", "startTime", "fileName", "type", "driveLink", "zoomLink",
                 "source", "lmsStatus", "updatedAt"}


def latest(client, **params):  # noqa: ANN001, ANN003, ANN201
    return client.get("/api/v1/recordings/latest", params=params, headers=client_headers())


def make_recordings(client, clock, count: int) -> list[str]:  # noqa: ANN001
    """`count` recordings, each synced one minute after the previous; their ids, oldest first."""
    ids = []
    for i in range(count):
        day = (date(2026, 8, 1) + timedelta(days=i)).isoformat()
        ids.append(sync(client, **recording_body(date=day)).json()["recording"]["id"])
        clock.advance(60)
    return ids


def test_latest_is_ordered_by_updated_at_newest_first(client, clock):
    ids = make_recordings(client, clock, 3)
    # Updating the oldest one makes it the most recently updated.
    sync(client, **recording_body(date="2026-08-01", fileName="renamed.mp4"))

    response = latest(client)
    assert response.status_code == 200
    body = response.json()
    assert [r["id"] for r in body["recordings"]] == [ids[0], ids[2], ids[1]]
    assert body["count"] == 3
    stamps = [r["updatedAt"] for r in body["recordings"]]
    assert stamps == sorted(stamps, reverse=True)
    assert body["recordings"][0]["fileName"] == "renamed.mp4"


def test_latest_returns_exactly_the_documented_fields(client):
    sync(client, **recording_body())
    recording = latest(client).json()["recordings"][0]
    assert set(recording) == LATEST_FIELDS
    assert recording["group"] == "CAI5_AIS4_S7"
    assert recording["date"] == "2026-09-11"
    assert recording["startTime"] == "18:00"
    assert recording["driveLink"] == DRIVE_LINK
    assert recording["zoomLink"] is None
    assert recording["source"] == "drive"
    assert recording["lmsStatus"] == "pending"
    assert recording["updatedAt"] == "2026-09-12T10:00:00Z"


def test_latest_defaults_to_20_and_honours_limit(client, clock):
    ids = make_recordings(client, clock, 25)
    default = latest(client).json()
    assert default["count"] == 20
    assert [r["id"] for r in default["recordings"]] == list(reversed(ids))[:20]

    five = latest(client, limit=5).json()
    assert five["count"] == 5
    assert [r["id"] for r in five["recordings"]] == list(reversed(ids))[:5]
    assert latest(client, limit=100).json()["count"] == 25


@pytest.mark.parametrize("value", ["0", "101", "-1", "many"])
def test_latest_refuses_a_limit_outside_1_to_100(client, value):
    response = latest(client, limit=value)
    assert response.status_code == 400
    assert response.json()["error"] == "Invalid request"


def test_latest_requires_the_api_key(client):
    sync(client, **recording_body())
    assert client.get("/api/v1/recordings/latest").status_code == 401
    wrong = {"X-API-Key": "zaak_wrong-key-000000000000000000000000000"}
    assert client.get("/api/v1/recordings/latest", headers=wrong).status_code == 401


def test_latest_on_an_empty_database_is_an_empty_list(client):
    response = latest(client)
    assert response.status_code == 200
    assert response.json() == {"recordings": [], "count": 0}


def test_latest_is_not_mistaken_for_a_recording_id(client):
    recording_id = sync(client, **recording_body()).json()["recording"]["id"]
    assert latest(client).json()["recordings"][0]["id"] == recording_id
    assert client.get(f"/api/v1/recordings/{recording_id}", headers=client_headers()).status_code == 200
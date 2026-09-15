"""PATCH /api/v1/recordings/{id}: partial edits of one recording."""

from __future__ import annotations

import asyncio
import logging
import uuid

import asyncpg
import pytest

from central_backend.observability import LOGGER_NAME
from conftest import DRIVE_ID, DRIVE_LINK, client_headers

ZOOM_LINK = "https://us06web.zoom.us/rec/share/FAKE-TOKEN-FOR-TESTS.NotARealLink"
OTHER_DRIVE_LINK = "https://drive.google.com/file/d/1ZyXwVuTsRqPoNmLkJiHgFeDcBa987654/view"
RESPONSE_FIELDS = {"id", "group", "date", "startTime", "fileName", "type", "driveLink", "zoomLink",
                   "source", "lmsStatus", "updatedAt"}


def sync(client, **overrides) -> dict:  # noqa: ANN001, ANN003
    body = {"group": "CAI5_AIS4_S7", "date": "2026-09-11", "startTime": "18:00",
            "fileName": "session.mp4", "type": "recording", "link": DRIVE_LINK}
    body.update(overrides)
    response = client.post("/api/v1/recordings/sync", json=body, headers=client_headers())
    assert response.status_code in (200, 201), response.text
    return response.json()["recording"]


def patch(client, recording_id: str, body: dict, headers: dict | None = None):  # noqa: ANN001, ANN201
    return client.patch(f"/api/v1/recordings/{recording_id}", json=body,
                        headers=client_headers() if headers is None else headers)


def stored(client, recording_id: str) -> dict:  # noqa: ANN001
    response = client.get(f"/api/v1/recordings/{recording_id}", headers=client_headers())
    assert response.status_code == 200
    return response.json()


def set_lms_status(client, recording_id: str, status: str) -> None:  # noqa: ANN001
    url = client.app.state.settings.database_url.replace("postgresql+asyncpg://", "postgresql://")

    async def run() -> None:
        conn = await asyncpg.connect(url)
        try:
            await conn.execute("UPDATE recordings SET lms_status = $1 WHERE id = $2", status, uuid.UUID(recording_id))
        finally:
            await conn.close()

    asyncio.run(run())


@pytest.fixture
def recording(client) -> dict:  # noqa: ANN001
    return sync(client)


# --------------------------------------------------------------------------- partial updates


def test_updating_the_file_name_changes_only_the_file_name(client, clock, recording):
    set_lms_status(client, recording["id"], "attached")
    clock.advance(60)
    response = patch(client, recording["id"], {"fileName": "updated.mp4"})
    assert response.status_code == 200, response.text
    body = response.json()
    assert set(body) == RESPONSE_FIELDS
    assert body["fileName"] == "updated.mp4"
    assert body["updatedAt"] == "2026-09-12T10:01:00Z"
    after = stored(client, recording["id"])
    for field in ("group", "date", "startTime", "type", "driveLink", "zoomLink", "source", "createdAt"):
        assert after[field] == recording[field], field
    assert after["lmsStatus"] == "attached"                      # not a link change: status kept


def test_a_new_link_is_recalculated_and_puts_the_lms_status_back_to_pending(client, clock, recording):
    set_lms_status(client, recording["id"], "attached")
    clock.advance(60)
    body = patch(client, recording["id"], {"link": ZOOM_LINK}).json()
    assert body["zoomLink"] == ZOOM_LINK
    assert body["driveLink"] is None                             # the new link replaces the old one
    assert body["source"] == "zoom"
    assert body["lmsStatus"] == "pending"
    assert stored(client, recording["id"])["lmsUpdatedAt"] == "2026-09-12T10:01:00Z"

    set_lms_status(client, recording["id"], "attached")
    body = patch(client, recording["id"], {"link": f"  {OTHER_DRIVE_LINK}  "}).json()
    assert (body["driveLink"], body["zoomLink"], body["source"], body["lmsStatus"]) == (OTHER_DRIVE_LINK, None, "drive", "pending")


def test_sending_the_same_link_again_is_not_a_change(client, clock, recording):
    set_lms_status(client, recording["id"], "attached")
    clock.advance(60)
    body = patch(client, recording["id"], {"link": DRIVE_LINK}).json()
    assert body["lmsStatus"] == "attached"
    assert body["updatedAt"] == recording["updatedAt"]           # nothing changed, nothing bumped


def test_an_empty_link_clears_it(client, recording):
    body = patch(client, recording["id"], {"link": ""}).json()
    assert (body["driveLink"], body["zoomLink"], body["source"], body["lmsStatus"]) == (None, None, "zoom", "pending")


def test_updating_group_and_date_moves_the_recording(client, recording):
    body = patch(client, recording["id"], {"group": "  CAI5_AIS4_S8 ", "date": "2026-09-12"}).json()
    assert (body["id"], body["group"], body["date"]) == (recording["id"], "CAI5_AIS4_S8", "2026-09-12")
    listed = client.get("/api/v1/recordings", params={"group": "CAI5_AIS4_S8", "date": "2026-09-12"},
                        headers=client_headers()).json()
    assert [r["id"] for r in listed["recordings"]] == [recording["id"]]
    assert client.get("/api/v1/recordings", params={"group": "CAI5_AIS4_S7"}, headers=client_headers()).json()["count"] == 0


def test_moving_onto_another_recordings_session_is_a_conflict(client, recording):
    other = sync(client, startTime="20:00")
    response = patch(client, other["id"], {"startTime": "18:00"})
    assert response.status_code == 409
    assert stored(client, other["id"])["startTime"] == "20:00"   # nothing was changed


def test_empty_strings_clear_the_optional_fields(client, recording):
    body = patch(client, recording["id"], {"fileName": "", "type": "  ", "startTime": ""}).json()
    assert (body["fileName"], body["type"], body["startTime"]) == (None, None, None)
    body = patch(client, recording["id"], {"fileName": None}).json()
    assert body["fileName"] is None


def test_the_lms_status_can_be_set_on_its_own(client, clock, recording):
    clock.advance(60)
    body = patch(client, recording["id"], {"lmsStatus": "attached"}).json()
    assert body["lmsStatus"] == "attached"
    assert stored(client, recording["id"])["lmsUpdatedAt"] == "2026-09-12T10:01:00Z"


def test_an_empty_patch_changes_nothing(client, recording):
    response = patch(client, recording["id"], {})
    assert response.status_code == 200
    assert response.json()["updatedAt"] == recording["updatedAt"]


# --------------------------------------------------------------------------- refused


@pytest.mark.parametrize("body, fragment", [
    ({"link": "https://example.com/video.mp4"}, "'link' must be"),
    ({"link": "C:\\Recordings\\session.mp4"}, "'link' must be"),
    ({"link": "http://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}, "'link' must be"),
    ({"link": "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"}, "'link' must be"),
    ({"date": "11/09/2026"}, "yyyy-MM-dd"),
    ({"date": "2026-02-30"}, "yyyy-MM-dd"),
    ({"date": ""}, "'date' cannot be empty"),
    ({"date": None}, "'date' cannot be empty"),
    ({"group": ""}, "'group' cannot be empty"),
    ({"group": "a/b"}, "'group' may hold"),
    ({"startTime": "6pm"}, "HH:mm"),
    ({"lmsStatus": "done"}, "'lmsStatus' must be one of"),
    ({"lmsStatus": ""}, "'lmsStatus' must be one of"),
    ({"link": ZOOM_LINK, "lmsStatus": "attached"}, "separate request"),
])
def test_invalid_values_are_refused_and_nothing_changes(client, recording, body, fragment):
    response = patch(client, recording["id"], body)
    assert response.status_code == 400, response.text
    assert response.json()["error"] == "Invalid request"
    assert fragment in str(response.json()["details"])
    assert stored(client, recording["id"]) == recording


@pytest.mark.parametrize("body", [{"recordLink": DRIVE_LINK}, {"source": "drive"}, {"driveLink": DRIVE_LINK},
                                  {"id": str(uuid.uuid4())}, {"fileName": "x.mp4", "createdAt": "2026-01-01"}])
def test_unknown_fields_are_refused(client, recording, body):
    response = patch(client, recording["id"], body)
    assert response.status_code == 400
    assert stored(client, recording["id"]) == recording


def test_a_missing_or_wrong_api_key_is_401(client, recording):
    assert patch(client, recording["id"], {"fileName": "x.mp4"}, headers={}).status_code == 401
    wrong = {"X-API-Key": "zaak_wrong-key-000000000000000000000000000"}
    assert patch(client, recording["id"], {"fileName": "x.mp4"}, headers=wrong).status_code == 401
    assert stored(client, recording["id"])["fileName"] == "session.mp4"


def test_an_unknown_recording_is_404(client):
    assert patch(client, str(uuid.uuid4()), {"fileName": "x.mp4"}).status_code == 404
    assert patch(client, "not-an-id", {"fileName": "x.mp4"}).status_code == 404


def test_the_log_names_the_changed_fields_but_never_the_full_link(client, recording, caplog):
    caplog.set_level(logging.INFO, logger=LOGGER_NAME)
    patch(client, recording["id"], {"link": OTHER_DRIVE_LINK, "fileName": "renamed.mp4"})
    assert "recording.patched" in caplog.text
    assert "file_name" in caplog.text and "lms_status" not in caplog.text.split("recording.patched")[0]
    assert "1ZyXwVuTsRqPoNmLkJiHgFeDcBa987654" not in caplog.text and DRIVE_ID not in caplog.text


def test_existing_endpoints_still_answer_as_before(client, recording):
    assert client.get("/api/v1/recordings/latest", headers=client_headers()).json()["count"] == 1
    assert client.get(f"/api/v1/recordings/{recording['id']}", headers=client_headers()).status_code == 200
    again = client.post("/api/v1/recordings/sync", headers=client_headers(),
                        json={"group": "CAI5_AIS4_S7", "date": "2026-09-11", "startTime": "18:00"})
    assert again.status_code == 200 and again.json()["action"] == "updated"

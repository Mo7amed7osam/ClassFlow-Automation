"""The Windows app's updates: published by hand into a folder, handed only to signed-in people."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from test_dashboard import build, login


@pytest.fixture
def dash(settings, clock, tmp_path):  # noqa: ANN001, ANN201
    with build(settings, clock) as client:
        client.app.state.releases_dir = tmp_path
        yield client


def publish(folder: Path, version: str = "1.26.916.1830", payload: bytes = b"MZ fake installer", **overrides) -> str:
    name = f"ZoomAutoAdmit-Setup-{version}.exe"
    (folder / name).write_bytes(payload)
    latest = {"version": version, "fileName": name, "sha256": hashlib.sha256(payload).hexdigest(),
              "size": len(payload), "publishedAt": "2026-09-16T18:30:00+03:00"}
    latest.update(overrides)
    (folder / "latest.json").write_text(json.dumps(latest), encoding="utf-8")
    return latest["sha256"]


def test_nothing_published_says_so(dash):  # noqa: ANN001
    assert login(dash).status_code == 200
    assert dash.get("/api/v1/app/latest").json() == {"version": None}
    assert dash.get("/api/v1/app/download").status_code == 404


def test_a_published_version_is_offered_and_downloads_intact(dash, tmp_path):  # noqa: ANN001
    assert login(dash).status_code == 200
    payload = b"MZ" + bytes(range(256)) * 40
    sha = publish(tmp_path, payload=payload)

    latest = dash.get("/api/v1/app/latest").json()
    assert latest["version"] == "1.26.916.1830"
    assert latest["sha256"] == sha and latest["size"] == len(payload)

    download = dash.get("/api/v1/app/download")
    assert download.status_code == 200
    assert hashlib.sha256(download.content).hexdigest() == sha
    assert download.headers["x-app-version"] == "1.26.916.1830"


def test_only_a_signed_in_person_gets_it(dash, tmp_path):  # noqa: ANN001
    publish(tmp_path)
    assert dash.get("/api/v1/app/latest").status_code == 401
    assert dash.get("/api/v1/app/download").status_code == 401


@pytest.mark.parametrize("broken", [
    {"fileName": "../../secrets.txt"},                  # never a path out of the folder
    {"fileName": "something-else.exe"},
    {"size": 5},                                        # half copied
    {"sha256": "not-a-hash"},
    {"version": "latest"},
])
def test_a_publication_that_does_not_add_up_is_not_offered(dash, tmp_path, broken):  # noqa: ANN001
    assert login(dash).status_code == 200
    publish(tmp_path, **broken)
    assert dash.get("/api/v1/app/latest").json() == {"version": None}
    assert dash.get("/api/v1/app/download").status_code == 404

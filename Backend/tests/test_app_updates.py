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


def publish_files(folder: Path, version: str, files: dict[str, bytes], **manifest_overrides) -> dict[str, str]:
    """Publishes a version with its file list: each file stored once by content, named in latest.json."""
    publish(folder, version=version)
    store = folder / "files"
    store.mkdir(exist_ok=True)
    entries, shas = [], {}
    for path, data in files.items():
        sha = hashlib.sha256(data).hexdigest()
        (store / sha).write_bytes(data)
        entries.append({"path": path, "sha256": sha, "size": len(data)})
        shas[path] = sha
    manifest = {"version": version, "files": entries}
    manifest.update(manifest_overrides)
    name = f"manifest-{version}.json"
    (folder / name).write_text(json.dumps(manifest), encoding="utf-8")
    latest = json.loads((folder / "latest.json").read_text(encoding="utf-8"))
    latest["manifest"] = name
    (folder / "latest.json").write_text(json.dumps(latest), encoding="utf-8")
    return shas


def test_the_file_list_lets_an_app_fetch_only_what_changed(dash, tmp_path):  # noqa: ANN001
    assert login(dash).status_code == 200
    page = r"WebSessions\assets\dashboard.js"
    shas = publish_files(tmp_path, "1.26.917.100", {
        "ZoomAutoAdmit.WindowsUI.exe": b"MZ app",
        page: b"console.log(1)",
    })

    listed = dash.get("/api/v1/app/manifest").json()
    assert listed["version"] == "1.26.917.100"
    assert {f["path"] for f in listed["files"]} == {"ZoomAutoAdmit.WindowsUI.exe", page}

    one = dash.get(f"/api/v1/app/files/{shas[page]}")
    assert one.status_code == 200 and one.content == b"console.log(1)"


def test_a_version_without_a_file_list_falls_back_to_the_installer(dash, tmp_path):  # noqa: ANN001
    assert login(dash).status_code == 200
    publish(tmp_path)
    assert dash.get("/api/v1/app/manifest").status_code == 404


def test_only_files_of_the_published_version_are_handed_out(dash, tmp_path):  # noqa: ANN001
    assert login(dash).status_code == 200
    publish_files(tmp_path, "1.26.917.100", {"a.dll": b"current"})
    stray = hashlib.sha256(b"an old version's file").hexdigest()
    (tmp_path / "files" / stray).write_bytes(b"an old version's file")
    assert dash.get(f"/api/v1/app/files/{stray}").status_code == 404
    assert dash.get("/api/v1/app/files/..%2F..%2Flatest.json").status_code == 404
    assert dash.get("/api/v1/app/files/not-a-hash").status_code == 404


@pytest.mark.parametrize("bad_path", [r"..\..\evil.dll", r"C:\Windows\evil.dll", "/etc/evil", ""])
def test_a_file_list_with_an_unsafe_path_is_not_offered(dash, tmp_path, bad_path):  # noqa: ANN001
    assert login(dash).status_code == 200
    publish_files(tmp_path, "1.26.917.100", {bad_path or "x": b"data"})
    if not bad_path:
        manifest = json.loads((tmp_path / "manifest-1.26.917.100.json").read_text(encoding="utf-8"))
        manifest["files"][0]["path"] = ""
        (tmp_path / "manifest-1.26.917.100.json").write_text(json.dumps(manifest), encoding="utf-8")
    assert dash.get("/api/v1/app/manifest").status_code == 404


def test_the_file_list_needs_a_signed_in_person(dash, tmp_path):  # noqa: ANN001
    shas = publish_files(tmp_path, "1.26.917.100", {"a.dll": b"current"})
    assert dash.get("/api/v1/app/manifest").status_code == 401
    assert dash.get(f"/api/v1/app/files/{shas['a.dll']}").status_code == 401

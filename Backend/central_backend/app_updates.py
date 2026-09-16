"""The Windows app's own updates, handed out by this server.

Every coordinator's copy of the app already talks to this server, so it is also where they get a
new version: the app asks now and then whether there is one, and offers a button when there is.

    GET /api/v1/app/latest      the published version: {"version", "sha256", "size"}, or
                                {"version": null} when nothing is published
    GET /api/v1/app/download    that version's installer
    GET /api/v1/app/manifest    that version's files: {"version", "files": [{"path", "sha256", "size"}]}
    GET /api/v1/app/files/{sha} one of those files, by its SHA-256

The manifest lets an app fetch only the files that changed (a few MB) instead of the whole installer
(170 MB). The files are kept once each by content in releases/files/<sha256>; the manifest is
manifest-<version>.json, named in latest.json as "manifest".

Both need a signed-in person (the dashboard session the app already has).

A version is published by hand, never by a build - Windows/installer/publish-update.ps1 copies one
installer into the releases folder and writes latest.json beside it - so a build that is still
being tried out never reaches anyone. The folder is CENTRAL_RELEASES_DIR, or
%LOCALAPPDATA%\ZoomAutoAdmit\Central\releases on the PC that runs this server.

latest.json:  {"version": "1.26.916.1830", "fileName": "ZoomAutoAdmit-Setup-1.26.916.1830.exe",
               "sha256": "<hex>", "size": 177000000, "publishedAt": "<ISO-8601>"}
"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path
from typing import Any

from fastapi import APIRouter, Depends, Request
from fastapi.responses import FileResponse, JSONResponse

from .access import Viewer, current_viewer
from .api import ApiError

router = APIRouter(dependencies=[Depends(current_viewer)])

_VERSION = re.compile(r"^\d{1,5}(\.\d{1,5}){1,3}$")
_FILE_NAME = re.compile(r"^ZoomAutoAdmit-Setup-[0-9.]+\.exe$")
_SHA256 = re.compile(r"^[0-9a-fA-F]{64}$")
_MANIFEST_NAME = re.compile(r"^manifest-[0-9.]+\.json$")


def default_releases_dir() -> Path:
    configured = os.environ.get("CENTRAL_RELEASES_DIR")
    if configured:
        return Path(configured)
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "ZoomAutoAdmit" / "Central" / "releases"
    return Path(__file__).resolve().parent.parent / "releases"


def published(folder: Path) -> tuple[dict[str, Any], Path] | None:
    """The published version and its installer, or None when nothing (valid) is published.

    Everything in latest.json is checked before it is believed: a file name that is not one of our
    installers, a missing file or a size that does not match means "nothing published" - never a
    path outside the folder, never a half-copied installer."""
    try:
        latest = json.loads((folder / "latest.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None
    if not isinstance(latest, dict):
        return None
    version, name, sha, size = latest.get("version"), latest.get("fileName"), latest.get("sha256"), latest.get("size")
    if not (isinstance(version, str) and _VERSION.match(version)):
        return None
    if not (isinstance(name, str) and _FILE_NAME.match(name)):
        return None
    if not (isinstance(sha, str) and _SHA256.match(sha)) or not isinstance(size, int) or size <= 0:
        return None
    installer = folder / name
    try:
        if not installer.is_file() or installer.stat().st_size != size:
            return None
    except OSError:
        return None
    return {"version": version, "sha256": sha.lower(), "size": size, "publishedAt": latest.get("publishedAt")}, installer


def _safe_relative(path: object) -> bool:
    """A file path inside the app folder: relative, no drive, never "..", never empty."""
    if not isinstance(path, str) or not path or len(path) > 400:
        return False
    parts = path.replace("\\", "/").split("/")
    return not path.startswith(("/", "\\")) and ":" not in path and all(part not in ("", ".", "..") for part in parts)


def published_manifest(folder: Path) -> dict[str, Any] | None:
    """The published version's file list, when one was published with it and every entry is sound."""
    try:
        latest = json.loads((folder / "latest.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None
    if not isinstance(latest, dict):
        return None
    name, version = latest.get("manifest"), latest.get("version")
    if not (isinstance(name, str) and _MANIFEST_NAME.match(name)) or not (isinstance(version, str) and _VERSION.match(version)):
        return None
    try:
        manifest = json.loads((folder / name).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return None
    if not isinstance(manifest, dict) or manifest.get("version") != version or not isinstance(manifest.get("files"), list):
        return None
    files = []
    for entry in manifest["files"]:
        if not isinstance(entry, dict):
            return None
        path, sha, size = entry.get("path"), entry.get("sha256"), entry.get("size")
        if not _safe_relative(path) or not (isinstance(sha, str) and _SHA256.match(sha)) or not isinstance(size, int) or size < 0:
            return None
        download = entry.get("download")
        item = {"path": path, "sha256": sha.lower(), "size": size}
        if isinstance(download, int) and download > 0:
            item["download"] = download
        files.append(item)
    if not files:
        return None
    return {"version": version, "files": files}


def _folder(request: Request) -> Path:
    return getattr(request.app.state, "releases_dir", None) or default_releases_dir()


@router.get("/api/v1/app/latest")
async def latest_version(request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    found = published(_folder(request))
    body = found[0] if found else {"version": None}
    return JSONResponse(body, headers={"Cache-Control": "no-store"})


@router.get("/api/v1/app/download")
async def download(request: Request, viewer: Viewer = Depends(current_viewer)) -> FileResponse:
    found = published(_folder(request))
    if found is None:
        raise ApiError(404, "Not found", "No version of the app is published.")
    info, installer = found
    return FileResponse(installer, media_type="application/octet-stream", filename=installer.name,
                        headers={"Cache-Control": "no-store", "X-App-Version": info["version"], "X-App-Sha256": info["sha256"]})


@router.get("/api/v1/app/manifest")
async def manifest(request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    found = published_manifest(_folder(request))
    if found is None:
        raise ApiError(404, "Not found", "The published version has no file list; download the installer.")
    return JSONResponse(found, headers={"Cache-Control": "no-store"})


@router.get("/api/v1/app/files/{name}")
async def app_file(name: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> FileResponse:
    """One file of the published version, as it is (<sha256>) or compressed (<sha256>.gz). Only a
    file the current manifest names is handed out."""
    packed = name.endswith(".gz")
    sha256 = name[:-3] if packed else name
    if not _SHA256.match(sha256):
        raise ApiError(404, "Not found", "No such file.")
    sha256 = sha256.lower()
    folder = _folder(request)
    found = published_manifest(folder)
    entry = next((f for f in found["files"] if f["sha256"] == sha256), None) if found else None
    stored = folder / "files" / (sha256 + (".gz" if packed else ""))
    try:
        expected = (entry or {}).get("download") if packed else (entry or {}).get("size")
        ok = entry is not None and expected is not None and stored.is_file() and stored.stat().st_size == expected
    except OSError:
        ok = False
    if not ok:
        raise ApiError(404, "Not found", "No such file in the published version.")
    return FileResponse(stored, media_type="application/octet-stream", headers={"Cache-Control": "no-store"})

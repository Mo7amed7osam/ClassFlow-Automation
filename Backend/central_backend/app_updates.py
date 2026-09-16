"""The Windows app's own updates, handed out by this server.

Every coordinator's copy of the app already talks to this server, so it is also where they get a
new version: the app asks now and then whether there is one, and offers a button when there is.

    GET /api/v1/app/latest      the published version: {"version", "sha256", "size"}, or
                                {"version": null} when nothing is published
    GET /api/v1/app/download    that version's installer

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

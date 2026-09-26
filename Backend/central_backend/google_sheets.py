"""First-party, read-only Google Sheets recording sync.

No workflow runner is between the sheet and ClassFlow.  OAuth tokens are encrypted in PostgreSQL,
the sheet is only read, and each source row has a durable processing record so restarts cannot
create duplicate LMS work.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass
from datetime import date, datetime
from typing import Any

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse, RedirectResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import func, select

from .api import ApiError, _json
from .auth import CurrentUser, current_user, require_admin, require_dashboard_header
from .models import GoogleSheetsConnection, GoogleSheetsSyncRecord, Recording
from .recordings import _same_session, sync_recording
from .user_data import SecretBox
from .scheduling import CAIRO
from .observability import emit

router = APIRouter()

GOOGLE_AUTH_URL = "https://accounts.google.com/o/oauth2/v2/auth"
GOOGLE_TOKEN_URL = "https://oauth2.googleapis.com/token"
GOOGLE_USERINFO_URL = "https://openidconnect.googleapis.com/v1/userinfo"
GOOGLE_SHEETS_URL = "https://sheets.googleapis.com/v4/spreadsheets"
GOOGLE_SCOPE = "https://www.googleapis.com/auth/spreadsheets.readonly openid email"
_DRIVE = re.compile(r"^https://drive\.google\.com/(?:file/d/|open\?id=)[A-Za-z0-9_-]+", re.I)
_GROUP_TAB = re.compile(r"^[A-Z0-9]+(?:_[A-Z0-9]+)+$")


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


def _box(request: Request) -> SecretBox:
    box = getattr(request.app.state, "secret_box", None)
    if box is None:
        raise ApiError(503, "Not configured", "The server has no secrets key, so it cannot keep a Google connection.")
    return box


def _oauth_config() -> tuple[str, str, str]:
    client_id = (os.environ.get("CENTRAL_GOOGLE_OAUTH_CLIENT_ID") or "").strip()
    client_secret = (os.environ.get("CENTRAL_GOOGLE_OAUTH_CLIENT_SECRET") or "").strip()
    redirect_uri = (os.environ.get("CENTRAL_GOOGLE_OAUTH_REDIRECT_URI") or "").strip()
    if not (client_id and client_secret and redirect_uri):
        raise ApiError(503, "Google OAuth is not configured", "Set the Google OAuth client ID, client secret and redirect URL on the server.")
    if not redirect_uri.startswith("https://"):
        raise ApiError(503, "Google OAuth is not configured", "The Google OAuth redirect URL must be https.")
    return client_id, client_secret, redirect_uri


def _request(url: str, *, method: str = "GET", body: bytes | None = None, headers: dict[str, str] | None = None) -> dict[str, Any]:
    request = urllib.request.Request(url, data=body, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:  # noqa: S310 - fixed Google endpoints
            parsed = json.loads(response.read().decode("utf-8"))
            if not isinstance(parsed, dict):
                raise ValueError("unexpected response")
            return parsed
    except (urllib.error.HTTPError, urllib.error.URLError, ValueError, json.JSONDecodeError) as exc:
        raise RuntimeError("Google did not accept the request.") from exc


async def _google_request(*args, **kwargs) -> dict[str, Any]:  # noqa: ANN002, ANN003
    return await asyncio.to_thread(_request, *args, **kwargs)


def _headers(values: list[Any]) -> dict[str, int]:
    result: dict[str, int] = {}
    for index, value in enumerate(values):
        key = str(value).strip().casefold()
        if key:
            result[key] = index
    return result


def _cell(row: list[Any], columns: dict[str, int], name: str) -> str:
    index = columns.get(name.casefold())
    return str(row[index]).strip() if index is not None and index < len(row) else ""


def _sheet_date(value: str) -> date | None:
    value = value.strip()
    for pattern in ("%Y-%m-%d", "%d/%m/%Y", "%m/%d/%Y"):
        try:
            return datetime.strptime(value, pattern).date()
        except ValueError:
            pass
    return None


@dataclass(frozen=True)
class SheetRow:
    group: str
    day: date
    file_name: str
    record_type: str
    drive_link: str
    row_key: str


class GoogleSheetsSynchronizer:
    def __init__(self, sessionmaker, secret_box: SecretBox, clock) -> None:  # noqa: ANN001
        self._sessionmaker, self._box, self._clock = sessionmaker, secret_box, clock

    async def _access_token(self, connection: GoogleSheetsConnection) -> str:
        _, client_secret, redirect_uri = _oauth_config()
        client_id = os.environ["CENTRAL_GOOGLE_OAUTH_CLIENT_ID"].strip()
        refresh = self._box.open(connection.refresh_token_encrypted, f"google-sheets:{connection.id}")
        payload = urllib.parse.urlencode({"client_id": client_id, "client_secret": client_secret,
                                          "refresh_token": refresh, "grant_type": "refresh_token",
                                          "redirect_uri": redirect_uri}).encode()
        reply = await _google_request(GOOGLE_TOKEN_URL, method="POST", body=payload,
                                      headers={"Content-Type": "application/x-www-form-urlencoded"})
        token = reply.get("access_token")
        if not isinstance(token, str) or not token:
            raise RuntimeError("Google did not return an access token.")
        return token

    async def _rows(self, connection: GoogleSheetsConnection, token: str) -> list[SheetRow]:
        auth = {"Authorization": f"Bearer {token}"}
        metadata = await _google_request(f"{GOOGLE_SHEETS_URL}/{urllib.parse.quote(connection.spreadsheet_id, safe='')}?fields=sheets.properties.title", headers=auth)
        tabs = [item.get("properties", {}).get("title") for item in metadata.get("sheets", []) if isinstance(item, dict)]
        # A spreadsheet often has a Notes, Read me or archive tab.  Only the LMS-code tabs are
        # class data; the rest must never become accidental groups in ClassFlow.
        groups = [name for name in tabs if isinstance(name, str) and _GROUP_TAB.match(name.strip())]
        if not groups:
            return []
        ranges = "&".join("ranges=" + urllib.parse.quote(f"'{name.replace(chr(39), chr(39) * 2)}'", safe="") for name in groups)
        data = await _google_request(f"{GOOGLE_SHEETS_URL}/{urllib.parse.quote(connection.spreadsheet_id, safe='')}/values:batchGet?{ranges}", headers=auth)
        rows: list[SheetRow] = []
        for tab, value_range in zip(groups, data.get("valueRanges", []), strict=False):
            values = value_range.get("values", []) if isinstance(value_range, dict) else []
            if not values or not isinstance(values[0], list):
                continue
            columns = _headers(values[0])
            if not {"file name", "date", "shared link"}.issubset(columns):
                continue
            for row_index, raw in enumerate(values[1:], start=2):
                if not isinstance(raw, list):
                    continue
                link, day = _cell(raw, columns, "shared link"), _sheet_date(_cell(raw, columns, "date"))
                if day is None or not _DRIVE.match(link):
                    continue
                row_key = hashlib.sha256(f"{tab}|{row_index}|{link}".encode()).hexdigest()
                rows.append(SheetRow(tab, day, _cell(raw, columns, "file name"), _cell(raw, columns, "type"), link, row_key))
        return rows

    async def sync_once(self) -> dict[str, int]:
        async with self._sessionmaker() as session:
            connection = (await session.execute(select(GoogleSheetsConnection).order_by(GoogleSheetsConnection.connected_at.desc()).limit(1))).scalar_one_or_none()
        if connection is None:
            return {"seen": 0, "pending": 0, "conflict": 0, "skipped": 0}
        token = await self._access_token(connection)
        rows = await self._rows(connection, token)
        counts = {"seen": len(rows), "pending": 0, "conflict": 0, "skipped": 0}
        now = self._clock()
        async with self._sessionmaker() as session, session.begin():
            for row in rows:
                known = (await session.execute(select(GoogleSheetsSyncRecord).where(
                    GoogleSheetsSyncRecord.connection_id == connection.id,
                    GoogleSheetsSyncRecord.row_key == row.row_key))).scalar_one_or_none()
                if known is not None:
                    counts["skipped"] += 1
                    continue
                same = (await session.execute(select(Recording).where(*_same_session(row.group, row.day, None)).with_for_update())).scalar_one_or_none()
                record = GoogleSheetsSyncRecord(id=uuid.uuid4(), connection_id=connection.id, row_key=row.row_key,
                                                group_name=row.group, session_date=row.day, drive_link=row.drive_link,
                                                created_at=now, updated_at=now)
                session.add(record)
                if same is not None and same.drive_link and same.drive_link != row.drive_link:
                    record.status, record.detail, record.processed_at = "conflict", "A different Drive link already exists for this group and date.", now
                    counts["conflict"] += 1
                    continue
                recording, _ = await sync_recording(session, {"group_name": row.group, "session_date": row.day,
                    "start_time": None, "file_name": row.file_name or None, "record_type": row.record_type or None,
                    "drive_link": row.drive_link}, now)
                record.recording_id, record.status, record.processed_at = recording.id, "pending", now
                counts["pending"] += 1
        return counts

    async def run_daily(self, stop: asyncio.Event) -> None:
        """Run once during the 08:00 Cairo minute and recover after a brief restart.

        The row ledger makes a repeat harmless.  A long outage intentionally waits for the next
        scheduled morning rather than writing an old Drive link at an unpredictable hour.
        """
        last_run: date | None = None
        while not stop.is_set():
            local = self._clock().astimezone(CAIRO)
            if local.hour == 8 and last_run != local.date():
                try:
                    result = await self.sync_once()
                    last_run = local.date()
                    emit("google_sheets.sync", **result)
                except asyncio.CancelledError:
                    raise
                except Exception as problem:  # noqa: BLE001 - the next minute retries the safe read
                    emit("google_sheets.sync_failed", error=type(problem).__name__)
            try:
                await asyncio.wait_for(stop.wait(), timeout=30)
            except TimeoutError:
                pass


class ConnectBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    spreadsheetId: str = Field(min_length=10, max_length=200)


@router.get("/api/v1/google-sheets", dependencies=[Depends(require_admin)])
async def google_status(request: Request) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        connection = (await session.execute(select(GoogleSheetsConnection).order_by(GoogleSheetsConnection.connected_at.desc()).limit(1))).scalar_one_or_none()
        recent = (await session.execute(select(GoogleSheetsSyncRecord.status, func.count()).group_by(GoogleSheetsSyncRecord.status))).all()
    return _no_store({"configured": connection is not None, "spreadsheetId": connection.spreadsheet_id if connection else None,
                      "googleEmail": connection.google_email if connection else None, "connectedAt": connection.connected_at if connection else None,
                      "states": {status: count for status, count in recent}})


@router.post("/api/v1/google-sheets/connect", dependencies=[Depends(require_dashboard_header)])
async def google_connect(body: ConnectBody, request: Request, user: CurrentUser = Depends(require_admin)) -> JSONResponse:
    client_id, _, redirect_uri = _oauth_config()
    state = _box(request).seal(json.dumps({"user": str(user.id), "sheet": body.spreadsheetId.strip()}), "google-oauth-state")
    query = urllib.parse.urlencode({"client_id": client_id, "redirect_uri": redirect_uri, "response_type": "code",
                                    "scope": GOOGLE_SCOPE, "access_type": "offline", "prompt": "consent", "state": state})
    return _no_store({"authorizationUrl": f"{GOOGLE_AUTH_URL}?{query}"})


@router.get("/api/v1/google-sheets/callback")
async def google_callback(request: Request, code: str = Query(min_length=1), state: str = Query(min_length=1)) -> RedirectResponse:
    box = _box(request)
    try:
        saved = json.loads(box.open(state, "google-oauth-state"))
        user_id, spreadsheet_id = uuid.UUID(saved["user"]), str(saved["sheet"])
        client_id, client_secret, redirect_uri = _oauth_config()
        payload = urllib.parse.urlencode({"code": code, "client_id": client_id, "client_secret": client_secret,
                                          "redirect_uri": redirect_uri, "grant_type": "authorization_code"}).encode()
        token = await _google_request(GOOGLE_TOKEN_URL, method="POST", body=payload,
                                      headers={"Content-Type": "application/x-www-form-urlencoded"})
        refresh = token.get("refresh_token")
        if not isinstance(refresh, str) or not refresh:
            raise ValueError("missing refresh token")
        identity = await _google_request(GOOGLE_USERINFO_URL, headers={"Authorization": f"Bearer {token.get('access_token', '')}"})
    except Exception as exc:  # noqa: BLE001 - OAuth responses and token values never enter logs
        raise ApiError(400, "Google connection failed", "Google did not return a usable authorization. Start the connection again.") from exc
    now = request.app.state.clock()
    async with request.app.state.sessionmaker() as session, session.begin():
        existing = (await session.execute(select(GoogleSheetsConnection).limit(1).with_for_update())).scalar_one_or_none()
        connection = existing or GoogleSheetsConnection(id=uuid.uuid4(), connected_at=now)
        if existing is None:
            session.add(connection)
        connection.spreadsheet_id, connection.connected_by, connection.updated_at = spreadsheet_id, user_id, now
        connection.google_email = identity.get("email") if isinstance(identity.get("email"), str) else None
        connection.refresh_token_encrypted = box.seal(refresh, f"google-sheets:{connection.id}")
    return RedirectResponse("/dashboard/settings?google=connected", status_code=303)


@router.post("/api/v1/google-sheets/sync", dependencies=[Depends(require_dashboard_header)])
async def google_sync(request: Request, _: CurrentUser = Depends(require_admin)) -> JSONResponse:
    synchronizer = GoogleSheetsSynchronizer(request.app.state.sessionmaker, _box(request), request.app.state.clock)
    try:
        result = await synchronizer.sync_once()
    except RuntimeError as exc:
        raise ApiError(502, "Google Sheet sync failed", "Google could not be reached. Nothing was changed; try again later.") from exc
    return _no_store(result)

"""What a dashboard user keeps on the server instead of on one PC.

Their LMS accounts (the sign-ins their app uses on the LMS), signed-in user only:
    GET    /api/v1/me/lms-accounts                 list (never a password)
    POST   /api/v1/me/lms-accounts                 add, or update the one with that email
    POST   /api/v1/me/lms-accounts/{id}/use        make it the one their app signs in with
    DELETE /api/v1/me/lms-accounts/{id}            remove it
    POST   /api/v1/me/lms-accounts/{id}/secret     its email and password, for their own app

The Zoom accounts their app opens classes with (no password: the Zoom sign-in stays in the Zoom
app's saved accounts or a browser profile on that PC):
    GET    /api/v1/me/zoom-accounts                list
    PUT    /api/v1/me/zoom-accounts                the whole set, as that PC has them

Settings every copy of the app shares (read by anyone signed in, written by the admin):
    GET    /api/v1/settings/{key}
    PUT    /api/v1/settings/{key}

An LMS password is stored AES-GCM encrypted with CENTRAL_SECRETS_KEY (32 random bytes, base64),
which lives with the server's other secrets and never in the database: a copy of the database
alone does not give the passwords. Without the key, saving or reading a password is refused (503).
Every write needs X-Dashboard-Request: 1, the secret included (a page on another site cannot
send it); answers are never cached. A user sees and uses only their own accounts.
"""

from __future__ import annotations

import base64
import os
import secrets
import uuid
from collections.abc import Mapping
from typing import Any

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import func, select, update

from .api import ApiError, _json
from .auth import CurrentUser, current_user, require_dashboard_header
from .config import ConfigurationError
from .models import AppSetting, LmsAccount, ZoomAccount
from .observability import emit

SECRETS_KEY_VARIABLE = "CENTRAL_SECRETS_KEY"
SETTING_KEYS = {"recordingsSheet"}
MAX_SETTING_BYTES = 16 * 1024

router = APIRouter()


class SecretBox:
    """AES-GCM with a 256-bit key. Each value gets its own random nonce and is bound to its row
    (the associated data), so a ciphertext copied onto another account does not decrypt."""

    VERSION = "v1"

    def __init__(self, key: bytes) -> None:
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM

        if len(key) != 32:
            raise ConfigurationError(f"{SECRETS_KEY_VARIABLE} must be 32 bytes (base64).")
        self._aead = AESGCM(key)

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> SecretBox | None:
        env = os.environ if env is None else env
        text = (env.get(SECRETS_KEY_VARIABLE) or "").strip()
        if not text:
            return None
        try:
            key = base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))
        except (ValueError, TypeError) as exc:
            raise ConfigurationError(f"{SECRETS_KEY_VARIABLE} is not base64.") from exc
        return cls(key)

    @staticmethod
    def new_key() -> str:
        return base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("=")

    def seal(self, plaintext: str, bound_to: str) -> str:
        nonce = secrets.token_bytes(12)
        sealed = self._aead.encrypt(nonce, plaintext.encode("utf-8"), bound_to.encode("utf-8"))
        return f"{self.VERSION}.{base64.b64encode(nonce + sealed).decode()}"

    def open(self, stored: str, bound_to: str) -> str:
        version, _, body = stored.partition(".")
        if version != self.VERSION:
            raise ValueError("unknown format")
        raw = base64.b64decode(body)
        return self._aead.decrypt(raw[:12], raw[12:], bound_to.encode("utf-8")).decode("utf-8")


def _box(request: Request) -> SecretBox:
    box = getattr(request.app.state, "secret_box", None)
    if box is None:
        raise ApiError(503, "Not configured", f"The server has no {SECRETS_KEY_VARIABLE}, so it cannot keep LMS passwords.")
    return box


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


def _view(account: LmsAccount) -> dict[str, Any]:
    return {"id": str(account.id), "label": account.label, "email": account.email, "role": account.role,
            "active": account.active, "updatedAt": account.updated_at}


async def _own(session, user: CurrentUser, account_id: uuid.UUID) -> LmsAccount:  # noqa: ANN001
    account = await session.get(LmsAccount, account_id)
    if account is None or account.user_id != user.id:
        raise ApiError(404, "Not found")
    return account


# ----------------------------------------------------------------------------- LMS accounts


class LmsAccountBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    label: str = Field(default="", max_length=100)
    email: str = Field(min_length=3, max_length=320)
    role: str = Field(default="coordinator", pattern="^(admin|coordinator)$")
    password: str = Field(min_length=1, max_length=500)
    active: bool = True


@router.get("/api/v1/me/lms-accounts")
async def list_accounts(request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        rows = (await session.execute(
            select(LmsAccount).where(LmsAccount.user_id == user.id).order_by(LmsAccount.active.desc(), LmsAccount.label)
        )).scalars().all()
    return _no_store({"accounts": [_view(a) for a in rows], "canKeepPasswords": getattr(request.app.state, "secret_box", None) is not None})


@router.post("/api/v1/me/lms-accounts", dependencies=[Depends(require_dashboard_header)])
async def save_account(body: LmsAccountBody, request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    box = _box(request)
    email = body.email.strip()
    if "@" not in email or any(c.isspace() for c in email):
        raise ApiError(400, "Invalid request", "That is not an email address.")
    label = body.label.strip()[:100] or ("Admin" if body.role == "admin" else "Coordinator")
    now = request.app.state.clock()
    async with request.app.state.sessionmaker() as session, session.begin():
        account = (await session.execute(
            select(LmsAccount).where(LmsAccount.user_id == user.id, func.lower(LmsAccount.email) == email.lower())
        )).scalar_one_or_none()
        created = account is None
        if account is None:
            account = LmsAccount(id=uuid.uuid4(), user_id=user.id, email=email, created_at=now)
            session.add(account)
        account.label, account.role, account.updated_at = label, body.role, now
        account.password_encrypted = box.seal(body.password, f"{user.id}:{account.id}")
        if body.active:
            await session.execute(update(LmsAccount).where(LmsAccount.user_id == user.id, LmsAccount.id != account.id).values(active=False))
            await session.flush()
            account.active = True
        elif created:
            account.active = False
        await session.flush()
        view = _view(account)
    emit("lms_account.saved", username=user.username, account=str(account.id), created=created)
    return _no_store(view, 201 if created else 200)


@router.post("/api/v1/me/lms-accounts/{account_id}/use", dependencies=[Depends(require_dashboard_header)])
async def use_account(account_id: uuid.UUID, request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    async with request.app.state.sessionmaker() as session, session.begin():
        account = await _own(session, user, account_id)
        await session.execute(update(LmsAccount).where(LmsAccount.user_id == user.id).values(active=False))
        await session.flush()
        account.active = True
        account.updated_at = request.app.state.clock()
        await session.flush()
        view = _view(account)
    emit("lms_account.used", username=user.username, account=str(account_id))
    return _no_store(view)


@router.delete("/api/v1/me/lms-accounts/{account_id}", dependencies=[Depends(require_dashboard_header)])
async def delete_account(account_id: uuid.UUID, request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    async with request.app.state.sessionmaker() as session, session.begin():
        await session.delete(await _own(session, user, account_id))
    emit("lms_account.deleted", username=user.username, account=str(account_id))
    return _no_store({"status": "deleted"})


@router.post("/api/v1/me/lms-accounts/{account_id}/secret", dependencies=[Depends(require_dashboard_header)])
async def account_secret(account_id: uuid.UUID, request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    """The sign-in itself, for the user's own app to sign in to the LMS with. Never logged."""
    box = _box(request)
    async with request.app.state.sessionmaker() as session:
        account = await _own(session, user, account_id)
        try:
            password = box.open(account.password_encrypted, f"{user.id}:{account.id}")
        except Exception as exc:  # noqa: BLE001 - a changed key or a damaged row; the value never goes to the log
            raise ApiError(409, "Cannot decrypt", "Save this account's password again.") from exc
    emit("lms_account.secret_read", username=user.username, account=str(account_id))
    return _no_store({"id": str(account_id), "email": account.email, "password": password})


# ----------------------------------------------------------------------------- Zoom accounts


MAX_ZOOM_ACCOUNTS = 50


def zoom_view(account: ZoomAccount) -> dict[str, Any]:
    return {"id": str(account.id), "accountId": account.account_id, "label": account.label,
            "zoomEmail": account.zoom_email, "group": account.group_name,
            "meetingUrl": account.default_meeting_url, "preferredEngine": account.preferred_engine,
            "active": account.active, "updatedAt": account.updated_at}


class ZoomAccountBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    accountId: str = Field(min_length=1, max_length=100)
    label: str = Field(default="", max_length=100)
    zoomEmail: str | None = Field(default=None, max_length=320)
    group: str | None = Field(default=None, max_length=100)
    meetingUrl: str | None = None
    preferredEngine: str | None = Field(default=None, pattern="^(desktop|web)$")
    active: bool = False


class ZoomAccountsBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    accounts: list[ZoomAccountBody] = Field(max_length=MAX_ZOOM_ACCOUNTS)


def _meeting_url(value: str | None) -> str | None:
    """A link the app can open, or nothing. What it points at is Zoom's business, not ours."""
    if value is None:
        return None
    url = value.strip()
    if not url:
        return None
    if len(url) > 2048 or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in url):
        raise ApiError(400, "Invalid request", "That meeting link is not a link.")
    if not url.lower().startswith("https://"):
        raise ApiError(400, "Invalid request", "A meeting link must start with https://.")
    return url


@router.get("/api/v1/me/zoom-accounts")
async def list_zoom_accounts(request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        rows = (await session.execute(
            select(ZoomAccount).where(ZoomAccount.user_id == user.id)
            .order_by(ZoomAccount.active.desc(), ZoomAccount.account_id)
        )).scalars().all()
    return _no_store({"accounts": [zoom_view(a) for a in rows]})


@router.put("/api/v1/me/zoom-accounts", dependencies=[Depends(require_dashboard_header)])
async def save_zoom_accounts(body: ZoomAccountsBody, request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    """The accounts this person's app has, as a whole set: what their PC knows is what is kept.

    An account is matched by the id its PC knows it by, so re-sending the same list changes nothing
    and an account removed there stops being offered to whoever runs their classes.
    """
    now = request.app.state.clock()
    seen: set[str] = set()
    async with request.app.state.sessionmaker() as session, session.begin():
        existing = {
            account.account_id.lower(): account
            for account in (await session.execute(
                select(ZoomAccount).where(ZoomAccount.user_id == user.id).with_for_update()
            )).scalars()
        }
        for item in body.accounts:
            account_id = item.accountId.strip()
            key = account_id.lower()
            if not account_id or key in seen:
                continue
            seen.add(key)
            account = existing.get(key)
            if account is None:
                account = ZoomAccount(id=uuid.uuid4(), user_id=user.id, account_id=account_id, created_at=now)
                session.add(account)
                existing[key] = account
            account.account_id = account_id
            account.label = item.label.strip()[:100] or account_id
            account.zoom_email = (item.zoomEmail or "").strip() or None
            account.group_name = (item.group or "").strip() or None
            account.default_meeting_url = _meeting_url(item.meetingUrl)
            account.preferred_engine = item.preferredEngine
            account.active = item.active
            account.updated_at = now
        for key, account in existing.items():
            if key not in seen:
                await session.delete(account)
        await session.flush()
        kept = [zoom_view(account) for key, account in sorted(existing.items()) if key in seen]
    emit("zoom_accounts.saved", username=user.username, count=len(kept))
    return _no_store({"accounts": kept})


# ----------------------------------------------------------------------------- shared settings


class SettingBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    value: dict[str, Any]


def _setting_key(key: str) -> str:
    if key not in SETTING_KEYS:
        raise ApiError(404, "Not found")
    return key


@router.get("/api/v1/settings/{key}")
async def read_setting(key: str, request: Request, _: CurrentUser = Depends(current_user)) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        row = await session.get(AppSetting, _setting_key(key))
    return _no_store({"key": key, "value": row.value if row else None, "updatedAt": row.updated_at if row else None})


@router.put("/api/v1/settings/{key}", dependencies=[Depends(require_dashboard_header)])
async def write_setting(key: str, body: SettingBody, request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    if not user.is_admin:
        raise ApiError(403, "Forbidden", "Only the admin can change shared settings.")
    _setting_key(key)
    import json

    if len(json.dumps(body.value)) > MAX_SETTING_BYTES:
        raise ApiError(400, "Invalid request", "That setting is too large.")
    now = request.app.state.clock()
    async with request.app.state.sessionmaker() as session, session.begin():
        row = await session.get(AppSetting, key)
        if row is None:
            session.add(AppSetting(key=key, value=body.value, updated_by=user.id, updated_at=now))
        else:
            row.value, row.updated_by, row.updated_at = body.value, user.id, now
    emit("setting.saved", username=user.username, key=key)
    return _no_store({"key": key, "value": body.value, "updatedAt": now})

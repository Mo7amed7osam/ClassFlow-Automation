"""Dashboard sign-in for the people who use it: the one admin and the coordinators.

Accounts live in the users table (never in the environment):
  * the admin is created once on the server:  python -m central_backend.cli create-admin
  * coordinators register themselves (status "pending" until the admin approves) or are created
    by the admin; only "active" accounts can sign in.

A sign-in gives a random session token in a cookie the page's scripts cannot read (HttpOnly), only
sent over HTTPS outside development (Secure, __Host- prefix), never sent from another site
(SameSite=Strict). The database keeps only its SHA-256. Every request re-reads the user, so a
disabled account or a changed role counts at once. Every POST/PATCH/PUT also needs the header
X-Dashboard-Request: 1, which a page on another site cannot send.

This is a third kind of credential, apart from n8n's API keys and the agents' device tokens; none
of the three works where another is expected.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import logging
import os
import re
import secrets
import uuid
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import datetime, timedelta
from functools import lru_cache
from typing import Any

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import delete, select
from sqlalchemy.ext.asyncio import AsyncSession

from .api import ApiError, _json
from .config import ConfigurationError
from .groups import assigned_groups, group_view
from .models import AdminSession, User
from .observability import emit
from .security import hash_secret

SESSION_HOURS_VARIABLE = "CENTRAL_SESSION_HOURS"
REMEMBER_DAYS_VARIABLE = "CENTRAL_REMEMBER_DAYS"
LEGACY_SESSION_HOURS_VARIABLE = "CENTRAL_ADMIN_SESSION_HOURS"
ALLOW_REGISTRATION_VARIABLE = "CENTRAL_ALLOW_REGISTRATION"
LEGACY_ADMIN_USERS_VARIABLE = "CENTRAL_ADMIN_USERS"
CSRF_HEADER = "X-Dashboard-Request"
SESSION_TOKEN_PREFIX = "zaas_"
MIN_PASSWORD, MAX_PASSWORD = 12, 200
MINIMUM_SCRYPT_N = 2**14

USERNAME = re.compile(r"^[a-z0-9][a-z0-9._-]{2,49}$")
_HASH = re.compile(r"^scrypt\$(\d+)\$(\d+)\$(\d+)\$([A-Za-z0-9+/=]+)\$([A-Za-z0-9+/=]+)$")

router = APIRouter()


# ----------------------------------------------------------------------------- passwords


def hash_password(password: str, *, n: int = 2**15, r: int = 8, p: int = 1) -> str:
    """scrypt (Python's standard library), with its cost and salt stored alongside the hash."""
    salt = secrets.token_bytes(16)
    digest = _scrypt(password, salt, n, r, p)
    return f"scrypt${n}${r}${p}${base64.b64encode(salt).decode()}${base64.b64encode(digest).decode()}"


def verify_password(password: str, stored: str) -> bool:
    match = _HASH.match(stored)
    if match is None:
        return False
    n, r, p = (int(match.group(i)) for i in (1, 2, 3))
    salt, expected = base64.b64decode(match.group(4)), base64.b64decode(match.group(5))
    return hmac.compare_digest(_scrypt(password, salt, n, r, p, len(expected)), expected)


def is_password_hash(value: str) -> bool:
    match = _HASH.match(value.strip())
    return match is not None and int(match.group(1)) >= MINIMUM_SCRYPT_N


def _scrypt(password: str, salt: bytes, n: int, r: int, p: int, length: int = 32) -> bytes:
    return hashlib.scrypt(password.encode("utf-8"), salt=salt, n=n, r=r, p=p, dklen=length,
                          maxmem=128 * r * n * p + 2 * 1024 * 1024)


@lru_cache(maxsize=1)
def _decoy_hash() -> str:
    """Checked against when the user does not exist, so that costs as long as a wrong password."""
    return hash_password(secrets.token_urlsafe(16))


def normalise_username(value: str) -> str:
    name = (value or "").strip().lower()
    if not USERNAME.match(name):
        raise ApiError(400, "Invalid request", "The username needs 3-50 characters: lower-case letters, digits and . _ -")
    return name


def check_new_password(password: str, username: str) -> None:
    if not MIN_PASSWORD <= len(password) <= MAX_PASSWORD:
        raise ApiError(400, "Invalid request", f"The password needs {MIN_PASSWORD} to {MAX_PASSWORD} characters.")
    if password.strip().lower() == username.lower():
        raise ApiError(400, "Invalid request", "The password cannot be the username.")


# The names CENTRAL_ADMIN_USERS accepted (1-50 characters), so every account that worked there can move.
_LEGACY_USERNAME = re.compile(r"^[a-z0-9][a-z0-9._-]{0,49}$")


def parse_legacy_admin_users(value: str | None) -> tuple[dict[str, str], list[str]]:
    """CENTRAL_ADMIN_USERS ("name:scrypt$...,..."), the old way to configure the admin: only read
    by `cli create-admin --from-env`, to move that account into the database.

    Returns (usable entries by lower-cased name, why each other entry was skipped). The reasons
    name the entry but never repeat its hash."""
    users: dict[str, str] = {}
    skipped: list[str] = []
    for entry in re.split(r"[,\s]+", value or ""):
        if not entry:
            continue
        name, _, stored = entry.partition(":")
        name = name.strip().lower()
        if not _LEGACY_USERNAME.match(name):
            skipped.append(f"'{name[:50]}' is not a valid username")
        elif not _HASH.match(stored.strip()):
            skipped.append(f"'{name}' has no scrypt hash")
        elif not is_password_hash(stored):
            skipped.append(f"'{name}' has a hash that is too weak (N < {MINIMUM_SCRYPT_N})")
        else:
            users[name] = stored.strip()
    return users, skipped


# ----------------------------------------------------------------------------- settings


@dataclass(frozen=True)
class AuthSettings:
    session_hours: float = 8.0
    # "Keep me signed in": how long such a session lasts (the session lives in the database and
    # ends at once when the account is disabled or its password changes).
    remember_days: float = 30.0
    allow_registration: bool = True
    max_failures: int = 5
    lockout_seconds: int = 900
    registrations_per_hour: int = 5
    legacy_admin_variable_set: bool = False

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> AuthSettings:
        env = os.environ if env is None else env
        hours_text = (env.get(SESSION_HOURS_VARIABLE) or env.get(LEGACY_SESSION_HOURS_VARIABLE) or "").strip()
        hours = 8.0
        if hours_text:
            try:
                hours = float(hours_text)
            except ValueError:
                hours = -1
            if not 0.25 <= hours <= 72:
                raise ConfigurationError(f"{SESSION_HOURS_VARIABLE} must be between 0.25 and 72 hours.")
        days_text = (env.get(REMEMBER_DAYS_VARIABLE) or "").strip()
        days = 30.0
        if days_text:
            try:
                days = float(days_text)
            except ValueError:
                days = -1
            if not 1 <= days <= 90:
                raise ConfigurationError(f"{REMEMBER_DAYS_VARIABLE} must be between 1 and 90 days.")
        registration = (env.get(ALLOW_REGISTRATION_VARIABLE) or "true").strip().lower()
        if registration not in {"true", "false"}:
            raise ConfigurationError(f"{ALLOW_REGISTRATION_VARIABLE} must be true or false.")
        return cls(session_hours=hours, remember_days=days, allow_registration=registration == "true",
                   legacy_admin_variable_set=bool((env.get(LEGACY_ADMIN_USERS_VARIABLE) or "").strip()))


class RateLimit:
    """At most `limit` events per key within `window`; the answer is how long to wait, or None."""

    def __init__(self, limit: int, window: timedelta) -> None:
        self._limit = limit
        self._window = window
        self._events: dict[tuple[str, str], list[datetime]] = {}

    def _recent(self, key: tuple[str, str], now: datetime) -> list[datetime]:
        recent = [t for t in self._events.get(key, []) if t > now - self._window]
        if recent:
            self._events[key] = recent
        else:
            self._events.pop(key, None)
        return recent

    def retry_after(self, key: tuple[str, str], now: datetime, limit: int | None = None) -> int | None:
        recent = self._recent(key, now)
        if len(recent) >= (limit or self._limit):
            return max(1, int((recent[0] + self._window - now).total_seconds()))
        return None

    def add(self, key: tuple[str, str], now: datetime) -> None:
        self._events.setdefault(key, []).append(now)

    def clear(self, key: tuple[str, str]) -> None:
        self._events.pop(key, None)


class LoginThrottle:
    """After too many failed sign-ins for one name from one address, or from one address at all,
    further attempts are refused for a while - before the password is even looked at."""

    def __init__(self, max_failures: int, lockout: timedelta) -> None:
        self._max = max_failures
        self._failures = RateLimit(max_failures, lockout)

    def retry_after(self, username: str, address: str, now: datetime) -> int | None:
        return (self._failures.retry_after((username, address), now)
                or self._failures.retry_after(("*", address), now, self._max * 4))

    def failed(self, username: str, address: str, now: datetime) -> None:
        self._failures.add((username, address), now)
        self._failures.add(("*", address), now)

    def succeeded(self, username: str, address: str) -> None:
        self._failures.clear((username, address))


# ----------------------------------------------------------------------------- the signed-in user


@dataclass(frozen=True)
class CurrentUser:
    id: uuid.UUID
    username: str
    display_name: str
    role: str
    session_id: uuid.UUID
    expires_at: datetime

    @property
    def is_admin(self) -> bool:
        return self.role == "admin"


def cookie_name(request: Request) -> str:
    # The __Host- prefix makes browsers insist on Secure, Path=/ and no Domain: only usable over HTTPS.
    return "__Host-zaa_session" if request.app.state.settings.require_https else "zaa_session"


async def current_user(request: Request) -> CurrentUser:
    """The signed-in, active user, or 401."""
    token = request.cookies.get(cookie_name(request))
    if not token or not token.startswith(SESSION_TOKEN_PREFIX) or len(token) > 200:
        raise ApiError(401, "Unauthorized")
    state = request.app.state
    async with state.sessionmaker() as session:
        row = (
            await session.execute(
                select(AdminSession, User).join(User, User.id == AdminSession.user_id)
                .where(AdminSession.token_hash == hash_secret(token), AdminSession.expires_at > state.clock(),
                       User.status == "active")
            )
        ).first()
    if row is None:
        raise ApiError(401, "Unauthorized")
    stored, user = row
    return CurrentUser(id=user.id, username=user.username, display_name=user.display_name, role=user.role,
                       session_id=stored.id, expires_at=stored.expires_at)


async def require_admin(user: CurrentUser = Depends(current_user)) -> CurrentUser:
    if not user.is_admin:
        raise ApiError(403, "Forbidden", "Only the admin can do this.")
    return user


def require_dashboard_header(request: Request) -> None:
    if request.headers.get(CSRF_HEADER) != "1":
        raise ApiError(403, "Forbidden", f"The {CSRF_HEADER} header is required.")


async def end_sessions(session: AsyncSession, user_id: uuid.UUID, keep: uuid.UUID | None = None) -> None:
    """Sign a user out everywhere (when disabled, rejected, or their password changes)."""
    statement = delete(AdminSession).where(AdminSession.user_id == user_id)
    if keep is not None:
        statement = statement.where(AdminSession.id != keep)
    await session.execute(statement)


def user_summary(user: User) -> dict[str, Any]:
    return {"id": str(user.id), "username": user.username, "displayName": user.display_name, "role": user.role,
            "status": user.status, "createdAt": user.created_at, "approvedAt": user.approved_at,
            "lastLoginAt": user.last_login_at}


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


def _address(request: Request) -> str:
    return request.client.host if request.client else "?"


# ----------------------------------------------------------------------------- routes


class RegisterBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    username: str = Field(min_length=1, max_length=100)
    displayName: str = Field(min_length=1, max_length=100)
    password: str = Field(min_length=1, max_length=MAX_PASSWORD)


class LoginBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    username: str = Field(min_length=1, max_length=100)
    password: str = Field(min_length=1, max_length=MAX_PASSWORD)
    remember: bool = False


class PasswordBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    currentPassword: str = Field(min_length=1, max_length=MAX_PASSWORD)
    newPassword: str = Field(min_length=1, max_length=MAX_PASSWORD)


@router.post("/api/v1/auth/register", dependencies=[Depends(require_dashboard_header)])
async def register(body: RegisterBody, request: Request) -> JSONResponse:
    """A coordinator asks for an account. It stays "pending" until the admin approves it."""
    state = request.app.state
    settings: AuthSettings = state.auth_settings
    if not settings.allow_registration:
        raise ApiError(403, "Registration is closed", "Ask the admin to create your account.")
    now = state.clock()
    address = _address(request)
    wait = state.register_limit.retry_after(("register", address), now)
    if wait is not None:
        response = _no_store({"error": "Too many attempts", "retryAfterSeconds": wait}, 429)
        response.headers["Retry-After"] = str(wait)
        return response
    state.register_limit.add(("register", address), now)

    username = normalise_username(body.username)
    display_name = body.displayName.strip()[:100]
    if not display_name:
        raise ApiError(400, "Invalid request", "Your name is required.")
    check_new_password(body.password, username)

    async with state.sessionmaker() as session, session.begin():
        if (await session.execute(select(User.id).where(User.username == username))).first():
            raise ApiError(409, "Conflict", "That username is taken.")
        session.add(User(id=uuid.uuid4(), username=username, display_name=display_name,
                         password_hash=hash_password(body.password), role="coordinator", status="pending",
                         created_at=now, updated_at=now))
    emit("auth.registered", username=username, client=address)
    return _no_store({"username": username, "status": "pending",
                      "message": "Your account was created and is waiting for the admin's approval."}, 201)


@router.post("/api/v1/auth/login", dependencies=[Depends(require_dashboard_header)])
async def login(body: LoginBody, request: Request) -> JSONResponse:
    state = request.app.state
    settings: AuthSettings = state.auth_settings
    now = state.clock()
    username = body.username.strip().lower()
    address = _address(request)
    throttle: LoginThrottle = state.login_throttle
    wait = throttle.retry_after(username, address, now)
    if wait is not None:
        emit("auth.login_throttled", level=logging.WARNING, username=username[:50], client=address)
        response = _no_store({"error": "Too many attempts", "retryAfterSeconds": wait}, 429)
        response.headers["Retry-After"] = str(wait)
        return response

    async with state.sessionmaker() as session, session.begin():
        user = (await session.execute(select(User).where(User.username == username))).scalar_one_or_none()
        # An unknown name costs the same as a wrong password, so timing does not reveal which names exist.
        valid = verify_password(body.password, user.password_hash if user else _decoy_hash())
        if user is None or not valid:
            throttle.failed(username, address, now)
            emit("auth.login_failed", level=logging.WARNING, username=username[:50], client=address)
            raise ApiError(401, "Invalid username or password")
        throttle.succeeded(username, address)
        # Only now, with the right password, does the answer say why an account cannot sign in.
        if user.status == "pending":
            raise ApiError(403, "Account awaiting approval", {"reason": "pending"})
        if user.status != "active":
            raise ApiError(403, "Account not active", {"reason": user.status})

        token = SESSION_TOKEN_PREFIX + secrets.token_urlsafe(32)
        lifetime = timedelta(days=settings.remember_days) if body.remember else timedelta(hours=settings.session_hours)
        expires = now + lifetime
        await session.execute(delete(AdminSession).where(AdminSession.expires_at <= now))
        session.add(AdminSession(id=uuid.uuid4(), token_hash=hash_secret(token), username=user.username,
                                 user_id=user.id, created_at=now, expires_at=expires))
        user.last_login_at = now
        answer = {"username": user.username, "displayName": user.display_name, "role": user.role, "expiresAt": expires,
                  "remembered": body.remember}
    emit("auth.login", username=username, role=answer["role"], client=address)

    response = _no_store(answer)
    response.set_cookie(cookie_name(request), token, max_age=int(lifetime.total_seconds()), path="/",
                        secure=state.settings.require_https, httponly=True, samesite="strict")
    return response


@router.post("/api/v1/auth/logout", dependencies=[Depends(require_dashboard_header)])
async def logout(request: Request) -> JSONResponse:
    token = request.cookies.get(cookie_name(request))
    if token:
        async with request.app.state.sessionmaker() as session, session.begin():
            await session.execute(delete(AdminSession).where(AdminSession.token_hash == hash_secret(token)))
    response = _no_store({"status": "logged out"})
    response.delete_cookie(cookie_name(request), path="/", secure=request.app.state.settings.require_https,
                           httponly=True, samesite="strict")
    return response


@router.get("/api/v1/auth/me")
async def me(request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    """Who is signed in, their role, and the groups they see (all of them for the admin)."""
    groups = None
    if not user.is_admin:
        async with request.app.state.sessionmaker() as session:
            groups = [group_view(g) for g in await assigned_groups(session, user.id)]
    return _no_store({"id": str(user.id), "username": user.username, "displayName": user.display_name,
                      "role": user.role, "allGroups": user.is_admin, "groups": groups, "expiresAt": user.expires_at})


@router.post("/api/v1/auth/password", dependencies=[Depends(require_dashboard_header)])
async def change_password(body: PasswordBody, request: Request, user: CurrentUser = Depends(current_user)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        stored = await session.get(User, user.id, with_for_update=True)
        if stored is None or not verify_password(body.currentPassword, stored.password_hash):
            raise ApiError(400, "Invalid request", "The current password is not right.")
        check_new_password(body.newPassword, stored.username)
        stored.password_hash = hash_password(body.newPassword)
        stored.updated_at = now
        await end_sessions(session, user.id, keep=user.session_id)      # other browsers are signed out
    emit("auth.password_changed", username=user.username)
    return _no_store({"status": "changed"})

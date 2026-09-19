"""The classes the admin's PC runs for other people.

A coordinator's class needs two sign-ins that are theirs, not the admin's: a Zoom account to open
the meeting, and an LMS account to press Run Session, fill the attendance in and put the recording
on it. This module is how one PC gets both for several coordinators at once, so two classes at the
same time each go up under the right name.

    GET    /api/v1/admin/delegations                     every coordinator, and whether we run theirs
    PUT    /api/v1/admin/delegations/{coordinator_id}    run theirs (or stop); their LMS and Zoom account
    GET    /api/v1/admin/users/{user_id}/lms-accounts    a coordinator's LMS sign-ins (never a password)
    GET    /api/v1/admin/users/{user_id}/zoom-accounts   a coordinator's Zoom accounts and their links
    POST   /api/v1/admin/users/{user_id}/lms-accounts/{account_id}/secret     the sign-in itself
    GET    /api/v1/admin/run-plan?from=&to=&coordinator=  the classes to run, one line each
    POST   /api/v1/admin/run-plan/import                 what the LMS listed for one coordinator
    PATCH  /api/v1/admin/run-plan/{plan_id}              its link, its Zoom account, what it opens with

Admin only; every write also needs X-Dashboard-Request: 1, and answers are never cached.

The timetable is not kept by hand: the app signs in to the LMS as the coordinator, reads their own
session list, and sends it to `import`. The LMS does not publish a Zoom link, so that (and the Zoom
account that opens it) is the one thing a person still fills in - once per group, since `import`
carries a group's last known link on to its next classes and PATCH can write one across the group.

Neither account is typed in twice. Each coordinator's app keeps its own Zoom accounts and LMS
sign-ins on the server against their dashboard account, so the admin picks from what that person
already has; a group's meeting link comes from their Zoom account for that group.

Reading a coordinator's LMS sign-in is the admin's to do (they already set these passwords), and
every read is written to admin_audit_log with who read whose. It is refused for a coordinator the
admin has not turned on, so a password is never handed out as a side effect of listing accounts.
"""

from __future__ import annotations

import re
import uuid
from datetime import date, datetime
from typing import Any

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, StrictBool
from sqlalchemy import Select, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from .admin import audit, groups_by_user
from .api import ApiError, _json
from .auth import CurrentUser, current_user, require_admin, require_dashboard_header
from .dashboard import parse_id
from .models import ClassPlan, LmsAccount, RunDelegation, User, ZoomAccount
from .observability import emit
from .user_data import _box, zoom_view

router = APIRouter(dependencies=[Depends(require_admin)])
writes = [Depends(require_dashboard_header)]

_GROUP = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$")
_TIME = re.compile(r"^(?:[01]\d|2[0-3]):[0-5]\d$")
MAX_IMPORT_ROWS = 500
"""One coordinator's timetable for a few weeks. More than this is a mistake, not a term."""


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


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


def _group(value: str) -> str:
    name = value.strip()
    if not _GROUP.match(name):
        raise ApiError(400, "Invalid request", f"'{value}' is not a group name.")
    return name


def _start_time(value: str | None) -> str | None:
    if value is None or not value.strip():
        return None
    start = value.strip()
    if not _TIME.match(start):
        raise ApiError(400, "Invalid request", "A start time is written HH:MM.")
    return start


async def _coordinator(session: AsyncSession, user_id: str) -> User:
    """The coordinator this is about. The admin account is not delegated to itself."""
    user = await session.get(User, parse_id(user_id))
    if user is None or user.role != "coordinator":
        raise ApiError(404, "Not found")
    return user


# ----------------------------------------------------------------------------- views


def _account_view(account: LmsAccount) -> dict[str, Any]:
    return {"id": str(account.id), "label": account.label, "email": account.email,
            "role": account.role, "active": account.active}


def _plan_view(plan: ClassPlan, fallback_zoom: str | None = None) -> dict[str, Any]:
    zoom = plan.zoom_account or fallback_zoom
    return {
        "id": str(plan.id),
        "coordinatorId": str(plan.coordinator_id),
        "group": plan.group_name,
        "date": plan.session_date.isoformat(),
        "startTime": plan.start_time,
        "title": plan.title,
        "meetingUrl": plan.meeting_url,
        "zoomAccount": zoom,
        "preferredEngine": plan.preferred_engine,
        "source": plan.source,
        "status": plan.status,
        "note": plan.note,
        "needsLink": plan.status == "planned" and not plan.meeting_url,
        "importedAt": plan.imported_at,
        "updatedAt": plan.updated_at,
    }


async def _zoom_accounts(session: AsyncSession, user_ids: list[uuid.UUID]) -> dict[uuid.UUID, list[ZoomAccount]]:
    if not user_ids:
        return {}
    rows = (await session.execute(
        select(ZoomAccount).where(ZoomAccount.user_id.in_(user_ids))
        .order_by(ZoomAccount.active.desc(), ZoomAccount.account_id)
    )).scalars().all()
    found: dict[uuid.UUID, list[ZoomAccount]] = {}
    for account in rows:
        found.setdefault(account.user_id, []).append(account)
    return found


def _chosen_zoom(accounts: list[ZoomAccount], chosen: uuid.UUID | None, named: str | None) -> ZoomAccount | None:
    """The account a delegation names - by reference, by the name it was given, or the active one."""
    for account in accounts:
        if chosen is not None and account.id == chosen:
            return account
    if named:
        for account in accounts:
            if account.account_id.lower() == named.lower():
                return account
    return next((a for a in accounts if a.active), None)


async def _lms_accounts(session: AsyncSession, user_ids: list[uuid.UUID]) -> dict[uuid.UUID, list[LmsAccount]]:
    if not user_ids:
        return {}
    rows = (await session.execute(
        select(LmsAccount).where(LmsAccount.user_id.in_(user_ids))
        .order_by(LmsAccount.active.desc(), LmsAccount.label)
    )).scalars().all()
    found: dict[uuid.UUID, list[LmsAccount]] = {}
    for account in rows:
        found.setdefault(account.user_id, []).append(account)
    return found


def _chosen_account(accounts: list[LmsAccount], chosen: uuid.UUID | None) -> LmsAccount | None:
    """The account a delegation names, or - with none named - the one that coordinator has in use."""
    if chosen is not None:
        for account in accounts:
            if account.id == chosen:
                return account
    return next((a for a in accounts if a.active), None)


# ----------------------------------------------------------------------------- delegations


class DelegationBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    enabled: StrictBool
    lmsAccountId: str | None = None
    """One of that coordinator's own Zoom accounts. The name it is known by follows from it."""
    zoomAccountId: str | None = None


@router.get("/api/v1/admin/delegations")
async def list_delegations(request: Request) -> JSONResponse:
    """Every coordinator with an account, their groups and LMS sign-ins, and what we run for them."""
    async with request.app.state.sessionmaker() as session:
        users = (await session.execute(
            select(User).where(User.role == "coordinator").order_by(User.display_name, User.username)
        )).scalars().all()
        ids = [user.id for user in users]
        delegations = {
            row.coordinator_id: row
            for row in (await session.execute(select(RunDelegation).where(RunDelegation.coordinator_id.in_(ids)))).scalars()
        } if ids else {}
        groups = await groups_by_user(session, ids)
        accounts = await _lms_accounts(session, ids)
        zooms = await _zoom_accounts(session, ids)
        counts = {
            (coordinator_id, status): total
            for coordinator_id, status, total in (await session.execute(
                select(ClassPlan.coordinator_id, ClassPlan.status, func.count())
                .where(ClassPlan.coordinator_id.in_(ids)).group_by(ClassPlan.coordinator_id, ClassPlan.status)
            )).all()
        } if ids else {}
        missing = {
            coordinator_id: total
            for coordinator_id, total in (await session.execute(
                select(ClassPlan.coordinator_id, func.count())
                .where(ClassPlan.coordinator_id.in_(ids), ClassPlan.status == "planned", ClassPlan.meeting_url.is_(None))
                .group_by(ClassPlan.coordinator_id)
            )).all()
        } if ids else {}

    items = []
    for user in users:
        delegation = delegations.get(user.id)
        mine = accounts.get(user.id, [])
        chosen = _chosen_account(mine, delegation.lms_account_id if delegation else None)
        their_zooms = zooms.get(user.id, [])
        zoom = _chosen_zoom(their_zooms, delegation.zoom_account_id if delegation else None,
                            delegation.zoom_account if delegation else None)
        items.append({
            "coordinatorId": str(user.id),
            "username": user.username,
            "displayName": user.display_name,
            "status": user.status,
            "enabled": bool(delegation and delegation.enabled),
            "groups": groups.get(user.id, []),
            "lmsAccount": _account_view(chosen) if chosen else None,
            "lmsAccounts": [_account_view(a) for a in mine],
            "zoomAccountId": str(zoom.id) if zoom else None,
            "zoomAccount": zoom.account_id if zoom else (delegation.zoom_account if delegation else None),
            "zoomAccounts": [zoom_view(a) for a in their_zooms],
            "classes": {
                "planned": counts.get((user.id, "planned"), 0),
                "done": counts.get((user.id, "done"), 0),
                "skipped": counts.get((user.id, "skipped"), 0),
                "needsLink": missing.get(user.id, 0),
            },
            "updatedAt": delegation.updated_at if delegation else None,
        })
    return _no_store({"delegations": items})


@router.put("/api/v1/admin/delegations/{coordinator_id}", dependencies=writes)
async def set_delegation(
    coordinator_id: str, body: DelegationBody, request: Request, admin: CurrentUser = Depends(current_user)
) -> JSONResponse:
    """Run this coordinator's classes (or stop), with the LMS and Zoom account they go up under."""
    now = request.app.state.clock()
    chosen_id = parse_id(body.lmsAccountId) if body.lmsAccountId else None
    zoom_id = parse_id(body.zoomAccountId) if body.zoomAccountId else None
    async with request.app.state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, coordinator_id)
        if body.enabled and user.status != "active":
            raise ApiError(409, "Conflict", "That coordinator's account is not active.")
        if chosen_id is not None:
            account = await session.get(LmsAccount, chosen_id)
            if account is None or account.user_id != user.id:
                raise ApiError(404, "Not found", "That is not one of this coordinator's LMS accounts.")
        zoom = None
        if zoom_id is not None:
            zoom = await session.get(ZoomAccount, zoom_id)
            if zoom is None or zoom.user_id != user.id:
                raise ApiError(404, "Not found", "That is not one of this coordinator's Zoom accounts.")
        delegation = await session.get(RunDelegation, user.id, with_for_update=True)
        if delegation is None:
            delegation = RunDelegation(coordinator_id=user.id, created_by=admin.id, created_at=now)
            session.add(delegation)
        delegation.enabled = body.enabled
        delegation.lms_account_id = chosen_id
        delegation.zoom_account_id = zoom_id
        # The name the running PC knows the account by is kept beside the reference, so a class
        # still says what it wanted if the account is removed later.
        delegation.zoom_account = zoom.account_id if zoom else None
        delegation.updated_at = now
        audit(session, admin, "delegation.set",
              {"coordinator": user.username, "enabled": body.enabled, "zoomAccount": delegation.zoom_account}, now)
        view = {"coordinatorId": str(user.id), "enabled": delegation.enabled,
                "lmsAccountId": str(chosen_id) if chosen_id else None,
                "zoomAccountId": str(zoom_id) if zoom_id else None,
                "zoomAccount": delegation.zoom_account, "updatedAt": now}
    return _no_store(view)


# ----------------------------------------------------------------------------- a coordinator's LMS sign-in


@router.get("/api/v1/admin/users/{user_id}/lms-accounts")
async def coordinator_accounts(user_id: str, request: Request) -> JSONResponse:
    """What LMS sign-ins that coordinator keeps here. Never a password."""
    async with request.app.state.sessionmaker() as session:
        user = await _coordinator(session, user_id)
        accounts = (await _lms_accounts(session, [user.id])).get(user.id, [])
        delegation = await session.get(RunDelegation, user.id)
    chosen = _chosen_account(accounts, delegation.lms_account_id if delegation else None)
    return _no_store({
        "coordinatorId": str(user.id),
        "accounts": [_account_view(a) for a in accounts],
        "inUse": _account_view(chosen) if chosen else None,
        "canKeepPasswords": getattr(request.app.state, "secret_box", None) is not None,
    })


@router.get("/api/v1/admin/users/{user_id}/zoom-accounts")
async def coordinator_zoom_accounts(user_id: str, request: Request) -> JSONResponse:
    """The Zoom accounts that coordinator's own app keeps here, each with the link its classes open."""
    async with request.app.state.sessionmaker() as session:
        user = await _coordinator(session, user_id)
        accounts = (await _zoom_accounts(session, [user.id])).get(user.id, [])
        delegation = await session.get(RunDelegation, user.id)
    chosen = _chosen_zoom(accounts, delegation.zoom_account_id if delegation else None,
                          delegation.zoom_account if delegation else None)
    return _no_store({
        "coordinatorId": str(user.id),
        "accounts": [zoom_view(a) for a in accounts],
        "inUse": zoom_view(chosen) if chosen else None,
    })


@router.post("/api/v1/admin/users/{user_id}/lms-accounts/{account_id}/secret", dependencies=writes)
async def coordinator_secret(
    user_id: str, account_id: str, request: Request, admin: CurrentUser = Depends(current_user)
) -> JSONResponse:
    """That coordinator's LMS sign-in, for the admin's app to run their classes under their name.

    Only for a coordinator the admin has turned on, and written down every time. Never logged.
    """
    box = _box(request)
    now = request.app.state.clock()
    async with request.app.state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, user_id)
        delegation = await session.get(RunDelegation, user.id)
        if delegation is None or not delegation.enabled:
            raise ApiError(403, "Forbidden", "Turn this coordinator on before running their classes.")
        account = await session.get(LmsAccount, parse_id(account_id))
        if account is None or account.user_id != user.id:
            raise ApiError(404, "Not found")
        try:
            password = box.open(account.password_encrypted, f"{account.user_id}:{account.id}")
        except Exception as exc:  # noqa: BLE001 - a changed key or a damaged row; the value never goes to the log
            raise ApiError(409, "Cannot decrypt", "That coordinator has to save their LMS password again.") from exc
        audit(session, admin, "lms_secret.read",
              {"coordinator": user.username, "account": str(account.id), "email": account.email}, now)
        body = {"id": str(account.id), "coordinatorId": str(user.id), "email": account.email,
                "role": account.role, "label": account.label, "password": password}
    emit("lms_account.secret_read_for_run", username=admin.username, coordinator=user.username, account=str(account.id))
    return _no_store(body)


# ----------------------------------------------------------------------------- the plan


class ImportedClass(BaseModel):
    model_config = ConfigDict(extra="forbid")
    group: str = Field(min_length=1, max_length=100)
    date: date
    startTime: str | None = None
    title: str | None = Field(default=None, max_length=200)
    meetingUrl: str | None = None
    zoomAccount: str | None = Field(default=None, max_length=100)
    preferredEngine: str | None = Field(default=None, pattern="^(desktop|web)$")


class ImportBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    coordinatorId: str
    classes: list[ImportedClass] = Field(max_length=MAX_IMPORT_ROWS)
    source: str = Field(default="lms", pattern="^(lms|manual)$")


class PlanBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    meetingUrl: str | None = None
    zoomAccount: str | None = Field(default=None, max_length=100)
    preferredEngine: str | None = Field(default=None, pattern="^(desktop|web|auto)$")
    status: str | None = Field(default=None, pattern="^(planned|skipped|opened|done|failed)$")
    note: str | None = Field(default=None, max_length=300)
    applyToGroup: StrictBool = False


def _window(from_: str | None, to: str | None) -> tuple[date | None, date | None]:
    try:
        start = date.fromisoformat(from_) if from_ else None
        end = date.fromisoformat(to) if to else None
    except ValueError as exc:
        raise ApiError(400, "Invalid request", "A date is written yyyy-mm-dd.") from exc
    if start and end and end < start:
        raise ApiError(400, "Invalid request", "'to' is before 'from'.")
    return start, end


def _narrow(query: Select, start: date | None, end: date | None, coordinators: list[uuid.UUID]) -> Select:
    if start:
        query = query.where(ClassPlan.session_date >= start)
    if end:
        query = query.where(ClassPlan.session_date <= end)
    if coordinators:
        query = query.where(ClassPlan.coordinator_id.in_(coordinators))
    return query


@router.get("/api/v1/admin/run-plan")
async def run_plan(
    request: Request,
    from_: str | None = Query(default=None, alias="from"),
    to: str | None = Query(default=None),
    coordinator: list[str] = Query(default=[]),
    status: str | None = Query(default=None),
    only_delegated: bool = Query(default=True, alias="onlyDelegated"),
) -> JSONResponse:
    """The classes to run. `coordinator` may be given more than once to narrow it to a few people."""
    start, end = _window(from_, to)
    wanted = [parse_id(value) for value in coordinator]
    async with request.app.state.sessionmaker() as session:
        delegations = {row.coordinator_id: row for row in (await session.execute(select(RunDelegation))).scalars()}
        running = [key for key, row in delegations.items() if row.enabled]
        if only_delegated:
            wanted = [c for c in wanted if c in running] if wanted else running
            if not wanted:
                return _no_store({"classes": [], "coordinators": []})
        query = _narrow(select(ClassPlan), start, end, wanted)
        if status:
            query = query.where(ClassPlan.status == status)
        plans = (await session.execute(
            query.order_by(ClassPlan.session_date, ClassPlan.start_time.nulls_last(), ClassPlan.group_name)
        )).scalars().all()
        ids = sorted({plan.coordinator_id for plan in plans} | set(wanted))
        users = {
            user.id: user for user in (await session.execute(select(User).where(User.id.in_(ids)))).scalars()
        } if ids else {}
        accounts = await _lms_accounts(session, ids)
        zooms = await _zoom_accounts(session, ids)

    people = []
    for user_id in ids:
        user = users.get(user_id)
        if user is None:
            continue
        delegation = delegations.get(user_id)
        chosen = _chosen_account(accounts.get(user_id, []), delegation.lms_account_id if delegation else None)
        zoom = _chosen_zoom(zooms.get(user_id, []), delegation.zoom_account_id if delegation else None,
                            delegation.zoom_account if delegation else None)
        people.append({
            "coordinatorId": str(user_id),
            "displayName": user.display_name,
            "username": user.username,
            "enabled": bool(delegation and delegation.enabled),
            "zoomAccount": zoom.account_id if zoom else (delegation.zoom_account if delegation else None),
            "zoomAccounts": [zoom_view(a) for a in zooms.get(user_id, [])],
            "lmsAccount": _account_view(chosen) if chosen else None,
        })
    classes = [_plan_view(plan, (delegations.get(plan.coordinator_id).zoom_account
                                 if delegations.get(plan.coordinator_id) else None)) for plan in plans]
    return _no_store({"classes": classes, "coordinators": people})


@router.post("/api/v1/admin/run-plan/import", dependencies=writes)
async def import_plan(body: ImportBody, request: Request, admin: CurrentUser = Depends(current_user)) -> JSONResponse:
    """What that coordinator's LMS listed. Re-sending the same class updates it; nothing is duplicated.

    A class the LMS lists again keeps whatever a person put on it - its link, its Zoom account, and
    a decision to skip it - because the LMS knows the timetable and this side knows how it opens.
    """
    now = request.app.state.clock()
    async with request.app.state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, body.coordinatorId)
        # That coordinator's own Zoom accounts. One is usually kept per group, carrying the link its
        # classes open, so a class the LMS lists needs nothing typed in: the link and the account
        # that opens it are already theirs. A group with no account of its own falls back to the one
        # the delegation names.
        delegation = await session.get(RunDelegation, user.id)
        their_zooms = (await _zoom_accounts(session, [user.id])).get(user.id, [])
        default_zoom = _chosen_zoom(their_zooms, delegation.zoom_account_id if delegation else None,
                                    delegation.zoom_account if delegation else None)
        zoom_for: dict[str, ZoomAccount] = {}
        for account in their_zooms:
            for key in {(account.group_name or "").lower(), account.account_id.lower()} - {""}:
                zoom_for.setdefault(key, account)
        existing = {
            (plan.group_name.lower(), plan.session_date, plan.start_time or ""): plan
            for plan in (await session.execute(
                select(ClassPlan).where(ClassPlan.coordinator_id == user.id).with_for_update()
            )).scalars()
        }
        # A group's link is the same week after week, so the last one known carries on to new classes.
        known_link: dict[str, str] = {}
        known_zoom: dict[str, str] = {}
        for plan in sorted(existing.values(), key=lambda p: (p.session_date, p.start_time or "")):
            if plan.meeting_url:
                known_link[plan.group_name.lower()] = plan.meeting_url
            if plan.zoom_account:
                known_zoom[plan.group_name.lower()] = plan.zoom_account

        added = updated = 0
        for item in body.classes:
            group = _group(item.group)
            start = _start_time(item.startTime)
            url = _meeting_url(item.meetingUrl)
            key = (group.lower(), item.date, start or "")
            plan = existing.get(key)
            theirs = zoom_for.get(group.lower()) or default_zoom
            if plan is None:
                plan = ClassPlan(
                    id=uuid.uuid4(), coordinator_id=user.id, group_name=group, session_date=item.date,
                    start_time=start, source=body.source, status="planned", created_at=now,
                    meeting_url=url or known_link.get(group.lower()) or (theirs.default_meeting_url if theirs else None),
                    zoom_account=(item.zoomAccount or "").strip() or known_zoom.get(group.lower())
                                 or (theirs.account_id if theirs else None),
                    preferred_engine=item.preferredEngine or (theirs.preferred_engine if theirs else None),
                )
                session.add(plan)
                existing[key] = plan
                added += 1
            else:
                if url:
                    plan.meeting_url = url
                elif not plan.meeting_url and theirs is not None:
                    plan.meeting_url = theirs.default_meeting_url
                if item.zoomAccount:
                    plan.zoom_account = item.zoomAccount.strip() or None
                elif not plan.zoom_account and theirs is not None:
                    plan.zoom_account = theirs.account_id
                if item.preferredEngine:
                    plan.preferred_engine = item.preferredEngine
                updated += 1
            plan.title = (item.title or plan.title or "").strip()[:200] or None
            plan.imported_at = now
            plan.updated_at = now
            if plan.meeting_url:
                known_link[group.lower()] = plan.meeting_url
            if plan.zoom_account:
                known_zoom[group.lower()] = plan.zoom_account
        audit(session, admin, "run_plan.imported",
              {"coordinator": user.username, "added": added, "updated": updated, "source": body.source}, now)
    emit("run_plan.imported", username=admin.username, coordinator=user.username, added=added, updated=updated)
    return _no_store({"coordinatorId": str(user.id), "added": added, "updated": updated,
                      "total": added + updated}, 201 if added else 200)


@router.patch("/api/v1/admin/run-plan/{plan_id}", dependencies=writes)
async def update_plan(
    plan_id: str, body: PlanBody, request: Request, admin: CurrentUser = Depends(current_user)
) -> JSONResponse:
    """Its meeting link, the Zoom account that opens it, what it opens with, or skipping it.

    `applyToGroup` writes the link and the Zoom account onto every class of that group that has not
    happened yet, which is how one group's link is filled in once instead of on every line.
    """
    now = request.app.state.clock()
    fields = body.model_dump(exclude_unset=True)
    url = _meeting_url(body.meetingUrl) if "meetingUrl" in fields else None
    async with request.app.state.sessionmaker() as session, session.begin():
        plan = await session.get(ClassPlan, parse_id(plan_id), with_for_update=True)
        if plan is None:
            raise ApiError(404, "Not found")
        if "meetingUrl" in fields:
            plan.meeting_url = url
        if "zoomAccount" in fields:
            plan.zoom_account = (body.zoomAccount or "").strip() or None
        if "preferredEngine" in fields:
            plan.preferred_engine = None if body.preferredEngine in (None, "auto") else body.preferredEngine
        if body.status:
            plan.status = body.status
        if "note" in fields:
            plan.note = (body.note or "").strip()[:300] or None
        plan.updated_at = now

        spread = 0
        if body.applyToGroup:
            others = (await session.execute(
                select(ClassPlan).where(
                    ClassPlan.coordinator_id == plan.coordinator_id,
                    func.lower(ClassPlan.group_name) == plan.group_name.lower(),
                    ClassPlan.id != plan.id,
                    ClassPlan.status == "planned",
                ).with_for_update()
            )).scalars().all()
            for other in others:
                if "meetingUrl" in fields:
                    other.meeting_url = plan.meeting_url
                if "zoomAccount" in fields:
                    other.zoom_account = plan.zoom_account
                if "preferredEngine" in fields:
                    other.preferred_engine = plan.preferred_engine
                other.updated_at = now
                spread += 1
        audit(session, admin, "run_plan.updated",
              {"plan": str(plan.id), "group": plan.group_name, "date": plan.session_date.isoformat(),
               "status": plan.status, "alsoInGroup": spread}, now)
        view = _plan_view(plan)
    view["alsoInGroup"] = spread
    return _no_store(view)

"""The admin's own tools: coordinator accounts, and which groups each coordinator sees.

    GET   /api/v1/admin/users?status=&role=     accounts, pending first, with their groups
    POST  /api/v1/admin/users                   create a coordinator (active at once)
    GET   /api/v1/admin/users/{id}              one account
    PATCH /api/v1/admin/users/{id}              display name; enable / disable a coordinator
    POST  /api/v1/admin/users/{id}/approve      a pending (or rejected) registration -> active
    POST  /api/v1/admin/users/{id}/reject       a pending registration -> rejected
    POST  /api/v1/admin/users/{id}/password     set a new password for a coordinator
    PUT   /api/v1/admin/users/{id}/groups       the coordinator's groups, as a whole set
    GET   /api/v1/admin/groups                  every group, its recordings and its coordinators
    POST  /api/v1/admin/groups                  register a group
    PATCH /api/v1/admin/groups/{id}             display name; archive / restore

Admin only; writes also need X-Dashboard-Request: 1. The admin account itself cannot be disabled,
rejected, given groups or have its password reset here (it changes its own password through
/api/v1/auth/password), and no route can create a second admin. Disabling, rejecting or resetting
a password signs that person out everywhere. Every change is written to admin_audit_log.
"""

from __future__ import annotations

import re
import uuid
from datetime import datetime
from typing import Any

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, StrictBool
from sqlalchemy import case, delete, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from .api import ApiError, _json
from .auth import (
    MAX_PASSWORD,
    CurrentUser,
    check_new_password,
    end_sessions,
    hash_password,
    normalise_username,
    require_admin,
    require_dashboard_header,
    user_summary,
)
from .dashboard import group_items, parse_id
from .models import AdminAuditLog, ClassPlan, Group, LmsAccount, User, UserGroup, ZoomAccount
from .observability import emit

router = APIRouter(dependencies=[Depends(require_admin)])
writes = [Depends(require_dashboard_header)]

_GROUP = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$")


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


def audit(session: AsyncSession, admin: CurrentUser, action: str, details: dict[str, Any], now: datetime) -> None:
    session.add(AdminAuditLog(username=admin.username, user_id=admin.id, action=action, details=details, created_at=now))
    emit("admin.audit", username=admin.username, action=action, **details)


async def groups_by_user(session: AsyncSession, user_ids: list[uuid.UUID]) -> dict[uuid.UUID, list[dict[str, Any]]]:
    if not user_ids:
        return {}
    rows = (
        await session.execute(
            select(UserGroup.user_id, Group).join(Group, Group.id == UserGroup.group_id)
            .where(UserGroup.user_id.in_(user_ids)).order_by(Group.name)
        )
    ).all()
    found: dict[uuid.UUID, list[dict[str, Any]]] = {}
    for user_id, group in rows:
        found.setdefault(user_id, []).append({"id": str(group.id), "name": group.name,
                                              "displayName": group.display_name, "archived": group.archived_at is not None})
    return found


async def _user_view(session: AsyncSession, user: User) -> dict[str, Any]:
    return {**user_summary(user), "groups": (await groups_by_user(session, [user.id])).get(user.id, [])}


async def _coordinator(session: AsyncSession, user_id: str) -> User:
    """The account, locked; the admin account is refused (it is not managed from here)."""
    user = await session.get(User, parse_id(user_id), with_for_update=True)
    if user is None:
        raise ApiError(404, "Not found")
    if user.role == "admin":
        raise ApiError(409, "Conflict", "The admin account is not managed here.")
    return user


# ----------------------------------------------------------------------------- accounts


@router.get("/api/v1/admin/users")
async def list_users(
    request: Request,
    status: str | None = Query(default=None, pattern=r"^(pending|active|rejected|disabled)$"),
    role: str | None = Query(default=None, pattern=r"^(admin|coordinator)$"),
) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        statement = select(User)
        if status:
            statement = statement.where(User.status == status)
        if role:
            statement = statement.where(User.role == role)
        # Waiting first, because they are what the admin came to do; then the admins, then the
        # working coordinators. An account that is disabled or rejected is not part of the day's
        # work, so it goes to the bottom instead of sitting between two live ones.
        order = case(
            (User.status == "pending", 0),
            (User.status.in_(("disabled", "rejected")), 4),
            (User.role == "admin", 1),
            else_=2,
        )
        users = (await session.execute(statement.order_by(order, User.username))).scalars().all()
        assignments = await groups_by_user(session, [u.id for u in users])
        counts = dict((await session.execute(select(User.status, func.count()).group_by(User.status))).all())
    return _no_store({
        "users": [{**user_summary(u), "groups": assignments.get(u.id, [])} for u in users],
        "count": len(users),
        "counts": {s: counts.get(s, 0) for s in ("pending", "active", "rejected", "disabled")},
    })


class CreateUserBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    username: str = Field(min_length=1, max_length=100)
    displayName: str = Field(min_length=1, max_length=100)
    password: str = Field(min_length=1, max_length=MAX_PASSWORD)
    groupIds: list[uuid.UUID] = Field(default_factory=list, max_length=500)
    # An account is what it is born as. There is no endpoint that changes a role afterwards, so a
    # coordinator cannot become an admin by any route - only a new account can be one.
    role: str = Field(default="coordinator", pattern=r"^(admin|coordinator)$")


@router.post("/api/v1/admin/users", dependencies=writes)
async def create_user(body: CreateUserBody, request: Request, admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    """An account, active at once (no approval step needed).

    With role "admin" the new account is an admin from birth, with the same powers as the admin
    making it, including making further admins. A coordinator is never promoted into one: the role
    is written here and nothing else writes it again."""
    username = normalise_username(body.username)
    display_name = body.displayName.strip()
    if not display_name:
        raise ApiError(400, "Invalid request", "The display name is required.")
    check_new_password(body.password, username)
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        if (await session.execute(select(User.id).where(User.username == username))).first():
            raise ApiError(409, "Conflict", "That username is taken.")
        user = User(id=uuid.uuid4(), username=username, display_name=display_name[:100],
                    password_hash=hash_password(body.password), role=body.role, status="active",
                    approved_at=now, approved_by=admin.id, created_at=now, updated_at=now)
        session.add(user)
        await session.flush()
        # An admin sees every group already, so naming some for one would only be misleading.
        names = [] if body.role == "admin" else await _set_groups(session, user, body.groupIds, admin, now)
        audit(session, admin, "user.create",
              {"targetUserId": str(user.id), "targetUsername": username, "role": body.role, "groups": names}, now)
        view = await _user_view(session, user)
    return _no_store(view, 201)


@router.get("/api/v1/admin/users/{user_id}")
async def get_user(user_id: str, request: Request) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        user = await session.get(User, parse_id(user_id))
        if user is None:
            raise ApiError(404, "Not found")
        return _no_store(await _user_view(session, user))


class UpdateUserBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    displayName: str | None = Field(default=None, min_length=1, max_length=100)
    status: str | None = Field(default=None, pattern=r"^(active|disabled)$")


@router.patch("/api/v1/admin/users/{user_id}", dependencies=writes)
async def update_user(user_id: str, body: UpdateUserBody, request: Request,
                      admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        user = await session.get(User, parse_id(user_id), with_for_update=True)
        if user is None:
            raise ApiError(404, "Not found")
        changes: dict[str, Any] = {}
        if body.displayName is not None and body.displayName.strip() != user.display_name:
            user.display_name = body.displayName.strip()[:100]
            changes["displayName"] = user.display_name
        if body.status is not None and body.status != user.status:
            if user.id == admin.id:
                raise ApiError(409, "Conflict", "You cannot disable your own account.")
            if user.role == "admin" and body.status == "disabled":
                # Disabling the last one would leave nobody who can approve, create or re-enable an
                # account, and no endpoint can undo that.
                others = (await session.execute(
                    select(func.count()).select_from(User).where(
                        User.role == "admin", User.status == "active", User.id != user.id))).scalar_one()
                if others == 0:
                    raise ApiError(409, "Conflict",
                                   "This is the last active admin. Make another admin first.")
            if user.status not in ("active", "disabled"):
                raise ApiError(409, "Conflict", f"A {user.status} account is approved or rejected first.")
            user.status = body.status
            changes["status"] = body.status
            if body.status == "disabled":
                await end_sessions(session, user.id)
        if changes:
            user.updated_at = now
            action = {"disabled": "user.disable", "active": "user.enable"}.get(changes.get("status", ""), "user.update")
            audit(session, admin, action, {"targetUserId": str(user.id), "targetUsername": user.username, **changes}, now)
        view = await _user_view(session, user)
    return _no_store(view)


@router.post("/api/v1/admin/users/{user_id}/approve", dependencies=writes)
async def approve(user_id: str, request: Request, admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, user_id)
        if user.status not in ("pending", "rejected"):
            raise ApiError(409, "Conflict", f"Only a pending or rejected account can be approved; this one is {user.status}.")
        user.status, user.approved_at, user.approved_by, user.updated_at = "active", now, admin.id, now
        audit(session, admin, "user.approve", {"targetUserId": str(user.id), "targetUsername": user.username}, now)
        view = await _user_view(session, user)
    return _no_store(view)


@router.post("/api/v1/admin/users/{user_id}/reject", dependencies=writes)
async def reject(user_id: str, request: Request, admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, user_id)
        if user.status != "pending":
            raise ApiError(409, "Conflict", f"Only a pending account can be rejected; this one is {user.status}. Disable it instead.")
        user.status, user.updated_at = "rejected", now
        await end_sessions(session, user.id)
        audit(session, admin, "user.reject", {"targetUserId": str(user.id), "targetUsername": user.username}, now)
        view = await _user_view(session, user)
    return _no_store(view)


class PasswordResetBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    password: str = Field(min_length=1, max_length=MAX_PASSWORD)


@router.delete("/api/v1/admin/users/{user_id}", dependencies=writes)
async def delete_user(user_id: str, request: Request,
                      admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    """Remove a coordinator for good, with everything that was theirs.

    Deleting is not disabling: their LMS and Zoom accounts, their saved classes, their timetable,
    their group assignments, their delegation and their sessions all go with them, because every
    one of those tables is theirs alone and cascades. What stays is what is not theirs to take
    away - the attendance already recorded, the recordings already attached, and the audit log,
    which keeps their username so the record of what was done remains readable.

    An admin account is never deleted here: being an admin is not something this endpoint should
    be able to take away quietly, and there is no route back.
    """
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, user_id)
        username = user.username
        removed = {
            "groups": (await session.execute(
                select(func.count()).select_from(UserGroup).where(UserGroup.user_id == user.id))).scalar_one(),
            "lmsAccounts": (await session.execute(
                select(func.count()).select_from(LmsAccount).where(LmsAccount.user_id == user.id))).scalar_one(),
            "zoomAccounts": (await session.execute(
                select(func.count()).select_from(ZoomAccount).where(ZoomAccount.user_id == user.id))).scalar_one(),
            "classPlans": (await session.execute(
                select(func.count()).select_from(ClassPlan).where(ClassPlan.coordinator_id == user.id))).scalar_one(),
        }
        # Written before the row goes, so the audit row is part of the same transaction: either
        # the account is gone and the record of it exists, or neither happened.
        audit(session, admin, "user.delete",
              {"targetUserId": str(user.id), "targetUsername": username, "removed": removed}, now)
        await end_sessions(session, user.id)
        await session.delete(user)
    emit("admin.user_deleted", username=username, by=admin.username)
    return _no_store({"deleted": True, "username": username, "removed": removed})


@router.post("/api/v1/admin/users/{user_id}/password", dependencies=writes)
async def reset_password(user_id: str, body: PasswordResetBody, request: Request,
                         admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    """A new password chosen by the admin, e.g. for a coordinator who forgot theirs. It is not
    stored or logged anywhere but as a hash; the coordinator is signed out everywhere."""
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, user_id)
        check_new_password(body.password, user.username)
        user.password_hash, user.updated_at = hash_password(body.password), now
        await end_sessions(session, user.id)
        audit(session, admin, "user.password_reset", {"targetUserId": str(user.id), "targetUsername": user.username}, now)
    return _no_store({"status": "changed"})


class GroupsBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    groupIds: list[uuid.UUID] = Field(max_length=500)


async def _set_groups(session: AsyncSession, user: User, group_ids: list[uuid.UUID], admin: CurrentUser,
                      now: datetime) -> list[str]:
    """Replace the coordinator's groups with exactly these (existing, not archived). Returns their names."""
    wanted = set(group_ids)
    groups = (await session.execute(select(Group).where(Group.id.in_(wanted)))).scalars().all() if wanted else []
    if len(groups) != len(wanted):
        raise ApiError(400, "Invalid request", "Some of those groups do not exist.")
    if any(g.archived_at is not None for g in groups):
        raise ApiError(400, "Invalid request", "Archived groups cannot be assigned.")
    await session.execute(delete(UserGroup).where(UserGroup.user_id == user.id))
    for group in groups:
        session.add(UserGroup(user_id=user.id, group_id=group.id, assigned_by=admin.id, assigned_at=now))
    return sorted(g.name for g in groups)


@router.put("/api/v1/admin/users/{user_id}/groups", dependencies=writes)
async def set_groups(user_id: str, body: GroupsBody, request: Request, admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        user = await _coordinator(session, user_id)
        names = await _set_groups(session, user, body.groupIds, admin, now)
        user.updated_at = now
        audit(session, admin, "user.groups", {"targetUserId": str(user.id), "targetUsername": user.username, "groups": names}, now)
        await session.flush()
        view = await _user_view(session, user)
    return _no_store(view)


# ----------------------------------------------------------------------------- groups


@router.get("/api/v1/admin/groups")
async def list_groups(request: Request) -> JSONResponse:
    """Every group (archived ones too) with its recordings and the coordinators who see it."""
    async with request.app.state.sessionmaker() as session:
        items = await group_items(session, None, include_archived=True)
        rows = (
            await session.execute(
                select(UserGroup.group_id, User).join(User, User.id == UserGroup.user_id).order_by(User.username)
            )
        ).all()
    coordinators: dict[str, list[dict[str, Any]]] = {}
    for group_id, user in rows:
        coordinators.setdefault(str(group_id), []).append(
            {"id": str(user.id), "username": user.username, "displayName": user.display_name, "status": user.status})
    return _no_store({"groups": [{**g, "coordinators": coordinators.get(g["id"], [])} for g in items], "count": len(items)})


class CreateGroupBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str = Field(min_length=1, max_length=100)
    displayName: str | None = Field(default=None, max_length=100)


@router.post("/api/v1/admin/groups", dependencies=writes)
async def create_group(body: CreateGroupBody, request: Request, admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    """Register a group ahead of its first recording (n8n registers new ones by itself, too)."""
    name = body.name.strip()
    if not _GROUP.match(name):
        raise ApiError(400, "Invalid request", "The group name may hold letters, digits, spaces and _ - . (at most 100).")
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        if (await session.execute(select(Group.id).where(Group.name == name))).first():
            raise ApiError(409, "Conflict", "That group already exists.")
        group = Group(id=uuid.uuid4(), name=name, display_name=(body.displayName or "").strip() or None, created_at=now)
        session.add(group)
        audit(session, admin, "group.create", {"group": name}, now)
    return _no_store({"id": str(group.id), "name": name, "displayName": group.display_name, "archived": False}, 201)


class UpdateGroupBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    displayName: str | None = Field(default=None, max_length=100)
    archived: StrictBool | None = None


@router.patch("/api/v1/admin/groups/{group_id}", dependencies=writes)
async def update_group(group_id: str, body: UpdateGroupBody, request: Request,
                       admin: CurrentUser = Depends(require_admin)) -> JSONResponse:
    """The name never changes (the recordings sheet keeps sending it); the label and archiving do."""
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        group = await session.get(Group, parse_id(group_id), with_for_update=True)
        if group is None:
            raise ApiError(404, "Not found")
        changes: dict[str, Any] = {}
        if "displayName" in body.model_fields_set:
            label = (body.displayName or "").strip() or None
            if label != group.display_name:
                group.display_name = label
                changes["displayName"] = label
        if body.archived is not None and body.archived != (group.archived_at is not None):
            group.archived_at = now if body.archived else None
            changes["archived"] = body.archived
        if changes:
            audit(session, admin, "group.update", {"group": group.name, **changes}, now)
        view = {"id": str(group.id), "name": group.name, "displayName": group.display_name,
                "archived": group.archived_at is not None}
    return _no_store(view)

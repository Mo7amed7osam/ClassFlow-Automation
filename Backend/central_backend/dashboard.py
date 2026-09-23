"""The dashboard's data (read-only) and its web page.

For signed-in users (see auth.py), filtered by what each may see (see access.py): the admin sees
everything; a coordinator only their assigned groups, and never the agents. The n8n API key does
not open these, and a dashboard session does not open the n8n API. Everything here only reads; the
write operations are in dashboard_operations.py, the account management in admin.py.

    GET /api/v1/dashboard/overview                  counts for the header cards
    GET /api/v1/dashboard/recordings                recordings + link status + latest job; filters, sort, pages
    GET /api/v1/dashboard/recordings/{id}           one recording, its jobs and its audit trail
    GET /api/v1/dashboard/groups                    per group: recordings, last session date, LMS progress
    GET /api/v1/dashboard/agents                    devices, online status, heartbeat, assigned jobs (admin)
    GET /api/v1/dashboard/agents/{deviceId}/jobs    one device's recent jobs (admin)
    GET /api/v1/dashboard/live                       cloud meetings and their captured attendance
    GET /dashboard/...                              the built React app (Dashboard/dist), when present
"""

from __future__ import annotations

import uuid
from datetime import timedelta
from pathlib import Path
from typing import Any

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import FileResponse, JSONResponse, RedirectResponse, Response
from sqlalchemy import and_, case, func, select
from sqlalchemy.ext.asyncio import AsyncSession

from .access import Viewer, current_viewer
from .auth import require_admin
from .api import ApiError, _json
from .devices import device_view, is_online
from .models import ACTIVE_JOB_STATUSES, AdminAuditLog, AttendanceSession, AttendanceSnapshot, Device, Group, Job, Recording
from .recordings import recording_view

# No blanket dependency: each endpoint states who may use it (a Viewer, or the admin).
router = APIRouter()

LMS_LABELS = {"pending": "pending", "attached": "on LMS", "failed": "LMS failed"}
LINK_LABELS = {"drive": "Drive", "zoom": "Zoom only", "missing": "Missing"}
SORTS = {
    "updated": (Recording.updated_at.desc(), Recording.id),
    "created": (Recording.created_at.desc(), Recording.id),
    "session": (Recording.session_date.desc(), func.coalesce(Recording.start_time, "").desc(), Recording.group_name),
}


def _no_store(body: Any) -> JSONResponse:
    return JSONResponse(_json(body), headers={"Cache-Control": "no-store"})


def link_status(recording: Recording) -> dict[str, str]:
    """Which link the recording has (a Drive link wins), combined with where it stands on the LMS.
    Whether the link actually opens is not checked."""
    link = "drive" if recording.drive_link else "zoom" if recording.zoom_link else "missing"
    lms = LMS_LABELS.get(recording.lms_status, recording.lms_status)
    return {"link": link, "lms": recording.lms_status, "label": f"{LINK_LABELS[link]} · {lms}"}


def _link_filter(link: str):  # noqa: ANN202
    if link == "drive":
        return Recording.drive_link.is_not(None)
    if link == "zoom":
        return and_(Recording.drive_link.is_(None), Recording.zoom_link.is_not(None))
    return and_(Recording.drive_link.is_(None), Recording.zoom_link.is_(None))


def job_summary(job: Job) -> dict[str, Any]:
    """What an admin needs to see about a job - never its recording link."""
    payload = job.payload or {}
    return {
        "jobId": str(job.id),
        "type": job.type,
        "status": job.status,
        "group": payload.get("group"),
        "date": payload.get("date"),
        "attempts": job.attempts,
        "createdAt": job.created_at,
        "assignedAt": job.assigned_at,
        "startedAt": job.started_at,
        "finishedAt": job.finished_at,
        "errorCode": (job.error or {}).get("code"),
        "alreadyExists": (job.result or {}).get("alreadyExists"),
        "recordingId": str(job.recording_id) if job.recording_id else None,
        "dryRun": bool(payload.get("dryRun")),
        "replaceExisting": bool(payload.get("replaceExisting")),
    }


def recording_item(recording: Recording, last_job: Job | None = None) -> dict[str, Any]:
    """A recording as the dashboard shows it: the stored fields, its link status, and its latest job."""
    return {**recording_view(recording), "linkStatus": link_status(recording),
            "lastJob": job_summary(last_job) if last_job else None}


async def latest_jobs(session: AsyncSession, recording_ids: list[uuid.UUID]) -> dict[uuid.UUID, Job]:
    """The newest job of each of these recordings (only jobs the dashboard created carry one)."""
    if not recording_ids:
        return {}
    rows = (
        await session.execute(
            select(Job).where(Job.recording_id.in_(recording_ids))
            .order_by(Job.recording_id, Job.created_at.desc()).distinct(Job.recording_id)
        )
    ).scalars().all()
    return {job.recording_id: job for job in rows if job.recording_id is not None}


def parse_id(value: str) -> uuid.UUID:
    try:
        return uuid.UUID(value)
    except ValueError as exc:
        raise ApiError(404, "Not found") from exc


# ----------------------------------------------------------------------------- data


async def recording_counts(session: AsyncSession, viewer: Viewer) -> dict[str, int]:
    row = (
        await session.execute(
            select(
                func.count(),
                func.count().filter(Recording.lms_status == "pending"),
                func.count().filter(Recording.lms_status == "attached"),
                func.count().filter(and_(Recording.drive_link.is_(None), Recording.zoom_link.is_(None))),
            ).select_from(Recording).where(viewer.recording_filter())
        )
    ).one()
    return {"total": row[0], "pending": row[1], "onLms": row[2], "missingLink": row[3]}


@router.get("/api/v1/dashboard/overview")
async def overview(request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """Counts for the header cards. A coordinator gets their groups' recordings only; agents and
    jobs span every group, so only the admin gets those."""
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session:
        recordings_count = await recording_counts(session, viewer)
        if not viewer.is_admin:
            return _no_store({"recordings": recordings_count, "agents": None, "jobs": None,
                              "groups": len(viewer.groups or ()), "serverTime": now})
        connected = state.registry.connected_device_ids()
        day_ago = now - timedelta(hours=24)
        devices = (await session.execute(select(Device).where(Device.revoked_at.is_(None)))).scalars().all()
        jobs = dict(
            (await session.execute(
                select(Job.status, func.count()).where(Job.status.in_(("queued", "assigned", "running"))).group_by(Job.status)
            )).all()
        )
        finished = dict(
            (await session.execute(
                select(Job.status, func.count())
                .where(Job.status.in_(("succeeded", "failed")), Job.finished_at >= day_ago)
                .group_by(Job.status)
            )).all()
        )
        group_count = (await session.execute(select(func.count()).select_from(Group).where(Group.archived_at.is_(None)))).scalar_one()
    online = [d for d in devices if is_online(d, connected, now, state.settings)]
    return _no_store({
        "agents": {"total": len(devices), "online": len(online),
                   "busy": sum(1 for d in online if d.agent_state == "busy")},
        "recordings": recordings_count,
        "jobs": {"queued": jobs.get("queued", 0), "assigned": jobs.get("assigned", 0), "running": jobs.get("running", 0),
                 "succeededLast24h": finished.get("succeeded", 0), "failedLast24h": finished.get("failed", 0)},
        "groups": group_count,
        "serverTime": now,
    })


@router.get("/api/v1/dashboard/live")
async def live_sessions(request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """The meetings that a worker is currently opening or holding.

    A cloud class is one long ``class.run`` job. The worker admits while that job is running and
    sends Zoom participant snapshots to attendance; this endpoint presents those two sources
    together. It deliberately has no Stop button: a separate queued end job cannot safely
    interrupt the worker that owns a live browser session.
    """
    state = request.app.state
    active = ("queued", "assigned", "running")
    group_column = Job.payload["group"].astext
    conditions = [Job.type == "class.run", Job.status.in_(active)]
    if viewer.groups is not None:
        conditions.append(group_column.in_(sorted(viewer.groups)))

    async with state.sessionmaker() as session:
        jobs = (await session.execute(
            select(Job).where(*conditions).order_by(Job.started_at.desc().nullslast(), Job.created_at.desc()).limit(100)
        )).scalars().all()
        device_ids = {job.device_id for job in jobs if job.device_id is not None}
        devices = {
            row.id: row for row in (await session.execute(select(Device).where(Device.id.in_(device_ids)))).scalars().all()
        } if device_ids else {}

        items = []
        for job in jobs:
            payload = job.payload or {}
            group, day, start = payload.get("group"), payload.get("date"), payload.get("startTime")
            attendance = None
            if group and day:
                try:
                    from datetime import date
                    session_day = date.fromisoformat(str(day))
                    attendance = (await session.execute(
                        select(AttendanceSession).where(
                            AttendanceSession.group_name == group,
                            AttendanceSession.session_date == session_day,
                            func.coalesce(AttendanceSession.start_time, "") == (start or ""),
                        ).order_by(AttendanceSession.updated_at.desc()).limit(1)
                    )).scalar_one_or_none()
                except ValueError:
                    attendance = None
            snapshot_count, observed = 0, 0
            attendance_id = None
            if attendance is not None:
                snapshots = (await session.execute(
                    select(AttendanceSnapshot).where(AttendanceSnapshot.session_id == attendance.id)
                    .order_by(AttendanceSnapshot.captured_at.desc()).limit(1)
                )).scalars().all()
                snapshot_count = (await session.execute(
                    select(func.count()).select_from(AttendanceSnapshot).where(AttendanceSnapshot.session_id == attendance.id)
                )).scalar_one()
                observed = len(snapshots[0].names or []) if snapshots else 0
                attendance_id = str(attendance.id)
            device = devices.get(job.device_id)
            items.append({
                **job_summary(job),
                "meetingUrl": payload.get("meetingUrl"),
                "durationMinutes": payload.get("durationMinutes"),
                "worker": device.name if device else None,
                "attendanceSessionId": attendance_id,
                "attendanceStatus": attendance.status if attendance else None,
                "snapshots": snapshot_count,
                "observed": observed,
            })
    return _no_store({"items": items, "count": len(items), "serverTime": state.clock()})


@router.get("/api/v1/dashboard/recordings")
async def recordings(
    request: Request,
    viewer: Viewer = Depends(current_viewer),
    group: str | None = Query(default=None, max_length=100),
    date_filter: str | None = Query(default=None, alias="date", max_length=10),
    status: str | None = Query(default=None, pattern=r"^[a-z][a-z_]{0,31}$"),
    link: str | None = Query(default=None, pattern=r"^(drive|zoom|missing)$"),
    sort: str = Query(default="updated", pattern=r"^(updated|created|session)$"),
    page: int = Query(default=1, ge=1, le=100000),
    page_size: int = Query(default=25, alias="pageSize", ge=1, le=100),
) -> JSONResponse:
    # The viewer's scope first: a coordinator asking for another group's recordings gets none.
    conditions = [viewer.recording_filter()]
    if group and group.strip():
        conditions.append(Recording.group_name == group.strip())
    if date_filter and date_filter.strip():
        from datetime import date

        try:
            conditions.append(Recording.session_date == date.fromisoformat(date_filter.strip()))
        except ValueError as exc:
            raise ApiError(400, "Invalid request", "'date' must be yyyy-MM-dd.") from exc
    if status:
        conditions.append(Recording.lms_status == status)
    if link:
        conditions.append(_link_filter(link))

    async with request.app.state.sessionmaker() as session:
        total = (await session.execute(select(func.count()).select_from(Recording).where(*conditions))).scalar_one()
        rows = (
            await session.execute(
                select(Recording).where(*conditions).order_by(*SORTS[sort])
                .limit(page_size).offset((page - 1) * page_size)
            )
        ).scalars().all()
        last_jobs = await latest_jobs(session, [r.id for r in rows])
    items = [recording_item(r, last_jobs.get(r.id)) for r in rows]
    return _no_store({"items": items, "total": total, "page": page, "pageSize": page_size, "sort": sort})


@router.get("/api/v1/dashboard/recordings/{recording_id}")
async def recording_details(recording_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """One recording with the jobs made from it and what was done to it, newest first. Outside the
    viewer's groups it is answered as if it did not exist."""
    key = parse_id(recording_id)
    async with request.app.state.sessionmaker() as session:
        recording = await session.get(Recording, key)
        if recording is None or not viewer.can_see(recording.group_name):
            raise ApiError(404, "Not found")
        jobs = (
            await session.execute(select(Job).where(Job.recording_id == key).order_by(Job.created_at.desc()).limit(20))
        ).scalars().all()
        audit = (
            await session.execute(
                select(AdminAuditLog).where(AdminAuditLog.recording_id == key).order_by(AdminAuditLog.id.desc()).limit(20)
            )
        ).scalars().all()
    return _no_store({
        "recording": recording_item(recording, jobs[0] if jobs else None),
        "jobs": [job_summary(j) for j in jobs],
        "audit": [{"action": a.action, "username": a.username, "details": a.details, "createdAt": a.created_at} for a in audit],
    })


async def group_items(session: AsyncSession, names: frozenset[str] | None, include_archived: bool) -> list[dict[str, Any]]:
    """Groups (all, or only `names`) with their recording counts. Built from the groups table, so a
    group that has no recording yet still appears."""
    counts = (
        select(
            Recording.group_name.label("name"),
            func.count().label("recordings"),
            func.max(Recording.session_date).label("last_session"),
            func.max(Recording.updated_at).label("last_updated"),
            func.count().filter(Recording.lms_status == "pending").label("pending"),
            func.count().filter(Recording.lms_status == "attached").label("on_lms"),
            func.count().filter(and_(Recording.drive_link.is_(None), Recording.zoom_link.is_(None))).label("missing"),
        ).group_by(Recording.group_name).subquery()
    )
    statement = select(Group, counts).outerjoin(counts, counts.c.name == Group.name).order_by(Group.name)
    if names is not None:
        statement = statement.where(Group.name.in_(sorted(names)))
    if not include_archived:
        statement = statement.where(Group.archived_at.is_(None))
    rows = (await session.execute(statement)).all()
    return [
        {"id": str(g.id), "group": g.name, "displayName": g.display_name, "archived": g.archived_at is not None,
         "recordings": row.recordings or 0, "lastSessionDate": row.last_session.isoformat() if row.last_session else None,
         "lastUpdatedAt": row.last_updated, "pending": row.pending or 0, "onLms": row.on_lms or 0,
         "missingLink": row.missing or 0}
        for row in rows
        for g in [row[0]]
    ]


@router.get("/api/v1/dashboard/groups")
async def groups(request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """The groups the viewer sees (all of them for the admin, archived ones included)."""
    async with request.app.state.sessionmaker() as session:
        items = await group_items(session, viewer.groups, include_archived=viewer.is_admin)
    return _no_store({"groups": items, "count": len(items)})


@router.get("/api/v1/dashboard/ai", dependencies=[Depends(require_admin)])
async def ai(request: Request) -> JSONResponse:
    """Whether attendance matching can ask an AI, and which model answers.

    The key itself is the server's, set in its environment and never sent anywhere: this says only
    that there is one, so the admin can see why "Match with AI" is or is not offered without reading
    a log or a container's settings.
    """
    matcher = getattr(request.app.state, "attendance_ai", None)
    return _no_store({"available": matcher is not None,
                      "model": getattr(matcher, "model", None)})


@router.get("/api/v1/dashboard/agents", dependencies=[Depends(require_admin)])
async def agents(request: Request) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    connected = state.registry.connected_device_ids()
    async with state.sessionmaker() as session:
        devices = (await session.execute(select(Device).order_by(Device.name, Device.id))).scalars().all()
        active = (
            await session.execute(
                select(Job).where(Job.status.in_(ACTIVE_JOB_STATUSES), Job.device_id.is_not(None)).order_by(Job.assigned_at)
            )
        ).scalars().all()
        counts = (
            await session.execute(
                select(Job.device_id, Job.status, func.count())
                .where(Job.status.in_(("succeeded", "failed")), Job.finished_at >= now - timedelta(hours=24))
                .group_by(Job.device_id, Job.status)
            )
        ).all()
    by_device: dict[uuid.UUID, list[dict[str, Any]]] = {}
    for job in active:
        by_device.setdefault(job.device_id, []).append(job_summary(job))
    last_day: dict[uuid.UUID, dict[str, int]] = {}
    for device_id, job_status, n in counts:
        last_day.setdefault(device_id, {"succeeded": 0, "failed": 0})[job_status] = n

    items = []
    for device in devices:
        view = device_view(device, connected, now, state.settings)
        view.pop("installationId", None)
        items.append({
            **view,
            "lastAssignedAt": device.last_assigned_at,
            "activeJobs": by_device.get(device.id, []),
            "jobsLast24h": last_day.get(device.id, {"succeeded": 0, "failed": 0}),
        })
    return _no_store({"agents": items, "count": len(items), "online": sum(1 for a in items if a["status"] == "online")})


@router.get("/api/v1/dashboard/agents/{device_id}/jobs", dependencies=[Depends(require_admin)])
async def agent_jobs(device_id: str, request: Request, limit: int = Query(default=20, ge=1, le=100)) -> JSONResponse:
    try:
        key = uuid.UUID(device_id)
    except ValueError as exc:
        raise ApiError(404, "Not found") from exc
    async with request.app.state.sessionmaker() as session:
        if await session.get(Device, key) is None:
            raise ApiError(404, "Not found")
        jobs = (
            await session.execute(
                select(Job).where(Job.device_id == key)
                .order_by(case((Job.status.in_(ACTIVE_JOB_STATUSES), 0), else_=1), Job.created_at.desc())
                .limit(limit)
            )
        ).scalars().all()
    return _no_store({"deviceId": str(key), "jobs": [job_summary(j) for j in jobs], "count": len(jobs)})


# ----------------------------------------------------------------------------- the web page

SECURITY_HEADERS = {
    "Content-Security-Policy": (
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
        "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'"
    ),
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Referrer-Policy": "no-referrer",
}


def site_router(dist: Path) -> APIRouter:
    """Serves the built dashboard under /dashboard/. Unknown page paths get index.html, so the
    app's own routes (/dashboard/agents, ...) survive a reload; a missing asset is a real 404."""
    root = dist.resolve()
    site = APIRouter()

    def serve(file: Path, immutable: bool) -> Response:
        headers = dict(SECURITY_HEADERS)
        headers["Cache-Control"] = "public, max-age=31536000, immutable" if immutable else "no-cache"
        return FileResponse(file, headers=headers)

    @site.get("/dashboard", include_in_schema=False)
    async def dashboard_root() -> Response:
        return RedirectResponse("/dashboard/", status_code=308)

    @site.get("/dashboard/{path:path}", include_in_schema=False)
    async def dashboard_file(path: str) -> Response:
        if path:
            candidate = (root / path).resolve()
            if not candidate.is_relative_to(root):
                return JSONResponse({"error": "Not found"}, status_code=404)
            if candidate.is_file():
                return serve(candidate, immutable=path.startswith("assets/"))
            if path.startswith("assets/") or "." in path.rsplit("/", 1)[-1]:
                return JSONResponse({"error": "Not found"}, status_code=404)
        return serve(root / "index.html", immutable=False)

    return site

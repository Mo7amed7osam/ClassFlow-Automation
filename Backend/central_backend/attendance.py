"""Attendance, centrally: rosters, sessions, snapshots from the Windows agent, matching, review.

Taking attendance (machines):
    POST /api/v1/attendance/snapshots      one read of a meeting's Zoom list. Authenticated with the
                                           agent's device token (Bearer zaad_...) or the n8n key.
                                           Creates the session the first time; re-sending the same
                                           clientSnapshotId changes nothing.

Rosters and review (the dashboard; every route scoped to the viewer's groups, writes need the
X-Dashboard-Request header; see access.py):
    GET/POST   /api/v1/dashboard/students                 list / add a student
    PATCH      /api/v1/dashboard/students/{id}            edit, move to another (own) group, deactivate
    POST       /api/v1/dashboard/students/import          a pasted or CSV roster (dryRun previews)
    GET        /api/v1/dashboard/students/{id}/aliases    the Zoom names remembered for a student
    DELETE     /api/v1/dashboard/aliases/{id}             forget one
    GET/POST   /api/v1/dashboard/attendance/sessions      list / create a session by hand
    GET        /api/v1/dashboard/attendance/sessions/{id} records, participants, candidates, summary
    POST       .../{id}/participants                      add names by hand (a pasted participant list)
    POST       .../{id}/match                             match again (AI too, when configured)
    PUT        .../{id}/records/{studentId}               manual correction (remembered for next time)
    POST       .../{id}/participants/{pid}/ignore         a host or staff name is not a student
    POST       .../{id}/finalize, .../{id}/reopen
    GET        .../{id}/export.csv

Matching and timing are in attendance_matching.py and attendance_names.py; the snapshots are kept
as sent, and participants and records are worked out from them again whenever something changes.
"""

from __future__ import annotations

import csv
import io
import logging
import re
import uuid
from datetime import date, datetime
from typing import Any, Literal

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel, ConfigDict, Field, StrictBool, field_validator
from sqlalchemy import ColumnElement, func, or_, select, true
from sqlalchemy.exc import IntegrityError
from sqlalchemy.ext.asyncio import AsyncSession

from .access import Viewer, current_viewer
from .api import ApiError, _json
from .attendance_matching import (
    AliasMemory,
    ManualDecision,
    Observed,
    RosterStudent,
    SnapshotView,
    match,
    merge_intervals,
    presence,
)
from .attendance_names import clean_display_name, normalize
from .attendance_names import score as score_names
from .auth import require_dashboard_header
from .dashboard import parse_id
from .devices import authenticate_device
from .groups import ensure_group
from .models import (
    AdminAuditLog,
    AttendanceParticipant,
    AttendanceRecord,
    AttendanceSession,
    AttendanceSnapshot,
    Device,
    Recording,
    Student,
    StudentAlias,
)
from .observability import emit
from .roster_import import parse_roster
from .security import bearer_token, matches_any

router = APIRouter()
dashboard_router = APIRouter(dependencies=[Depends(current_viewer)])
writes = [Depends(require_dashboard_header)]

GROUP = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$")
TIME = re.compile(r"^([01]\d|2[0-3]):[0-5]\d$")
MAX_PARTICIPANTS = 1000
FINALIZED = "This session is finalized; reopen it to change it."


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


def group_filter(viewer: Viewer, column: Any) -> ColumnElement[bool]:
    return true() if viewer.groups is None else column.in_(sorted(viewer.groups))


def audit(session: AsyncSession, viewer: Viewer, action: str, details: dict[str, Any], now: datetime) -> None:
    session.add(AdminAuditLog(username=viewer.user.username, user_id=viewer.user.id, action=action, details=details, created_at=now))
    emit("attendance.audit", username=viewer.user.username, action=action, **{k: v for k, v in details.items() if k != "names"})


def _clean_url(url: str | None) -> str | None:
    """A meeting link without its query (where Zoom puts the passcode)."""
    if not url:
        return None
    return url.split("?", 1)[0].split("#", 1)[0][:500]


# =========================================================================== recompute


async def recompute(session: AsyncSession, att: AttendanceSession, now: datetime) -> None:
    """Participants and records, from the snapshots, the roster, name memory and manual decisions."""
    snapshots = (await session.execute(
        select(AttendanceSnapshot).where(AttendanceSnapshot.session_id == att.id).order_by(AttendanceSnapshot.captured_at, AttendanceSnapshot.id)
    )).scalars().all()
    staff: set[str] = set()
    views = []
    for snap in snapshots:
        names = []
        for raw in snap.names:
            cleaned = clean_display_name(raw)
            if cleaned.name:
                names.append(cleaned.name)
                if cleaned.is_staff:
                    staff.add(normalize(cleaned.name))
        views.append(SnapshotView(snap.captured_at, names, snap.is_complete))
    seen = presence(views, session_end=att.ended_at)

    existing = {p.name_key: p for p in (await session.execute(
        select(AttendanceParticipant).where(AttendanceParticipant.session_id == att.id))).scalars().all()}
    for key, p in seen.items():
        row = existing.get(key)
        intervals = [[a.isoformat(), b.isoformat()] for a, b in p.intervals]
        if row is None:
            row = AttendanceParticipant(id=uuid.uuid4(), session_id=att.id, name=p.name, name_key=key, first_seen_at=p.first_seen,
                                        last_seen_at=p.last_seen, sightings=p.sightings, present_seconds=p.seconds,
                                        intervals=intervals, ignored=key in staff, created_at=now)
            session.add(row)
            existing[key] = row
        else:
            row.first_seen_at, row.last_seen_at, row.sightings = p.first_seen, p.last_seen, p.sightings
            row.present_seconds, row.intervals = p.seconds, intervals
    await session.flush()

    students = (await session.execute(
        select(Student).where(Student.group_name == att.group_name, Student.active.is_(True))
        .order_by(Student.order_index.asc().nulls_last(), Student.full_name)
    )).scalars().all()
    roster = [RosterStudent(str(s.id), s.full_name, s.order_index if s.order_index is not None else 100000 + i, tuple(s.aliases or []))
              for i, s in enumerate(students)]
    memory = [AliasMemory(str(a.student_id), a.alias_key, a.status == "accepted") for a in (await session.execute(
        select(StudentAlias).where(StudentAlias.student_id.in_([s.id for s in students])))).scalars().all()] if students else []
    records = {str(r.student_id): r for r in (await session.execute(
        select(AttendanceRecord).where(AttendanceRecord.session_id == att.id))).scalars().all()}
    manual = [ManualDecision(sid, str(r.participant_id) if r.participant_id else None, r.status) for sid, r in records.items() if r.manual]
    participants = list(existing.values())
    observed = [Observed(str(p.id), p.name, p.name_key, p.ignored) for p in participants]
    result = match(roster, observed, memory, manual)

    by_id = {str(p.id): p for p in participants}
    active_ids = {str(s.id) for s in students}
    for sid, a in result.assignments.items():
        record = records.get(sid)
        if record is None:
            record = AttendanceRecord(id=uuid.uuid4(), session_id=att.id, student_id=uuid.UUID(sid), status="absent", created_at=now)
            session.add(record)
        spans = []
        for pid in [a.participant_id, *a.extra_participant_ids]:
            if pid and pid in by_id:
                spans.extend((datetime.fromisoformat(x), datetime.fromisoformat(y)) for x, y in by_id[pid].intervals)
        spans = merge_intervals(spans)
        record.participant_id = uuid.UUID(a.participant_id) if a.participant_id else None
        record.extra_participant_ids = list(a.extra_participant_ids)
        record.status = a.status
        record.confidence = a.confidence
        record.match_source = a.source
        record.reason = (a.reason or "")[:300] or None
        record.manual = a.manual
        record.join_time = spans[0][0] if spans else None
        record.leave_time = spans[-1][1] if spans else None
        record.duration_seconds = int(sum((y - x).total_seconds() for x, y in spans)) if spans else None
        record.updated_at = now
    for sid, record in records.items():
        if sid not in active_ids and not record.manual:
            await session.delete(record)
    att.matched_at = now
    att.updated_at = now


# =========================================================================== snapshots from machines


async def ingest_principal(request: Request) -> Device | None:
    """The agent (its device token) or the n8n client. None means the n8n client."""
    token = bearer_token(request.headers.get("authorization"))
    if token and token.startswith("zaad_"):
        async with request.app.state.sessionmaker() as session:
            device = await authenticate_device(session, token)
        if device is None:
            raise ApiError(401, "Unauthorized")
        return device
    presented = request.headers.get("x-api-key") or token
    if not matches_any(presented, request.app.state.settings.client_api_key_hashes):
        emit("attendance.refused", level=logging.WARNING, client=request.client.host if request.client else None)
        raise ApiError(401, "Unauthorized")
    return None


class SessionIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    ref: str | None = Field(default=None, min_length=1, max_length=200)
    group: str = Field(min_length=1, max_length=100)
    date: date
    startTime: str | None = Field(default=None, max_length=5)
    title: str | None = Field(default=None, max_length=200)
    meetingUrl: str | None = Field(default=None, max_length=2048)

    @field_validator("group")
    @classmethod
    def _group(cls, v: str) -> str:
        v = v.strip()
        if not GROUP.match(v):
            raise ValueError("letters, digits, spaces and _ - . only")
        return v

    @field_validator("startTime")
    @classmethod
    def _time(cls, v: str | None) -> str | None:
        if v is None or v == "":
            return None
        if not TIME.match(v):
            raise ValueError("HH:mm, 24-hour")
        return v


class ParticipantIn(BaseModel):
    model_config = ConfigDict(extra="ignore")
    name: str = Field(min_length=1, max_length=300)


class SnapshotIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    clientSnapshotId: str | None = Field(default=None, min_length=1, max_length=200)
    session: SessionIn
    capturedAt: datetime
    source: Literal["desktop", "web", "extension", "manual", "api"] = "api"
    trigger: str = Field(default="scheduled", min_length=1, max_length=24)
    isComplete: StrictBool = False
    ended: StrictBool = False
    participants: list[str | ParticipantIn] = Field(max_length=MAX_PARTICIPANTS)

    @field_validator("capturedAt")
    @classmethod
    def _aware(cls, v: datetime) -> datetime:
        if v.tzinfo is None:
            raise ValueError("needs a time zone, e.g. 2026-09-14T18:05:00Z")
        return v

    def names(self) -> list[str]:
        out = []
        for p in self.participants:
            name = (p if isinstance(p, str) else p.name).strip()
            if name:
                out.append(name[:300])
        return out


async def _link_recording(session: AsyncSession, att: AttendanceSession) -> None:
    rows = (await session.execute(select(Recording.id, Recording.start_time).where(
        Recording.group_name == att.group_name, Recording.session_date == att.session_date))).all()
    exact = [r.id for r in rows if att.start_time and r.start_time == att.start_time]
    att.recording_id = exact[0] if exact else (rows[0].id if len(rows) == 1 else None)


async def find_or_create_session(session: AsyncSession, body: SessionIn, device: Device | None, source: str,
                                 started: datetime, now: datetime) -> AttendanceSession:
    if body.ref:
        att = (await session.execute(select(AttendanceSession).where(AttendanceSession.external_ref == body.ref)
                                     .with_for_update())).scalar_one_or_none()
        if att is not None:
            if att.group_name != body.group:
                raise ApiError(409, "Conflict", "This session reference belongs to another group.")
            return att
    else:
        att = (await session.execute(select(AttendanceSession).where(
            AttendanceSession.group_name == body.group, AttendanceSession.session_date == body.date,
            func.coalesce(AttendanceSession.start_time, "") == (body.startTime or ""),
            AttendanceSession.external_ref.is_(None)).order_by(AttendanceSession.created_at.desc()).limit(1)
            .with_for_update())).scalar_one_or_none()
        if att is not None:
            return att
    await ensure_group(session, body.group, now)
    att = AttendanceSession(id=uuid.uuid4(), group_name=body.group, session_date=body.date, start_time=body.startTime,
                            title=(body.title or None), source=source, external_ref=body.ref, meeting_url=_clean_url(body.meetingUrl),
                            device_id=device.id if device else None, status="open", started_at=started, created_at=now, updated_at=now)
    session.add(att)
    await _link_recording(session, att)
    await session.flush()
    emit("attendance.session_created", sessionId=str(att.id), group=att.group_name, date=att.session_date.isoformat(), source=source)
    return att


def summary_of(records: list[AttendanceRecord], unmatched: int) -> dict[str, int]:
    count = lambda s: sum(1 for r in records if r.status == s)  # noqa: E731
    return {"present": count("present"), "needsReview": count("needs_review"), "absent": count("absent"),
            "students": len(records), "unmatched": unmatched}


@router.post("/api/v1/attendance/snapshots")
async def post_snapshot(body: SnapshotIn, request: Request) -> JSONResponse:
    device = await ingest_principal(request)
    try:
        return await _store_snapshot(body, request, device)
    except IntegrityError:
        # another read of the same new meeting created its session (or the same snapshot) meanwhile
        return await _store_snapshot(body, request, device)


async def _store_snapshot(body: SnapshotIn, request: Request, device: Device | None) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    names = body.names()
    async with state.sessionmaker() as session, session.begin():
        if body.clientSnapshotId:
            existing = (await session.execute(select(AttendanceSnapshot).where(
                AttendanceSnapshot.client_snapshot_id == body.clientSnapshotId))).scalar_one_or_none()
            if existing is not None:
                return _no_store({"sessionId": str(existing.session_id), "duplicate": True})
        source = body.source if body.source != "api" else ("desktop" if device else "api")
        att = await find_or_create_session(session, body.session, device, source, body.capturedAt, now)
        session.add(AttendanceSnapshot(session_id=att.id, client_snapshot_id=body.clientSnapshotId, captured_at=body.capturedAt,
                                       source=source, trigger=body.trigger, is_complete=body.isComplete, names=names, created_at=now))
        if att.started_at is None or body.capturedAt < att.started_at:
            att.started_at = body.capturedAt
        if body.ended:
            att.ended_at = max(att.ended_at or body.capturedAt, body.capturedAt)
            if att.status == "open":
                att.status = "closed"
        await session.flush()
        if att.status != "finalized":
            await recompute(session, att, now)
        records = (await session.execute(select(AttendanceRecord).where(AttendanceRecord.session_id == att.id))).scalars().all()
        assigned = {r.participant_id for r in records if r.participant_id} | {uuid.UUID(x) for r in records for x in r.extra_participant_ids}
        unmatched = (await session.execute(select(func.count()).select_from(AttendanceParticipant).where(
            AttendanceParticipant.session_id == att.id, AttendanceParticipant.ignored.is_(False),
            AttendanceParticipant.id.not_in(assigned) if assigned else true()))).scalar_one()
        answer = {"sessionId": str(att.id), "duplicate": False, "participants": len(names), "status": att.status,
                  "summary": summary_of(list(records), unmatched)}
    emit("attendance.snapshot", sessionId=answer["sessionId"], names=len(names), source=source, trigger=body.trigger,
         device=str(device.id) if device else None)
    return _no_store(answer, 201)


# =========================================================================== students (dashboard)


def student_view(s: Student) -> dict[str, Any]:
    return {"id": str(s.id), "group": s.group_name, "fullName": s.full_name, "email": s.email, "externalId": s.external_id,
            "order": s.order_index, "aliases": list(s.aliases or []), "active": s.active, "createdAt": s.created_at,
            "updatedAt": s.updated_at}


async def _visible_group(session: AsyncSession, viewer: Viewer, name: str, now: datetime) -> str:
    """A group the viewer may put students in: one of theirs, or (the admin) any, registered if new."""
    name = name.strip()
    if not GROUP.match(name):
        raise ApiError(400, "Invalid request", "The group name may hold letters, digits, spaces and _ - . (at most 100).")
    if not viewer.can_see(name):
        raise ApiError(403, "Forbidden", "You can only manage the students of your own groups.")
    await ensure_group(session, name, now)
    return name


async def _visible_student(session: AsyncSession, viewer: Viewer, student_id: str, lock: bool = False) -> Student:
    statement = select(Student).where(Student.id == parse_id(student_id))
    student = (await session.execute(statement.with_for_update() if lock else statement)).scalar_one_or_none()
    if student is None or not viewer.can_see(student.group_name):
        raise ApiError(404, "Not found")
    return student


@dashboard_router.get("/api/v1/dashboard/students")
async def list_students(request: Request, viewer: Viewer = Depends(current_viewer),
                        group: str | None = Query(default=None, max_length=100), q: str | None = Query(default=None, max_length=100),
                        include_inactive: bool = Query(default=False, alias="includeInactive")) -> JSONResponse:
    conditions = [group_filter(viewer, Student.group_name)]
    if group:
        conditions.append(Student.group_name == group.strip())
    if not include_inactive:
        conditions.append(Student.active.is_(True))
    if q and q.strip():
        like = f"%{q.strip()}%"
        conditions.append(or_(Student.full_name.ilike(like), Student.email.ilike(like), Student.external_id.ilike(like)))
    async with request.app.state.sessionmaker() as session:
        rows = (await session.execute(select(Student).where(*conditions).order_by(
            Student.group_name, Student.order_index.asc().nulls_last(), Student.full_name).limit(5000))).scalars().all()
    return _no_store({"students": [student_view(s) for s in rows], "count": len(rows)})


class StudentIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    group: str = Field(min_length=1, max_length=100)
    fullName: str = Field(min_length=1, max_length=300)
    email: str | None = Field(default=None, max_length=320)
    externalId: str | None = Field(default=None, max_length=128)
    order: int | None = Field(default=None, ge=1, le=99999)
    aliases: list[str] = Field(default_factory=list, max_length=50)


class StudentPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")
    group: str | None = Field(default=None, min_length=1, max_length=100)
    fullName: str | None = Field(default=None, min_length=1, max_length=300)
    email: str | None = Field(default=None, max_length=320)
    externalId: str | None = Field(default=None, max_length=128)
    order: int | None = Field(default=None, ge=1, le=99999)
    aliases: list[str] | None = Field(default=None, max_length=50)
    active: StrictBool | None = None


_EMAIL = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")


def _email(value: str | None) -> str | None:
    value = (value or "").strip().lower()
    if not value:
        return None
    if not _EMAIL.match(value):
        raise ApiError(400, "Invalid request", "The e-mail address is not valid.")
    return value


def _aliases(values: list[str]) -> list[str]:
    out: list[str] = []
    for v in values:
        v = re.sub(r"\s+", " ", v or "").strip()[:300]
        if v and normalize(v) not in {normalize(x) for x in out}:
            out.append(v)
    return out


async def _clash(session: AsyncSession, group: str, email: str | None, external_id: str | None, not_id: uuid.UUID | None = None) -> None:
    for column, value, what in ((func.lower(Student.email), email, "e-mail"), (Student.external_id, external_id, "id")):
        if value:
            statement = select(Student.id).where(Student.group_name == group, column == value)
            if not_id:
                statement = statement.where(Student.id != not_id)
            if (await session.execute(statement)).first():
                raise ApiError(409, "Conflict", f"Another student in {group} already has this {what}.")


@dashboard_router.post("/api/v1/dashboard/students", dependencies=writes)
async def create_student(body: StudentIn, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        group = await _visible_group(session, viewer, body.group, now)
        email, ext = _email(body.email), (body.externalId or "").strip() or None
        name = re.sub(r"\s+", " ", body.fullName).strip()
        if not normalize(name):
            raise ApiError(400, "Invalid request", "The name needs letters.")
        await _clash(session, group, email, ext)
        student = Student(id=uuid.uuid4(), group_name=group, full_name=name, email=email, external_id=ext, order_index=body.order,
                          aliases=_aliases(body.aliases), active=True, created_at=now, updated_at=now)
        session.add(student)
        audit(session, viewer, "student.create", {"studentId": str(student.id), "group": group}, now)
        view = student_view(student)
    return _no_store(view, 201)


@dashboard_router.patch("/api/v1/dashboard/students/{student_id}", dependencies=writes)
async def update_student(student_id: str, body: StudentPatch, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    sent = body.model_fields_set
    async with state.sessionmaker() as session, session.begin():
        student = await _visible_student(session, viewer, student_id, lock=True)
        group = await _visible_group(session, viewer, body.group, now) if "group" in sent and body.group else student.group_name
        email = _email(body.email) if "email" in sent else student.email
        ext = ((body.externalId or "").strip() or None) if "externalId" in sent else student.external_id
        await _clash(session, group, email, ext, not_id=student.id)
        changed: list[str] = []

        def put(field_name: str, value: Any) -> None:
            if getattr(student, field_name) != value:
                setattr(student, field_name, value)
                changed.append(field_name)

        put("group_name", group)
        if "fullName" in sent and body.fullName:
            name = re.sub(r"\s+", " ", body.fullName).strip()
            if not normalize(name):
                raise ApiError(400, "Invalid request", "The name needs letters.")
            put("full_name", name)
        put("email", email)
        put("external_id", ext)
        if "order" in sent:
            put("order_index", body.order)
        if "aliases" in sent and body.aliases is not None:
            put("aliases", _aliases(body.aliases))
        if "active" in sent and body.active is not None:
            put("active", body.active)
        if changed:
            student.updated_at = now
            audit(session, viewer, "student.update", {"studentId": str(student.id), "fields": changed}, now)
        view = student_view(student)
    return _no_store({**view, "changed": changed})


class ImportIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    group: str = Field(min_length=1, max_length=100)
    text: str = Field(min_length=1, max_length=2_000_000)
    dryRun: StrictBool = False


@dashboard_router.post("/api/v1/dashboard/students/import", dependencies=writes)
async def import_students(body: ImportIn, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """Adds the roster's students to the group, or updates the ones already there (found by e-mail,
    then id, then name). Nobody is removed. dryRun: say what would happen, change nothing."""
    parsed = parse_roster(body.text, group=body.group.strip())
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        group = await _visible_group(session, viewer, body.group, now)
        existing = (await session.execute(select(Student).where(Student.group_name == group).with_for_update())).scalars().all()
        by_email = {s.email.lower(): s for s in existing if s.email}
        by_ext = {s.external_id: s for s in existing if s.external_id}
        by_name = {normalize(s.full_name): s for s in existing}
        created, updated, unchanged = [], [], []
        touched: set[uuid.UUID] = set()
        for row in parsed.rows:
            matches = {candidate.id: candidate for candidate in (
                by_email.get(row.email), by_ext.get(row.external_id), by_name.get(normalize(row.full_name))
            ) if candidate is not None}
            if len(matches) > 1:
                problem = f"Line {row.line}: identifiers match different existing students."
                parsed.invalid.append(problem)
                parsed.skipped.append(problem)
                continue
            s = next(iter(matches.values()), None)
            if s is not None and s.id in touched:
                problem = f"Line {row.line}: the same student appears earlier."
                parsed.duplicates.append(problem)
                parsed.skipped.append(problem)
                continue
            if s is None:
                created.append(row.full_name)
                s = Student(id=uuid.uuid4(), group_name=group, full_name=row.full_name, email=row.email, external_id=row.external_id,
                            order_index=row.order, aliases=[], active=True, created_at=now, updated_at=now)
                session.add(s)
                touched.add(s.id)
                by_name[normalize(row.full_name)] = s
                if row.email:
                    by_email[row.email] = s
                if row.external_id:
                    by_ext[row.external_id] = s
                continue
            touched.add(s.id)
            changes = {k: v for k, v in (("full_name", row.full_name), ("email", row.email or s.email),
                                          ("external_id", row.external_id or s.external_id),
                                          ("order_index", row.order if row.order is not None else s.order_index), ("active", True))
                       if getattr(s, k) != v}
            if changes:
                updated.append(row.full_name)
                for k, v in changes.items():
                    setattr(s, k, v)
                s.updated_at = now
                if s.email:
                    by_email[s.email.lower()] = s
                if s.external_id:
                    by_ext[s.external_id] = s
                by_name[normalize(s.full_name)] = s
            else:
                unchanged.append(row.full_name)
        if not body.dryRun and (created or updated):
            audit(session, viewer, "student.import", {"group": group, "created": len(created), "updated": len(updated)}, now)
        if body.dryRun:
            await session.rollback()
    return _no_store({"group": group, "dryRun": body.dryRun, "created": len(created), "updated": len(updated),
                      "unchanged": len(unchanged), "skipped": parsed.skipped, "createdNames": created[:200],
                      "updatedNames": updated[:200], "invalidRows": parsed.invalid, "duplicateRows": parsed.duplicates})


@dashboard_router.get("/api/v1/dashboard/students/{student_id}/aliases")
async def student_aliases(student_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        student = await _visible_student(session, viewer, student_id)
        rows = (await session.execute(select(StudentAlias).where(StudentAlias.student_id == student.id)
                                      .order_by(StudentAlias.created_at.desc()))).scalars().all()
    return _no_store({"aliases": [{"id": str(a.id), "alias": a.alias, "status": a.status, "source": a.source,
                                   "createdAt": a.created_at} for a in rows]})


@dashboard_router.delete("/api/v1/dashboard/aliases/{alias_id}", dependencies=writes)
async def forget_alias(alias_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        row = await session.get(StudentAlias, parse_id(alias_id))
        student = await session.get(Student, row.student_id) if row else None
        if row is None or student is None or not viewer.can_see(student.group_name):
            raise ApiError(404, "Not found")
        await session.delete(row)
        audit(session, viewer, "student.alias_forget", {"studentId": str(student.id), "status": row.status}, now)
    return _no_store({"status": "forgotten"})


# =========================================================================== sessions (dashboard)


def session_view(att: AttendanceSession) -> dict[str, Any]:
    return {"id": str(att.id), "group": att.group_name, "date": att.session_date.isoformat(), "startTime": att.start_time,
            "title": att.title, "source": att.source, "status": att.status, "recordingId": str(att.recording_id) if att.recording_id else None,
            "startedAt": att.started_at, "endedAt": att.ended_at, "matchedAt": att.matched_at, "finalizedAt": att.finalized_at,
            "createdAt": att.created_at, "updatedAt": att.updated_at}


async def _visible_session(session: AsyncSession, viewer: Viewer, session_id: str, lock: bool = False) -> AttendanceSession:
    statement = select(AttendanceSession).where(AttendanceSession.id == parse_id(session_id))
    att = (await session.execute(statement.with_for_update() if lock else statement)).scalar_one_or_none()
    if att is None or not viewer.can_see(att.group_name):
        raise ApiError(404, "Not found")
    return att


@dashboard_router.get("/api/v1/dashboard/attendance/sessions")
async def list_sessions(request: Request, viewer: Viewer = Depends(current_viewer),
                        group: str | None = Query(default=None, max_length=100),
                        date_filter: str | None = Query(default=None, alias="date", max_length=10),
                        status: str | None = Query(default=None, pattern=r"^(open|closed|finalized)$"),
                        page: int = Query(default=1, ge=1, le=100000), page_size: int = Query(default=25, alias="pageSize", ge=1, le=100)) -> JSONResponse:
    conditions = [group_filter(viewer, AttendanceSession.group_name)]
    if group:
        conditions.append(AttendanceSession.group_name == group.strip())
    if date_filter:
        try:
            conditions.append(AttendanceSession.session_date == date.fromisoformat(date_filter))
        except ValueError as exc:
            raise ApiError(400, "Invalid request", "'date' must be yyyy-MM-dd.") from exc
    if status:
        conditions.append(AttendanceSession.status == status)
    async with request.app.state.sessionmaker() as session:
        total = (await session.execute(select(func.count()).select_from(AttendanceSession).where(*conditions))).scalar_one()
        rows = (await session.execute(select(AttendanceSession).where(*conditions).order_by(
            AttendanceSession.session_date.desc(), AttendanceSession.start_time.desc().nulls_last(), AttendanceSession.created_at.desc())
            .limit(page_size).offset((page - 1) * page_size))).scalars().all()
        ids = [r.id for r in rows]
        counts: dict[uuid.UUID, dict[str, int]] = {}
        if ids:
            for sid, st, n in (await session.execute(select(AttendanceRecord.session_id, AttendanceRecord.status, func.count())
                                                     .where(AttendanceRecord.session_id.in_(ids)).group_by(AttendanceRecord.session_id, AttendanceRecord.status))).all():
                counts.setdefault(sid, {})[st] = n
            snaps = dict((await session.execute(select(AttendanceSnapshot.session_id, func.max(AttendanceSnapshot.captured_at))
                                                .where(AttendanceSnapshot.session_id.in_(ids)).group_by(AttendanceSnapshot.session_id))).all())
        else:
            snaps = {}
    items = [{**session_view(r), "present": counts.get(r.id, {}).get("present", 0), "needsReview": counts.get(r.id, {}).get("needs_review", 0),
              "absent": counts.get(r.id, {}).get("absent", 0), "lastCapturedAt": snaps.get(r.id)} for r in rows]
    return _no_store({"items": items, "total": total, "page": page, "pageSize": page_size})


class SessionCreate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    group: str = Field(min_length=1, max_length=100)
    date: date
    startTime: str | None = Field(default=None, max_length=5)
    title: str | None = Field(default=None, max_length=200)


@dashboard_router.post("/api/v1/dashboard/attendance/sessions", dependencies=writes)
async def create_session(body: SessionCreate, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    if body.startTime and not TIME.match(body.startTime):
        raise ApiError(400, "Invalid request", "'startTime' must be HH:mm (24-hour).")
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        group = await _visible_group(session, viewer, body.group, now)
        att = AttendanceSession(id=uuid.uuid4(), group_name=group, session_date=body.date, start_time=body.startTime or None,
                                title=(body.title or "").strip() or None, source="manual", status="open", created_at=now, updated_at=now)
        session.add(att)
        await _link_recording(session, att)
        await session.flush()
        await recompute(session, att, now)
        audit(session, viewer, "attendance.session_create", {"sessionId": str(att.id), "group": group}, now)
        view = session_view(att)
    return _no_store(view, 201)


def _ai_on(request: Request) -> bool:
    return getattr(request.app.state, "attendance_ai", None) is not None


async def session_details(session: AsyncSession, att: AttendanceSession, ai_available: bool) -> dict[str, Any]:
    students = {s.id: s for s in (await session.execute(select(Student).where(Student.group_name == att.group_name))).scalars().all()}
    records = (await session.execute(select(AttendanceRecord).where(AttendanceRecord.session_id == att.id))).scalars().all()
    participants = (await session.execute(select(AttendanceParticipant).where(AttendanceParticipant.session_id == att.id)
                                          .order_by(AttendanceParticipant.first_seen_at, AttendanceParticipant.name))).scalars().all()
    by_pid = {p.id: p for p in participants}
    owner: dict[uuid.UUID, uuid.UUID] = {}
    for r in records:
        if r.participant_id:
            owner[r.participant_id] = r.student_id
        for x in r.extra_participant_ids:
            owner[uuid.UUID(x)] = r.student_id

    # candidates for the review screen: the same scores the matcher uses
    active = [s for s in students.values() if s.active]
    candidates: dict[uuid.UUID, list[dict[str, Any]]] = {}
    for p in participants:
        if p.ignored:
            continue
        scored = sorted(((score_names(s.full_name, list(s.aliases or []), p.name).value, s) for s in active), key=lambda x: -x[0])
        candidates[p.id] = [{"studentId": str(s.id), "score": v} for v, s in scored[:3] if v >= 35]

    def participant_brief(pid: uuid.UUID | None) -> dict[str, Any] | None:
        p = by_pid.get(pid) if pid else None
        return {"id": str(p.id), "name": p.name} if p else None

    order = lambda r: (students[r.student_id].order_index if r.student_id in students and students[r.student_id].order_index is not None else 100000,  # noqa: E731
                       students[r.student_id].full_name if r.student_id in students else "")
    record_views = []
    for r in sorted(records, key=order):
        s = students.get(r.student_id)
        if s is None:
            continue
        record_views.append({
            "studentId": str(s.id), "fullName": s.full_name, "email": s.email, "order": s.order_index, "active": s.active,
            "status": r.status, "confidence": r.confidence, "source": r.match_source, "reason": r.reason, "manual": r.manual,
            "participant": participant_brief(r.participant_id),
            "extraNames": [b["name"] for b in (participant_brief(uuid.UUID(x)) for x in r.extra_participant_ids) if b],
            "joinTime": r.join_time, "leaveTime": r.leave_time, "durationSeconds": r.duration_seconds,
        })
    participant_views = [{
        "id": str(p.id), "name": p.name, "firstSeenAt": p.first_seen_at, "lastSeenAt": p.last_seen_at, "sightings": p.sightings,
        "presentSeconds": p.present_seconds, "ignored": p.ignored, "assignedTo": str(owner[p.id]) if p.id in owner else None,
        "candidates": candidates.get(p.id, []),
    } for p in participants]
    unmatched = sum(1 for p in participants if not p.ignored and p.id not in owner)
    snapshots = (await session.execute(select(func.count(), func.max(AttendanceSnapshot.captured_at))
                                       .where(AttendanceSnapshot.session_id == att.id))).one()
    return {"session": session_view(att), "records": record_views, "participants": participant_views,
            "snapshots": {"count": snapshots[0], "lastCapturedAt": snapshots[1]}, "summary": summary_of(list(records), unmatched),
            "aiAvailable": ai_available}


@dashboard_router.get("/api/v1/dashboard/attendance/sessions/{session_id}")
async def get_session(session_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    async with request.app.state.sessionmaker() as session:
        att = await _visible_session(session, viewer, session_id)
        details = await session_details(session, att, _ai_on(request))
    return _no_store(details)


class NamesIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    names: list[str] = Field(min_length=1, max_length=MAX_PARTICIPANTS)
    capturedAt: datetime | None = None
    complete: StrictBool = True


@dashboard_router.post("/api/v1/dashboard/attendance/sessions/{session_id}/participants", dependencies=writes)
async def add_participants(session_id: str, body: NamesIn, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    """Names typed or pasted by a person (e.g. copied from Zoom's participant list, or the old extension's export)."""
    state = request.app.state
    now = state.clock()
    names = [n.strip()[:300] for n in body.names if n and n.strip()]
    if not names:
        raise ApiError(400, "Invalid request", "No names.")
    async with state.sessionmaker() as session, session.begin():
        att = await _visible_session(session, viewer, session_id, lock=True)
        if att.status == "finalized":
            raise ApiError(409, "Conflict", FINALIZED)
        captured = body.capturedAt or now
        session.add(AttendanceSnapshot(session_id=att.id, captured_at=captured, source="manual", trigger="manual",
                                       is_complete=body.complete, names=names, created_at=now))
        if att.started_at is None or captured < att.started_at:
            att.started_at = captured
        await session.flush()
        await recompute(session, att, now)
        audit(session, viewer, "attendance.participants_add", {"sessionId": str(att.id), "count": len(names)}, now)
        details = await session_details(session, att, _ai_on(request))
    return _no_store(details, 201)


class MatchIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    useAi: StrictBool = False


@dashboard_router.post("/api/v1/dashboard/attendance/sessions/{session_id}/match", dependencies=writes)
async def rematch(session_id: str, request: Request, body: MatchIn | None = None, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    use_ai = bool(body and body.useAi)
    ai = getattr(state, "attendance_ai", None)
    if use_ai and ai is None:
        raise ApiError(409, "Conflict", "AI matching is not configured on the server.")
    async with state.sessionmaker() as session, session.begin():
        att = await _visible_session(session, viewer, session_id, lock=True)
        if att.status == "finalized":
            raise ApiError(409, "Conflict", FINALIZED)
        await recompute(session, att, now)
        suggestions: list[dict[str, Any]] = []
        if use_ai:
            from .attendance_ai import apply_ai
            suggestions = await apply_ai(session, att, ai, now)
            await recompute(session, att, now)           # confident answers are remembered names now
        audit(session, viewer, "attendance.match", {"sessionId": str(att.id), "ai": use_ai,
                                                    "aiApplied": sum(1 for s in suggestions if s["applied"])}, now)
        details = await session_details(session, att, _ai_on(request))
    details["aiSuggestions"] = [s for s in suggestions if not s["applied"]]
    return _no_store(details)


class RecordIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    participantId: uuid.UUID | None = None
    status: Literal["present", "absent", "needs_review"] | None = None
    reset: StrictBool = False
    remember: StrictBool = True


async def remember(session: AsyncSession, student_id: uuid.UUID, name: str, accepted: bool, source: str,
                   user_id: uuid.UUID | None, now: datetime) -> None:
    """Name memory. A manual decision replaces whatever was remembered; an automatic one never
    replaces an existing entry."""
    key = normalize(name)
    if not key:
        return
    row = (await session.execute(select(StudentAlias).where(StudentAlias.student_id == student_id, StudentAlias.alias_key == key))).scalar_one_or_none()
    status = "accepted" if accepted else "rejected"
    if row is None:
        session.add(StudentAlias(id=uuid.uuid4(), student_id=student_id, alias=name[:300], alias_key=key, status=status,
                                 source=source, created_by=user_id, created_at=now))
    elif source == "manual":
        row.status, row.source, row.alias, row.created_by, row.created_at = status, source, name[:300], user_id, now


@dashboard_router.put("/api/v1/dashboard/attendance/sessions/{session_id}/records/{student_id}", dependencies=writes)
async def correct_record(session_id: str, student_id: str, body: RecordIn, request: Request,
                         viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        att = await _visible_session(session, viewer, session_id, lock=True)
        if att.status == "finalized":
            raise ApiError(409, "Conflict", FINALIZED)
        student = await session.get(Student, parse_id(student_id))
        if student is None or student.group_name != att.group_name:
            raise ApiError(404, "Not found")
        record = (await session.execute(select(AttendanceRecord).where(
            AttendanceRecord.session_id == att.id, AttendanceRecord.student_id == student.id).with_for_update())).scalar_one_or_none()
        if record is None:
            record = AttendanceRecord(id=uuid.uuid4(), session_id=att.id, student_id=student.id, status="absent", created_at=now, updated_at=now)
            session.add(record)
        action: dict[str, Any] = {"sessionId": str(att.id), "studentId": str(student.id)}
        if body.reset:
            record.manual = False
            action["change"] = "reset"
        elif body.participantId is not None:
            participant = await session.get(AttendanceParticipant, body.participantId)
            if participant is None or participant.session_id != att.id:
                raise ApiError(404, "Not found")
            if participant.ignored:
                raise ApiError(409, "Conflict", "That name is marked as not a student; include it first.")
            others = (await session.execute(select(AttendanceRecord).where(
                AttendanceRecord.session_id == att.id, AttendanceRecord.student_id != student.id,
                or_(AttendanceRecord.participant_id == participant.id,
                    AttendanceRecord.extra_participant_ids.contains([str(participant.id)]))))).scalars().all()
            for other in others:                                   # the name moves: whoever had it is matched again
                other.manual = False
                other.participant_id = None
            record.manual, record.participant_id, record.status = True, participant.id, body.status or "present"
            if body.remember:
                await remember(session, student.id, participant.name, True, "manual", viewer.user.id, now)
            action.update(change="match", participantName=participant.name, remembered=body.remember)
        elif body.status is not None:
            previous = await session.get(AttendanceParticipant, record.participant_id) if record.participant_id else None
            if previous is not None and body.remember:
                # absent: that name is not this student; present: it is
                await remember(session, student.id, previous.name, body.status == "present", "manual", viewer.user.id, now)
            record.manual, record.status = True, body.status
            record.participant_id = None if body.status == "absent" else record.participant_id
            action.update(change="status", status=body.status)
        else:
            raise ApiError(400, "Invalid request", "Send participantId, status or reset.")
        record.updated_by = viewer.user.id
        await session.flush()
        await recompute(session, att, now)
        audit(session, viewer, "attendance.correct", action, now)
        details = await session_details(session, att, _ai_on(request))
    return _no_store(details)


class IgnoreIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    ignored: StrictBool


@dashboard_router.post("/api/v1/dashboard/attendance/sessions/{session_id}/participants/{participant_id}/ignore", dependencies=writes)
async def ignore_participant(session_id: str, participant_id: str, body: IgnoreIn, request: Request,
                             viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        att = await _visible_session(session, viewer, session_id, lock=True)
        if att.status == "finalized":
            raise ApiError(409, "Conflict", FINALIZED)
        participant = await session.get(AttendanceParticipant, parse_id(participant_id))
        if participant is None or participant.session_id != att.id:
            raise ApiError(404, "Not found")
        participant.ignored = body.ignored
        if body.ignored:
            for record in (await session.execute(select(AttendanceRecord).where(
                    AttendanceRecord.session_id == att.id, AttendanceRecord.participant_id == participant.id))).scalars().all():
                record.manual, record.participant_id = False, None
        await session.flush()
        await recompute(session, att, now)
        audit(session, viewer, "attendance.ignore", {"sessionId": str(att.id), "ignored": body.ignored}, now)
        details = await session_details(session, att, _ai_on(request))
    return _no_store(details)


@dashboard_router.post("/api/v1/dashboard/attendance/sessions/{session_id}/finalize", dependencies=writes)
async def finalize(session_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        att = await _visible_session(session, viewer, session_id, lock=True)
        if att.status == "finalized":
            raise ApiError(409, "Conflict", "This session is already finalized.")
        await recompute(session, att, now)
        att.status, att.finalized_at, att.finalized_by, att.updated_at = "finalized", now, viewer.user.id, now
        audit(session, viewer, "attendance.finalize", {"sessionId": str(att.id)}, now)
        details = await session_details(session, att, _ai_on(request))
    return _no_store(details)


@dashboard_router.post("/api/v1/dashboard/attendance/sessions/{session_id}/reopen", dependencies=writes)
async def reopen(session_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> JSONResponse:
    state = request.app.state
    now = state.clock()
    async with state.sessionmaker() as session, session.begin():
        att = await _visible_session(session, viewer, session_id, lock=True)
        if att.status != "finalized":
            raise ApiError(409, "Conflict", "Only a finalized session can be reopened.")
        att.status, att.finalized_at, att.finalized_by, att.updated_at = ("closed" if att.ended_at else "open"), None, None, now
        await recompute(session, att, now)
        audit(session, viewer, "attendance.reopen", {"sessionId": str(att.id)}, now)
        details = await session_details(session, att, _ai_on(request))
    return _no_store(details)


def _cell(value: Any) -> str:
    """No spreadsheet formula can come from a Zoom display name: a leading = + - @ is quoted."""
    text = "" if value is None else str(value)
    return "'" + text if text[:1] in ("=", "+", "-", "@", "\t", "\r") else text


@dashboard_router.get("/api/v1/dashboard/attendance/sessions/{session_id}/export.csv")
async def export_csv(session_id: str, request: Request, viewer: Viewer = Depends(current_viewer)) -> Response:
    async with request.app.state.sessionmaker() as session:
        att = await _visible_session(session, viewer, session_id)
        details = await session_details(session, att, _ai_on(request))
    out = io.StringIO()
    writer = csv.writer(out, lineterminator="\r\n")
    writer.writerow(["Order", "Student", "Email", "Status", "Zoom name", "Other Zoom names", "Confidence", "Matched by",
                     "Join time (UTC)", "Leave time (UTC)", "Minutes", "Group", "Session date", "Start time"])
    label = {"present": "Present", "needs_review": "Needs review", "absent": "Absent"}
    for r in details["records"]:
        iso = lambda d: _json(d) if d else ""  # noqa: E731
        writer.writerow([_cell(r["order"]), _cell(r["fullName"]), _cell(r["email"]), label.get(r["status"], r["status"]),
                         _cell(r["participant"]["name"] if r["participant"] else ""), _cell(" | ".join(r["extraNames"])),
                         r["confidence"] if r["status"] != "absent" else "", r["source"] if r["source"] != "none" else "",
                         iso(r["joinTime"]), iso(r["leaveTime"]),
                         round(r["durationSeconds"] / 60) if r["durationSeconds"] is not None else "",
                         _cell(att.group_name), att.session_date.isoformat(), att.start_time or ""])
    filename = f"attendance-{att.group_name}-{att.session_date.isoformat()}.csv".replace(" ", "_")
    return Response("\ufeff" + out.getvalue(), media_type="text/csv; charset=utf-8",
                    headers={"Content-Disposition": f'attachment; filename="{filename}"', "Cache-Control": "no-store"})

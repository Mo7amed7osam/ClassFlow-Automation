"""The database tables. No password, cookie, browser profile, LMS credential or API key is stored:
device tokens and enrollment tokens are kept only as SHA-256 hashes."""

from __future__ import annotations

import uuid
from datetime import date, datetime
from typing import Any

from sqlalchemy import (
    BigInteger,
    Boolean,
    CheckConstraint,
    Date,
    DateTime,
    ForeignKey,
    Index,
    Integer,
    String,
    Text,
    func,
    text,
)
from sqlalchemy.dialects.postgresql import JSONB, UUID
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column

JOB_STATUSES = ("queued", "assigned", "running", "succeeded", "failed", "cancelled")
ACTIVE_JOB_STATUSES = ("assigned", "running")
FINAL_JOB_STATUSES = ("succeeded", "failed", "cancelled")


class Base(DeclarativeBase):
    pass


class Device(Base):
    __tablename__ = "devices"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    installation_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), unique=True, nullable=False)
    name: Mapped[str] = mapped_column(String(100), nullable=False)
    version: Mapped[str] = mapped_column(String(50), nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False, default="offline")
    agent_state: Mapped[str | None] = mapped_column(String(16))
    capabilities: Mapped[list[str]] = mapped_column(JSONB, nullable=False, default=list)
    token_hash: Mapped[str | None] = mapped_column(String(64))
    last_heartbeat: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    last_assigned_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    connected_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    revoked_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("status IN ('online', 'offline')", name="ck_devices_status"),
        CheckConstraint("agent_state IS NULL OR agent_state IN ('idle', 'busy')", name="ck_devices_agent_state"),
    )


class EnrollmentToken(Base):
    __tablename__ = "enrollment_tokens"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    token_hash: Mapped[str] = mapped_column(String(64), unique=True, nullable=False)
    label: Mapped[str] = mapped_column(String(100), nullable=False)
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    used_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    used_by_device_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("devices.id"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())


class Job(Base):
    __tablename__ = "jobs"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    type: Mapped[str] = mapped_column(String(64), nullable=False)
    device_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("devices.id"))
    payload: Mapped[dict[str, Any]] = mapped_column(JSONB, nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False, default="queued")
    result: Mapped[dict[str, Any] | None] = mapped_column(JSONB)
    error: Mapped[dict[str, Any] | None] = mapped_column(JSONB)
    idempotency_key: Mapped[str | None] = mapped_column(String(200), unique=True)
    attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=0)
    max_attempts: Mapped[int] = mapped_column(Integer, nullable=False, default=3)
    available_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    assigned_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    accepted_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    # Set only for jobs the dashboard creates from a recording; jobs from /api/v1/jobs leave it empty.
    recording_id: Mapped[uuid.UUID | None] = mapped_column(
        UUID(as_uuid=True), ForeignKey("recordings.id", ondelete="SET NULL"), index=True
    )
    occurrence_id: Mapped[uuid.UUID | None] = mapped_column(
        UUID(as_uuid=True), ForeignKey("class_occurrences.id", ondelete="SET NULL"), index=True
    )

    __table_args__ = (
        CheckConstraint(
            "status IN ('queued', 'assigned', 'running', 'succeeded', 'failed', 'cancelled')",
            name="ck_jobs_status",
        ),
        Index("ix_jobs_dispatch", "status", "available_at", "created_at"),
        Index("ix_jobs_device_status", "device_id", "status"),
    )


class JobEvent(Base):
    __tablename__ = "job_events"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    job_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("jobs.id"), nullable=False)
    device_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("devices.id"))
    event_type: Mapped[str] = mapped_column(String(40), nullable=False)
    payload: Mapped[dict[str, Any] | None] = mapped_column(JSONB)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (Index("ix_job_events_job", "job_id", "id"),)


class Recording(Base):
    """One session recording as the recordings sheet (via n8n) describes it, and whether its link
    has reached the LMS yet. A session is identified by group + date + start time (no time = one
    slot for the day), enforced by a unique index."""

    __tablename__ = "recordings"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    group_name: Mapped[str] = mapped_column(String(100), nullable=False, index=True)
    session_date: Mapped[date] = mapped_column(Date, nullable=False)
    start_time: Mapped[str | None] = mapped_column(String(5))
    file_name: Mapped[str | None] = mapped_column(String(255))
    record_type: Mapped[str | None] = mapped_column(String(50))
    drive_link: Mapped[str | None] = mapped_column(Text)
    zoom_link: Mapped[str | None] = mapped_column(Text)
    source: Mapped[str] = mapped_column(String(16), nullable=False, server_default="zoom")
    lms_status: Mapped[str] = mapped_column(String(32), nullable=False, server_default="pending")
    lms_updated_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        Index(
            "uq_recordings_session",
            "group_name",
            "session_date",
            text("coalesce(start_time, '')"),
            unique=True,
        ),
        Index("ix_recordings_session_date", "session_date"),
    )


USER_ROLES = ("admin", "coordinator")
USER_STATUSES = ("pending", "active", "rejected", "disabled")


class User(Base):
    """A person who signs in to the dashboard: the one admin, or a coordinator. Only a scrypt hash of
    the password is kept. At most one admin can exist (a partial unique index)."""

    __tablename__ = "users"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    username: Mapped[str] = mapped_column(String(50), nullable=False, unique=True)   # stored lower-case
    display_name: Mapped[str] = mapped_column(String(100), nullable=False)
    password_hash: Mapped[str] = mapped_column(String(200), nullable=False)
    role: Mapped[str] = mapped_column(String(16), nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False)
    approved_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    approved_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="SET NULL"))
    last_login_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("role IN ('admin', 'coordinator')", name="ck_users_role"),
        CheckConstraint("status IN ('pending', 'active', 'rejected', 'disabled')", name="ck_users_status"),
        # There may be several admins: an admin makes another, who has the same powers. A
        # coordinator is never promoted, so the role is decided when the row is written and no
        # endpoint accepts a change to it (migration 0012_several_admins).
        Index("ix_users_role_status", "role", "status"),
    )


class Group(Base):
    """A class group, named by its LMS code (e.g. CAI5_AIS4_S7). The name never changes - the
    recordings sheet keeps sending it - so a friendlier label goes in display_name. Recordings are
    matched to a group by that name."""

    __tablename__ = "groups"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    name: Mapped[str] = mapped_column(String(100), nullable=False, unique=True)
    display_name: Mapped[str | None] = mapped_column(String(100))
    archived_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())


class UserGroup(Base):
    """Which groups a coordinator may see."""

    __tablename__ = "user_groups"

    user_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="CASCADE"), primary_key=True)
    group_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("groups.id", ondelete="CASCADE"), primary_key=True)
    assigned_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="SET NULL"))
    assigned_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (Index("ix_user_groups_group", "group_id"),)


class AdminSession(Base):
    """A dashboard login (admin or coordinator). Only the session token's SHA-256 is kept; the token
    is in the browser's cookie. The user's role and status are read from users on every request."""

    __tablename__ = "admin_sessions"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    token_hash: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    username: Mapped[str] = mapped_column(String(50), nullable=False)
    user_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="CASCADE"), index=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, index=True)


class AdminAuditLog(Base):
    """What a dashboard user did: who, to which recording (or account), what, when. Never a
    password, token or full link."""

    __tablename__ = "admin_audit_log"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    username: Mapped[str] = mapped_column(String(50), nullable=False)
    user_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True))
    action: Mapped[str] = mapped_column(String(40), nullable=False)
    recording_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True))
    details: Mapped[dict[str, Any] | None] = mapped_column(JSONB)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (Index("ix_admin_audit_log_recording", "recording_id", "id"),)


STUDENT_ALIAS_STATUSES = ("accepted", "rejected")
ATTENDANCE_SESSION_STATUSES = ("open", "closed", "finalized")
ATTENDANCE_RECORD_STATUSES = ("present", "absent", "needs_review")


class Student(Base):
    """One student on a group's roster. The roster order, e-mail and an external id (the LMS's or
    the Windows roster's) are optional. Removing a student only deactivates them, so past attendance
    keeps its names."""

    __tablename__ = "students"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    group_name: Mapped[str] = mapped_column(String(100), nullable=False, index=True)
    full_name: Mapped[str] = mapped_column(String(300), nullable=False)
    email: Mapped[str | None] = mapped_column(String(320))
    external_id: Mapped[str | None] = mapped_column(String(128))
    order_index: Mapped[int | None] = mapped_column(Integer)
    aliases: Mapped[list[str]] = mapped_column(JSONB, nullable=False, server_default=text("'[]'::jsonb"))
    active: Mapped[bool] = mapped_column(Boolean, nullable=False, server_default=text("true"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        Index("uq_students_group_email", "group_name", text("lower(email)"), unique=True, postgresql_where=text("email IS NOT NULL")),
        Index("uq_students_group_external", "group_name", "external_id", unique=True, postgresql_where=text("external_id IS NOT NULL")),
    )


class StudentAlias(Base):
    """Name memory: a Zoom display name that is (accepted) or is not (rejected) a given student.
    Written by manual corrections, and by confident automatic matches; a manual decision always
    wins over an automatic one. Shared by every coordinator of the group, across sessions."""

    __tablename__ = "student_aliases"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    student_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("students.id", ondelete="CASCADE"), nullable=False)
    alias: Mapped[str] = mapped_column(String(300), nullable=False)
    alias_key: Mapped[str] = mapped_column(String(300), nullable=False, index=True)
    status: Mapped[str] = mapped_column(String(16), nullable=False)
    source: Mapped[str] = mapped_column(String(16), nullable=False)
    created_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("status IN ('accepted', 'rejected')", name="ck_student_aliases_status"),
        Index("uq_student_aliases_pair", "student_id", "alias_key", unique=True),
    )


class AttendanceSession(Base):
    """One class meeting of a group whose attendance is taken: from the Windows agent's snapshots,
    or created by hand. external_ref is the capturing client's own id for the meeting (the Windows
    app's session GUID), so re-sent snapshots land in the same session."""

    __tablename__ = "attendance_sessions"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    group_name: Mapped[str] = mapped_column(String(100), nullable=False)
    session_date: Mapped[date] = mapped_column(Date, nullable=False)
    start_time: Mapped[str | None] = mapped_column(String(5))
    title: Mapped[str | None] = mapped_column(String(200))
    source: Mapped[str] = mapped_column(String(16), nullable=False)
    external_ref: Mapped[str | None] = mapped_column(String(200))
    meeting_url: Mapped[str | None] = mapped_column(Text)
    device_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("devices.id", ondelete="SET NULL"))
    recording_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("recordings.id", ondelete="SET NULL"))
    status: Mapped[str] = mapped_column(String(16), nullable=False, server_default="open")
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    ended_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    matched_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    finalized_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    finalized_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("status IN ('open', 'closed', 'finalized')", name="ck_attendance_sessions_status"),
        Index("ix_attendance_sessions_group_date", "group_name", "session_date"),
        Index("uq_attendance_sessions_external", "external_ref", unique=True, postgresql_where=text("external_ref IS NOT NULL")),
    )


class AttendanceSnapshot(Base):
    """Raw evidence: the participant names one read of the Zoom list returned. Kept as sent; the
    participants and records are worked out from these. client_snapshot_id makes re-sending safe."""

    __tablename__ = "attendance_snapshots"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    session_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("attendance_sessions.id", ondelete="CASCADE"), nullable=False)
    client_snapshot_id: Mapped[str | None] = mapped_column(String(200), unique=True)
    captured_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    source: Mapped[str] = mapped_column(String(16), nullable=False)
    trigger: Mapped[str] = mapped_column(String(24), nullable=False)
    is_complete: Mapped[bool] = mapped_column(Boolean, nullable=False, server_default=text("false"))
    names: Mapped[list[str]] = mapped_column(JSONB, nullable=False)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (Index("ix_attendance_snapshots_session", "session_id", "captured_at"),)


class AttendanceParticipant(Base):
    """A name seen in a session's Zoom list, with when it was first and last seen, the time it was
    present (from the snapshots) and its presence intervals. ignored marks a host or staff member."""

    __tablename__ = "attendance_participants"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    session_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("attendance_sessions.id", ondelete="CASCADE"), nullable=False)
    name: Mapped[str] = mapped_column(String(300), nullable=False)
    name_key: Mapped[str] = mapped_column(String(300), nullable=False)
    first_seen_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    last_seen_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    sightings: Mapped[int] = mapped_column(Integer, nullable=False, server_default="0")
    present_seconds: Mapped[int] = mapped_column(Integer, nullable=False, server_default="0")
    intervals: Mapped[list[Any]] = mapped_column(JSONB, nullable=False, server_default=text("'[]'::jsonb"))
    ignored: Mapped[bool] = mapped_column(Boolean, nullable=False, server_default=text("false"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (Index("uq_attendance_participants_name", "session_id", "name_key", unique=True),)


class AttendanceRecord(Base):
    """A student's attendance in one session: present / absent / needs review, the Zoom name it
    was matched to (one name per student, one student per name), how sure and by what, and join /
    leave / duration from that name's presence. manual = set by a person, kept by re-matching."""

    __tablename__ = "attendance_records"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    session_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("attendance_sessions.id", ondelete="CASCADE"), nullable=False)
    student_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("students.id", ondelete="CASCADE"), nullable=False)
    participant_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("attendance_participants.id", ondelete="SET NULL"))
    extra_participant_ids: Mapped[list[str]] = mapped_column(JSONB, nullable=False, server_default=text("'[]'::jsonb"))
    status: Mapped[str] = mapped_column(String(16), nullable=False)
    confidence: Mapped[int] = mapped_column(Integer, nullable=False, server_default="0")
    match_source: Mapped[str] = mapped_column(String(16), nullable=False, server_default="none")
    reason: Mapped[str | None] = mapped_column(String(300))
    join_time: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    leave_time: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    duration_seconds: Mapped[int | None] = mapped_column(Integer)
    manual: Mapped[bool] = mapped_column(Boolean, nullable=False, server_default=text("false"))
    updated_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("status IN ('present', 'absent', 'needs_review')", name="ck_attendance_records_status"),
        Index("uq_attendance_records_student", "session_id", "student_id", unique=True),
    )


class LmsAccount(Base):
    """An LMS sign-in a dashboard user works with (their coordinator or admin account on the LMS).
    The password is AES-GCM encrypted with CENTRAL_SECRETS_KEY (never stored here); one account per
    user is the active one, which that user's app signs in with."""

    __tablename__ = "lms_accounts"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    user_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True)
    label: Mapped[str] = mapped_column(String(100), nullable=False)
    email: Mapped[str] = mapped_column(String(320), nullable=False)
    role: Mapped[str] = mapped_column(String(16), nullable=False)
    password_encrypted: Mapped[str] = mapped_column(Text, nullable=False)
    active: Mapped[bool] = mapped_column(Boolean, nullable=False, server_default=text("false"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("role IN ('admin', 'coordinator')", name="ck_lms_accounts_role"),
        Index("uq_lms_accounts_user_email", "user_id", text("lower(email)"), unique=True),
        Index("uq_lms_accounts_one_active", "user_id", unique=True, postgresql_where=text("active")),
    )


class ZoomAccount(Base):
    """A Zoom account a user opens classes with, kept against their dashboard account.

    Which account (the id the PC knows it by), its e-mail, the group it hosts and the link its
    classes open - so another PC running that person's classes knows which account to open them
    with, and with what link, instead of somebody typing it in twice.

    The Zoom password is kept too, AES-GCM encrypted with CENTRAL_SECRETS_KEY exactly as an LMS
    one is, and only when its PC has one saved. Without it a browser profile nobody has signed in
    - a new PC, or the second profile of a simultaneous class - can only join as a guest, and a
    guest cannot admit anybody. With it the app signs that profile in by itself, once, the way a
    person would. Zoom asking for a captcha or a one-time code still needs a person; the app says
    so and leaves the visible sign-in to them.
    """

    __tablename__ = "zoom_accounts"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    user_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True)
    account_id: Mapped[str] = mapped_column(String(100), nullable=False)
    label: Mapped[str] = mapped_column(String(100), nullable=False)
    zoom_email: Mapped[str | None] = mapped_column(String(320))
    group_name: Mapped[str | None] = mapped_column(String(100))
    default_meeting_url: Mapped[str | None] = mapped_column(Text)
    preferred_engine: Mapped[str | None] = mapped_column(String(8))
    password_encrypted: Mapped[str | None] = mapped_column(Text)
    active: Mapped[bool] = mapped_column(Boolean, nullable=False, server_default=text("false"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("preferred_engine IS NULL OR preferred_engine IN ('desktop', 'web')", name="ck_zoom_accounts_engine"),
        Index("uq_zoom_accounts_user_account", "user_id", text("lower(account_id)"), unique=True),
    )


RUN_PLAN_STATUSES = ("planned", "skipped", "opened", "done", "failed")
RUN_PLAN_SOURCES = ("lms", "manual")
RUN_ENGINES = ("desktop", "web")


class UserSchedule(Base):
    """The classes a person's own PC opens by itself, kept against their dashboard account.

    One row per user, holding that PC's schedule list as it stands. The server never reasons about
    what is in it - the times, the days and the engine are the Windows app's own shape - so it is
    kept as one document rather than modelled twice and left to drift. It is what lets a person sign
    in on a new PC and find their classes there instead of setting them up again.

    Only their own classes are here. The ones this PC runs for other coordinators live in
    class_plans, which the server does reason about.
    """

    __tablename__ = "user_schedules"

    user_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("users.id", ondelete="CASCADE"), primary_key=True
    )
    schedules: Mapped[list[Any]] = mapped_column(JSONB, nullable=False, server_default=text("'[]'::jsonb"))
    """Each entry as the app writes it: id, name, meetingUrl, accountId, time, days, enabled, …"""
    count: Mapped[int] = mapped_column(Integer, nullable=False, server_default="0")
    """How many are in it, so a listing does not have to open the document."""
    device_name: Mapped[str | None] = mapped_column(String(100))
    """Which PC sent them last, for a person wondering where these came from."""
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())


class RunDelegation(Base):
    """A coordinator whose classes the admin's PC opens and finishes instead of theirs.

    The admin chooses the coordinator; nothing here holds a password. lms_account_id names which of
    that coordinator's LMS sign-ins the class steps use (null: the one they have in use), and
    zoom_account the Zoom account on the running PC that opens their meetings. Two coordinators run
    side by side because each carries its own pair.
    """

    __tablename__ = "run_delegations"

    coordinator_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("users.id", ondelete="CASCADE"), primary_key=True
    )
    enabled: Mapped[bool] = mapped_column(Boolean, nullable=False, server_default=text("true"))
    lms_account_id: Mapped[uuid.UUID | None] = mapped_column(
        UUID(as_uuid=True), ForeignKey("lms_accounts.id", ondelete="SET NULL")
    )
    # Which of their Zoom accounts opens their classes, and the name the running PC knows it by.
    # The name is kept beside the reference so a class still says what it wanted when the account
    # is removed.
    zoom_account_id: Mapped[uuid.UUID | None] = mapped_column(
        UUID(as_uuid=True), ForeignKey("zoom_accounts.id", ondelete="SET NULL")
    )
    zoom_account: Mapped[str | None] = mapped_column(String(100))
    created_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="SET NULL"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())


class ClassPlan(Base):
    """One class of a delegated coordinator: when it is, which group, and what opens it.

    The rows come from that coordinator's own LMS session list, read with their sign-in, so the
    timetable is theirs and not a copy somebody has to keep up to date. The LMS does not carry a
    Zoom link, so meeting_url (and the Zoom account that opens it) is filled in here or inherited
    from the delegation; a class without one is shown as needing it rather than failing at its time.
    """

    __tablename__ = "class_plans"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    coordinator_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    group_name: Mapped[str] = mapped_column(String(100), nullable=False)
    session_date: Mapped[date] = mapped_column(Date, nullable=False)
    start_time: Mapped[str | None] = mapped_column(String(5))
    title: Mapped[str | None] = mapped_column(String(200))
    meeting_url: Mapped[str | None] = mapped_column(Text)
    zoom_account: Mapped[str | None] = mapped_column(String(100))
    preferred_engine: Mapped[str | None] = mapped_column(String(8))
    source: Mapped[str] = mapped_column(String(16), nullable=False, server_default="lms")
    status: Mapped[str] = mapped_column(String(16), nullable=False, server_default="planned")
    note: Mapped[str | None] = mapped_column(String(300))
    imported_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("source IN ('lms', 'manual')", name="ck_class_plans_source"),
        CheckConstraint(
            "status IN ('planned', 'skipped', 'opened', 'done', 'failed')", name="ck_class_plans_status"
        ),
        CheckConstraint(
            "preferred_engine IS NULL OR preferred_engine IN ('desktop', 'web')", name="ck_class_plans_engine"
        ),
        Index(
            "uq_class_plans_class",
            "coordinator_id",
            "group_name",
            "session_date",
            text("coalesce(start_time, '')"),
            unique=True,
        ),
        Index("ix_class_plans_date", "session_date", "start_time"),
    )


OCCURRENCE_STATES = (
    "scheduled", "live", "attendancePending", "attendanceSubmitted", "correctionPending",
    "attendanceFinalized", "lmsSessionCompleted", "recordingPending", "zoomLinkFound", "zoomLinkAttached",
    "waitingForDrive", "driveLinkFound", "driveLinkAttached", "conflict", "failed", "skipped",
)


class ClassOccurrence(Base):
    """The durable state of one planned class, retained across workers and deployments."""

    __tablename__ = "class_occurrences"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    class_plan_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("class_plans.id", ondelete="CASCADE"), nullable=False, unique=True
    )
    group_name: Mapped[str] = mapped_column(String(100), nullable=False, index=True)
    session_date: Mapped[date] = mapped_column(Date, nullable=False)
    scheduled_start: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    scheduled_end: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    zoom_account_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("zoom_accounts.id", ondelete="SET NULL"))
    # Recording attachment is an LMS operation.  Keep the account selected when the class was
    # scheduled, rather than letting a later worker pick whichever profile happens to be open.
    lms_account_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("lms_accounts.id", ondelete="SET NULL"))
    zoom_meeting_url: Mapped[str | None] = mapped_column(Text)
    actual_start: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    actual_end: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    lms_session_url: Mapped[str | None] = mapped_column(Text)
    lms_session_id: Mapped[str | None] = mapped_column(String(200))
    attendance_session_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("attendance_sessions.id", ondelete="SET NULL"))
    state: Mapped[str] = mapped_column(String(32), nullable=False, server_default="scheduled")
    zoom_recording_url: Mapped[str | None] = mapped_column(Text)
    drive_recording_url: Mapped[str | None] = mapped_column(Text)
    recording_found_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    retry_state: Mapped[dict[str, Any]] = mapped_column(JSONB, nullable=False, server_default=text("'{}'::jsonb"))
    last_error: Mapped[str | None] = mapped_column(String(500))
    next_retry_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("state IN ('scheduled', 'live', 'attendancePending', 'attendanceSubmitted', 'correctionPending', 'attendanceFinalized', 'lmsSessionCompleted', 'recordingPending', 'zoomLinkFound', 'zoomLinkAttached', 'waitingForDrive', 'driveLinkFound', 'driveLinkAttached', 'conflict', 'failed', 'skipped')", name="ck_class_occurrences_state"),
        Index("ix_class_occurrences_group_date", "group_name", "session_date"),
        Index("ix_class_occurrences_state_retry", "state", "next_retry_at"),
    )


class AppSetting(Base):
    """A setting shared by every copy of the app (e.g. the recordings sheet's link). Never a secret."""

    __tablename__ = "app_settings"

    key: Mapped[str] = mapped_column(String(64), primary_key=True)
    value: Mapped[dict[str, Any]] = mapped_column(JSONB, nullable=False)
    updated_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="SET NULL"))
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())


class GoogleSheetsConnection(Base):
    """The one Google OAuth connection used by the cloud recording synchronizer.

    The refresh token is AES-GCM encrypted before it reaches this table.  The spreadsheet itself
    is deliberately not secret, but keeping it alongside the connection makes a deployed worker
    independent of a particular administrator's browser or computer.
    """

    __tablename__ = "google_sheets_connections"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    spreadsheet_id: Mapped[str] = mapped_column(String(200), nullable=False)
    refresh_token_encrypted: Mapped[str] = mapped_column(Text, nullable=False)
    google_email: Mapped[str | None] = mapped_column(String(320))
    connected_by: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("users.id", ondelete="SET NULL"))
    connected_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())


class GoogleSheetsSyncRecord(Base):
    """A durable, per-row result for the read-only recordings sheet.

    `row_key` includes the tab and the Drive URL.  Thus a scheduled run can safely be repeated
    after a restart, while a changed Drive link for the same class remains visible for review
    instead of silently replacing an already attached recording.
    """

    __tablename__ = "google_sheets_sync_records"

    id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), primary_key=True)
    connection_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("google_sheets_connections.id", ondelete="CASCADE"), nullable=False)
    row_key: Mapped[str] = mapped_column(String(300), nullable=False)
    group_name: Mapped[str] = mapped_column(String(100), nullable=False)
    session_date: Mapped[date] = mapped_column(Date, nullable=False)
    drive_link: Mapped[str] = mapped_column(Text, nullable=False)
    status: Mapped[str] = mapped_column(String(16), nullable=False, server_default="pending")
    detail: Mapped[str | None] = mapped_column(String(500))
    recording_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("recordings.id", ondelete="SET NULL"))
    occurrence_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("class_occurrences.id", ondelete="SET NULL"))
    job_id: Mapped[uuid.UUID | None] = mapped_column(UUID(as_uuid=True), ForeignKey("jobs.id", ondelete="SET NULL"))
    processed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        CheckConstraint("status IN ('pending', 'processing', 'attached', 'conflict', 'failed')", name="ck_google_sheet_sync_status"),
        Index("uq_google_sheet_sync_row", "connection_id", "row_key", unique=True),
        Index("ix_google_sheet_sync_class", "group_name", "session_date"),
        Index("ix_google_sheet_sync_job", "job_id"),
    )


ACTIVITY_OUTCOMES = ("done", "failed", "skipped")


class DeviceActivity(Base):
    """
    What a PC did on its own: a class opened, an LMS step finished, a class ended. Every PC works
    by itself and writes these down locally, so nothing waits on the network; its agent sends them
    when the server answers. Re-sending the same client_event_id changes nothing, so a device that
    was away for a day can simply send everything again.
    """

    __tablename__ = "device_activity"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    device_id: Mapped[uuid.UUID] = mapped_column(UUID(as_uuid=True), ForeignKey("devices.id", ondelete="CASCADE"), nullable=False)
    client_event_id: Mapped[str] = mapped_column(String(200), nullable=False, unique=True)
    happened_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    kind: Mapped[str] = mapped_column(String(40), nullable=False)
    outcome: Mapped[str] = mapped_column(String(16), nullable=False)
    group_name: Mapped[str | None] = mapped_column(String(100))
    session_date: Mapped[date | None] = mapped_column(Date)
    summary: Mapped[str] = mapped_column(String(500), nullable=False, server_default=text("''"))
    detail: Mapped[dict[str, Any] | None] = mapped_column(JSONB)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False, server_default=func.now())

    __table_args__ = (
        Index("ix_device_activity_device", "device_id", "happened_at"),
        Index("ix_device_activity_class", "group_name", "session_date"),
        CheckConstraint("outcome IN ('done', 'failed', 'skipped')", name="ck_device_activity_outcome"),
    )


__all__ = [
    "ACTIVITY_OUTCOMES",
    "RUN_ENGINES",
    "RUN_PLAN_SOURCES",
    "RUN_PLAN_STATUSES",
    "OCCURRENCE_STATES",
    "ClassPlan",
    "ClassOccurrence",
    "RunDelegation",
    "UserSchedule",
    "ZoomAccount",
    "AppSetting",
    "GoogleSheetsConnection",
    "GoogleSheetsSyncRecord",
    "DeviceActivity",
    "LmsAccount",
    "ATTENDANCE_RECORD_STATUSES",
    "ATTENDANCE_SESSION_STATUSES",
    "STUDENT_ALIAS_STATUSES",
    "AttendanceParticipant",
    "AttendanceRecord",
    "AttendanceSession",
    "AttendanceSnapshot",
    "Student",
    "StudentAlias",
    "AdminAuditLog",
    "AdminSession",
    "Group",
    "User",
    "UserGroup",
    "USER_ROLES",
    "USER_STATUSES",
    "ACTIVE_JOB_STATUSES",
    "FINAL_JOB_STATUSES",
    "JOB_STATUSES",
    "Base",
    "Device",
    "EnrollmentToken",
    "Job",
    "JobEvent",
    "Recording",
]

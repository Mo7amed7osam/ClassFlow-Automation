"""Attendance: rosters (students, name memory), sessions, raw snapshots, participants and records.

Additive only: six new tables; nothing existing is touched.

Revision ID: 0006_attendance
Revises: 0005_users_and_groups
Create Date: 2026-09-14
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0006_attendance"
down_revision = "0005_users_and_groups"
branch_labels = None
depends_on = None

UUID = postgresql.UUID(as_uuid=True)
JSONB = postgresql.JSONB
TS = sa.DateTime(timezone=True)


def upgrade() -> None:
    op.create_table(
        "students",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("group_name", sa.String(100), nullable=False),
        sa.Column("full_name", sa.String(300), nullable=False),
        sa.Column("email", sa.String(320)),
        sa.Column("external_id", sa.String(128)),
        sa.Column("order_index", sa.Integer),
        sa.Column("aliases", JSONB, nullable=False, server_default=sa.text("'[]'::jsonb")),
        sa.Column("active", sa.Boolean, nullable=False, server_default=sa.text("true")),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_students_group_name", "students", ["group_name"])
    op.create_index("uq_students_group_email", "students", ["group_name", sa.text("lower(email)")], unique=True,
                    postgresql_where=sa.text("email IS NOT NULL"))
    op.create_index("uq_students_group_external", "students", ["group_name", "external_id"], unique=True,
                    postgresql_where=sa.text("external_id IS NOT NULL"))

    op.create_table(
        "student_aliases",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("student_id", UUID, sa.ForeignKey("students.id", ondelete="CASCADE"), nullable=False),
        sa.Column("alias", sa.String(300), nullable=False),
        sa.Column("alias_key", sa.String(300), nullable=False),
        sa.Column("status", sa.String(16), nullable=False),
        sa.Column("source", sa.String(16), nullable=False),
        sa.Column("created_by", UUID),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("status IN ('accepted', 'rejected')", name="ck_student_aliases_status"),
    )
    op.create_index("ix_student_aliases_alias_key", "student_aliases", ["alias_key"])
    op.create_index("uq_student_aliases_pair", "student_aliases", ["student_id", "alias_key"], unique=True)

    op.create_table(
        "attendance_sessions",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("group_name", sa.String(100), nullable=False),
        sa.Column("session_date", sa.Date, nullable=False),
        sa.Column("start_time", sa.String(5)),
        sa.Column("title", sa.String(200)),
        sa.Column("source", sa.String(16), nullable=False),
        sa.Column("external_ref", sa.String(200)),
        sa.Column("meeting_url", sa.Text),
        sa.Column("device_id", UUID, sa.ForeignKey("devices.id", ondelete="SET NULL", name="fk_attendance_sessions_device")),
        sa.Column("recording_id", UUID, sa.ForeignKey("recordings.id", ondelete="SET NULL", name="fk_attendance_sessions_recording")),
        sa.Column("status", sa.String(16), nullable=False, server_default="open"),
        sa.Column("started_at", TS),
        sa.Column("ended_at", TS),
        sa.Column("matched_at", TS),
        sa.Column("finalized_at", TS),
        sa.Column("finalized_by", UUID),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("status IN ('open', 'closed', 'finalized')", name="ck_attendance_sessions_status"),
    )
    op.create_index("ix_attendance_sessions_group_date", "attendance_sessions", ["group_name", "session_date"])
    op.create_index("uq_attendance_sessions_external", "attendance_sessions", ["external_ref"], unique=True,
                    postgresql_where=sa.text("external_ref IS NOT NULL"))

    op.create_table(
        "attendance_snapshots",
        sa.Column("id", sa.BigInteger, primary_key=True, autoincrement=True),
        sa.Column("session_id", UUID, sa.ForeignKey("attendance_sessions.id", ondelete="CASCADE"), nullable=False),
        sa.Column("client_snapshot_id", sa.String(200), unique=True),
        sa.Column("captured_at", TS, nullable=False),
        sa.Column("source", sa.String(16), nullable=False),
        sa.Column("trigger", sa.String(24), nullable=False),
        sa.Column("is_complete", sa.Boolean, nullable=False, server_default=sa.text("false")),
        sa.Column("names", JSONB, nullable=False),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_attendance_snapshots_session", "attendance_snapshots", ["session_id", "captured_at"])

    op.create_table(
        "attendance_participants",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("session_id", UUID, sa.ForeignKey("attendance_sessions.id", ondelete="CASCADE"), nullable=False),
        sa.Column("name", sa.String(300), nullable=False),
        sa.Column("name_key", sa.String(300), nullable=False),
        sa.Column("first_seen_at", TS, nullable=False),
        sa.Column("last_seen_at", TS, nullable=False),
        sa.Column("sightings", sa.Integer, nullable=False, server_default="0"),
        sa.Column("present_seconds", sa.Integer, nullable=False, server_default="0"),
        sa.Column("intervals", JSONB, nullable=False, server_default=sa.text("'[]'::jsonb")),
        sa.Column("ignored", sa.Boolean, nullable=False, server_default=sa.text("false")),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
    )
    op.create_index("uq_attendance_participants_name", "attendance_participants", ["session_id", "name_key"], unique=True)

    op.create_table(
        "attendance_records",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("session_id", UUID, sa.ForeignKey("attendance_sessions.id", ondelete="CASCADE"), nullable=False),
        sa.Column("student_id", UUID, sa.ForeignKey("students.id", ondelete="CASCADE"), nullable=False),
        sa.Column("participant_id", UUID, sa.ForeignKey("attendance_participants.id", ondelete="SET NULL")),
        sa.Column("extra_participant_ids", JSONB, nullable=False, server_default=sa.text("'[]'::jsonb")),
        sa.Column("status", sa.String(16), nullable=False),
        sa.Column("confidence", sa.Integer, nullable=False, server_default="0"),
        sa.Column("match_source", sa.String(16), nullable=False, server_default="none"),
        sa.Column("reason", sa.String(300)),
        sa.Column("join_time", TS),
        sa.Column("leave_time", TS),
        sa.Column("duration_seconds", sa.Integer),
        sa.Column("manual", sa.Boolean, nullable=False, server_default=sa.text("false")),
        sa.Column("updated_by", UUID),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("status IN ('present', 'absent', 'needs_review')", name="ck_attendance_records_status"),
    )
    op.create_index("uq_attendance_records_student", "attendance_records", ["session_id", "student_id"], unique=True)


def downgrade() -> None:
    raise NotImplementedError(
        "0006_attendance is not downgraded automatically: it would delete every roster and attendance "
        "record. Back up and drop the six tables by hand if that is really intended."
    )

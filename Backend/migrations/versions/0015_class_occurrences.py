"""Add durable class-occurrence lifecycle state.

Revision ID: 0015_class_occurrences
Revises: 0014_google_sheets_sync
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0015_class_occurrences"
down_revision = "0014_google_sheets_sync"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "class_occurrences",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("class_plan_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("class_plans.id", ondelete="CASCADE"), nullable=False, unique=True),
        sa.Column("group_name", sa.String(length=100), nullable=False),
        sa.Column("session_date", sa.Date(), nullable=False),
        sa.Column("scheduled_start", sa.DateTime(timezone=True)),
        sa.Column("scheduled_end", sa.DateTime(timezone=True)),
        sa.Column("zoom_account_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("zoom_accounts.id", ondelete="SET NULL")),
        sa.Column("zoom_meeting_url", sa.Text()),
        sa.Column("actual_start", sa.DateTime(timezone=True)),
        sa.Column("actual_end", sa.DateTime(timezone=True)),
        sa.Column("lms_session_url", sa.Text()),
        sa.Column("lms_session_id", sa.String(length=200)),
        sa.Column("attendance_session_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("attendance_sessions.id", ondelete="SET NULL")),
        sa.Column("state", sa.String(length=32), nullable=False, server_default="scheduled"),
        sa.Column("zoom_recording_url", sa.Text()),
        sa.Column("drive_recording_url", sa.Text()),
        sa.Column("recording_found_at", sa.DateTime(timezone=True)),
        sa.Column("retry_state", postgresql.JSONB(astext_type=sa.Text()), nullable=False, server_default=sa.text("'{}'::jsonb")),
        sa.Column("last_error", sa.String(length=500)),
        sa.Column("next_retry_at", sa.DateTime(timezone=True)),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("state IN ('scheduled', 'live', 'attendancePending', 'attendanceSubmitted', 'correctionPending', 'attendanceFinalized', 'lmsSessionCompleted', 'recordingPending', 'zoomLinkFound', 'zoomLinkAttached', 'waitingForDrive', 'driveLinkAttached', 'conflict', 'failed', 'skipped')", name="ck_class_occurrences_state"),
    )
    op.create_index("ix_class_occurrences_group_date", "class_occurrences", ["group_name", "session_date"])
    op.create_index("ix_class_occurrences_state_retry", "class_occurrences", ["state", "next_retry_at"])
    op.add_column("jobs", sa.Column("occurrence_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("class_occurrences.id", ondelete="SET NULL")))
    op.create_index("ix_jobs_occurrence_id", "jobs", ["occurrence_id"])


def downgrade() -> None:
    op.drop_index("ix_jobs_occurrence_id", table_name="jobs")
    op.drop_column("jobs", "occurrence_id")
    op.drop_index("ix_class_occurrences_state_retry", table_name="class_occurrences")
    op.drop_index("ix_class_occurrences_group_date", table_name="class_occurrences")
    op.drop_table("class_occurrences")

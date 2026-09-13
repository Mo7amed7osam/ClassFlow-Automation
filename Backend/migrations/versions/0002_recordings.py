"""Recordings: the recording metadata n8n syncs from the recordings sheet.

Additive only: one new table and its indexes; nothing existing is touched.

Revision ID: 0002_recordings
Revises: 0001_initial
Create Date: 2026-09-13
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0002_recordings"
down_revision = "0001_initial"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "recordings",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("group_name", sa.String(100), nullable=False),
        sa.Column("session_date", sa.Date, nullable=False),
        sa.Column("start_time", sa.String(5)),
        sa.Column("file_name", sa.String(255)),
        sa.Column("record_type", sa.String(50)),
        sa.Column("drive_link", sa.Text),
        sa.Column("zoom_link", sa.Text),
        sa.Column("source", sa.String(16), nullable=False, server_default="zoom"),
        sa.Column("lms_status", sa.String(32), nullable=False, server_default="pending"),
        sa.Column("lms_updated_at", sa.DateTime(timezone=True)),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_recordings_group_name", "recordings", ["group_name"])
    op.create_index("ix_recordings_session_date", "recordings", ["session_date"])
    # One row per session: group + date + start time, where "no start time" counts as one slot
    # (a plain unique constraint would let any number of NULL start times through).
    op.create_index(
        "uq_recordings_session",
        "recordings",
        ["group_name", "session_date", sa.text("coalesce(start_time, '')")],
        unique=True,
    )


def downgrade() -> None:
    # Deliberately not automatic: going back would delete every synced recording.
    raise NotImplementedError(
        "0002_recordings is not downgraded automatically; dropping the table deletes its data. "
        "Back up and drop it by hand if that is really intended."
    )

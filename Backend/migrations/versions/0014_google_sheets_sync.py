"""First-party Google Sheets recording synchronization.

The application, not n8n, owns the OAuth refresh token and the durable per-row processing state.
The sheet remains read-only throughout this flow.

Revision ID: 0014_google_sheets_sync
Revises: 0013_zoom_passwords
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0014_google_sheets_sync"
down_revision = "0013_zoom_passwords"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "google_sheets_connections",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("spreadsheet_id", sa.String(length=200), nullable=False),
        sa.Column("refresh_token_encrypted", sa.Text(), nullable=False),
        sa.Column("google_email", sa.String(length=320)),
        sa.Column("connected_by", postgresql.UUID(as_uuid=True), sa.ForeignKey("users.id", ondelete="SET NULL")),
        sa.Column("connected_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )
    op.create_table(
        "google_sheets_sync_records",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("connection_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("google_sheets_connections.id", ondelete="CASCADE"), nullable=False),
        sa.Column("row_key", sa.String(length=300), nullable=False),
        sa.Column("group_name", sa.String(length=100), nullable=False),
        sa.Column("session_date", sa.Date(), nullable=False),
        sa.Column("drive_link", sa.Text(), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False, server_default="pending"),
        sa.Column("detail", sa.String(length=500)),
        sa.Column("recording_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("recordings.id", ondelete="SET NULL")),
        sa.Column("processed_at", sa.DateTime(timezone=True)),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("status IN ('pending', 'processing', 'attached', 'conflict', 'failed')", name="ck_google_sheet_sync_status"),
        sa.UniqueConstraint("connection_id", "row_key", name="uq_google_sheet_sync_row"),
    )
    op.create_index("ix_google_sheet_sync_class", "google_sheets_sync_records", ["group_name", "session_date"])


def downgrade() -> None:
    op.drop_index("ix_google_sheet_sync_class", table_name="google_sheets_sync_records")
    op.drop_table("google_sheets_sync_records")
    op.drop_table("google_sheets_connections")

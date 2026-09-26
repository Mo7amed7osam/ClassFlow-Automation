"""Link sheet rows and occurrences to their durable recording-attach jobs.

Revision ID: 0016_recording_attachment_jobs
Revises: 0015_class_occurrences
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0016_recording_attachment_jobs"
down_revision = "0015_class_occurrences"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("class_occurrences", sa.Column("lms_account_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("lms_accounts.id", ondelete="SET NULL")))
    op.add_column("google_sheets_sync_records", sa.Column("occurrence_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("class_occurrences.id", ondelete="SET NULL")))
    op.add_column("google_sheets_sync_records", sa.Column("job_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("jobs.id", ondelete="SET NULL")))
    op.create_index("ix_google_sheet_sync_job", "google_sheets_sync_records", ["job_id"])


def downgrade() -> None:
    op.drop_index("ix_google_sheet_sync_job", table_name="google_sheets_sync_records")
    op.drop_column("google_sheets_sync_records", "job_id")
    op.drop_column("google_sheets_sync_records", "occurrence_id")
    op.drop_column("class_occurrences", "lms_account_id")

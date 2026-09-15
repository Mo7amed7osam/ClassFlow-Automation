"""Recording operations: link jobs to recordings, and keep an audit log of dashboard actions.

Additive only: one nullable column on jobs (existing jobs keep NULL and behave as before) and
one new table.

Revision ID: 0004_recording_operations
Revises: 0003_admin_sessions
Create Date: 2026-09-13
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0004_recording_operations"
down_revision = "0003_admin_sessions"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column(
        "jobs",
        sa.Column("recording_id", postgresql.UUID(as_uuid=True),
                  sa.ForeignKey("recordings.id", ondelete="SET NULL", name="fk_jobs_recording_id"), nullable=True),
    )
    op.create_index("ix_jobs_recording_id", "jobs", ["recording_id"])

    op.create_table(
        "admin_audit_log",
        sa.Column("id", sa.BigInteger, primary_key=True, autoincrement=True),
        sa.Column("username", sa.String(50), nullable=False),
        sa.Column("action", sa.String(40), nullable=False),
        sa.Column("recording_id", postgresql.UUID(as_uuid=True)),
        sa.Column("details", postgresql.JSONB),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_admin_audit_log_recording", "admin_audit_log", ["recording_id", "id"])


def downgrade() -> None:
    # Consistent with the earlier migrations: nothing is dropped automatically.
    raise NotImplementedError(
        "0004_recording_operations is not downgraded automatically: it would drop the audit log and "
        "the job-to-recording links. Back up and drop them by hand if that is really intended."
    )

"""Dashboard admin sessions.

Additive only: one new table; nothing existing is touched.

Revision ID: 0003_admin_sessions
Revises: 0002_recordings
Create Date: 2026-09-13
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0003_admin_sessions"
down_revision = "0002_recordings"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "admin_sessions",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("token_hash", sa.String(64), nullable=False, unique=True),
        sa.Column("username", sa.String(50), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
    )
    op.create_index("ix_admin_sessions_expires_at", "admin_sessions", ["expires_at"])


def downgrade() -> None:
    # Consistent with the earlier migrations: nothing is dropped automatically.
    raise NotImplementedError(
        "0003_admin_sessions is not downgraded automatically. Drop admin_sessions by hand if intended "
        "(it only holds logins; everyone would have to sign in again)."
    )

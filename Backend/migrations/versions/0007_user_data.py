"""User data on the server: each user's LMS accounts (password AES-GCM encrypted) and shared settings.

Additive only: two new tables; nothing existing is touched.

Revision ID: 0007_user_data
Revises: 0006_attendance
Create Date: 2026-09-14
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0007_user_data"
down_revision = "0006_attendance"
branch_labels = None
depends_on = None

UUID = postgresql.UUID(as_uuid=True)
JSONB = postgresql.JSONB
TS = sa.DateTime(timezone=True)


def upgrade() -> None:
    op.create_table(
        "lms_accounts",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("user_id", UUID, sa.ForeignKey("users.id", ondelete="CASCADE"), nullable=False),
        sa.Column("label", sa.String(100), nullable=False),
        sa.Column("email", sa.String(320), nullable=False),
        sa.Column("role", sa.String(16), nullable=False),
        sa.Column("password_encrypted", sa.Text, nullable=False),
        sa.Column("active", sa.Boolean, nullable=False, server_default=sa.text("false")),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("role IN ('admin', 'coordinator')", name="ck_lms_accounts_role"),
    )
    op.create_index("ix_lms_accounts_user_id", "lms_accounts", ["user_id"])
    op.create_index("uq_lms_accounts_user_email", "lms_accounts", ["user_id", sa.text("lower(email)")], unique=True)
    op.create_index("uq_lms_accounts_one_active", "lms_accounts", ["user_id"], unique=True, postgresql_where=sa.text("active"))

    op.create_table(
        "app_settings",
        sa.Column("key", sa.String(64), primary_key=True),
        sa.Column("value", JSONB, nullable=False),
        sa.Column("updated_by", UUID, sa.ForeignKey("users.id", ondelete="SET NULL")),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
    )


def downgrade() -> None:
    op.drop_table("app_settings")
    op.drop_index("uq_lms_accounts_one_active", table_name="lms_accounts")
    op.drop_index("uq_lms_accounts_user_email", table_name="lms_accounts")
    op.drop_index("ix_lms_accounts_user_id", table_name="lms_accounts")
    op.drop_table("lms_accounts")

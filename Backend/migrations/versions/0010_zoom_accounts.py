"""The Zoom accounts a user opens classes with: zoom_accounts, and the delegation that names one.

Additive only: one new table and one new nullable column. No password is stored here; the Zoom
sign-in stays in the Zoom app or a browser profile on the PC.

Revision ID: 0010_zoom_accounts
Revises: 0009_delegated_runs
Create Date: 2026-09-19
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0010_zoom_accounts"
down_revision = "0009_delegated_runs"
branch_labels = None
depends_on = None

UUID = postgresql.UUID(as_uuid=True)
TS = sa.DateTime(timezone=True)


def upgrade() -> None:
    op.create_table(
        "zoom_accounts",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("user_id", UUID, sa.ForeignKey("users.id", ondelete="CASCADE"), nullable=False),
        sa.Column("account_id", sa.String(100), nullable=False),
        sa.Column("label", sa.String(100), nullable=False),
        sa.Column("zoom_email", sa.String(320)),
        sa.Column("group_name", sa.String(100)),
        sa.Column("default_meeting_url", sa.Text),
        sa.Column("preferred_engine", sa.String(8)),
        sa.Column("active", sa.Boolean, nullable=False, server_default=sa.text("false")),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint(
            "preferred_engine IS NULL OR preferred_engine IN ('desktop', 'web')", name="ck_zoom_accounts_engine"
        ),
    )
    op.create_index("ix_zoom_accounts_user_id", "zoom_accounts", ["user_id"])
    op.execute("CREATE UNIQUE INDEX uq_zoom_accounts_user_account ON zoom_accounts (user_id, lower(account_id))")

    op.add_column("run_delegations", sa.Column("zoom_account_id", UUID))
    op.create_foreign_key(
        "fk_run_delegations_zoom_account", "run_delegations", "zoom_accounts",
        ["zoom_account_id"], ["id"], ondelete="SET NULL",
    )


def downgrade() -> None:
    op.drop_constraint("fk_run_delegations_zoom_account", "run_delegations", type_="foreignkey")
    op.drop_column("run_delegations", "zoom_account_id")
    op.execute("DROP INDEX IF EXISTS uq_zoom_accounts_user_account")
    op.drop_index("ix_zoom_accounts_user_id", table_name="zoom_accounts")
    op.drop_table("zoom_accounts")

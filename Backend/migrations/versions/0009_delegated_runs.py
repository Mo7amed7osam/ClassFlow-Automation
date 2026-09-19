"""The coordinators whose classes the admin's PC runs, and those classes: run_delegations, class_plans.

Additive only: two new tables; nothing existing is touched.

Revision ID: 0009_delegated_runs
Revises: 0008_device_activity
Create Date: 2026-09-19
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0009_delegated_runs"
down_revision = "0008_device_activity"
branch_labels = None
depends_on = None

UUID = postgresql.UUID(as_uuid=True)
TS = sa.DateTime(timezone=True)


def upgrade() -> None:
    op.create_table(
        "run_delegations",
        sa.Column("coordinator_id", UUID, sa.ForeignKey("users.id", ondelete="CASCADE"), primary_key=True),
        sa.Column("enabled", sa.Boolean, nullable=False, server_default=sa.text("true")),
        sa.Column("lms_account_id", UUID, sa.ForeignKey("lms_accounts.id", ondelete="SET NULL")),
        sa.Column("zoom_account", sa.String(100)),
        sa.Column("created_by", UUID, sa.ForeignKey("users.id", ondelete="SET NULL")),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
    )

    op.create_table(
        "class_plans",
        sa.Column("id", UUID, primary_key=True),
        sa.Column("coordinator_id", UUID, sa.ForeignKey("users.id", ondelete="CASCADE"), nullable=False),
        sa.Column("group_name", sa.String(100), nullable=False),
        sa.Column("session_date", sa.Date, nullable=False),
        sa.Column("start_time", sa.String(5)),
        sa.Column("title", sa.String(200)),
        sa.Column("meeting_url", sa.Text),
        sa.Column("zoom_account", sa.String(100)),
        sa.Column("preferred_engine", sa.String(8)),
        sa.Column("source", sa.String(16), nullable=False, server_default="lms"),
        sa.Column("status", sa.String(16), nullable=False, server_default="planned"),
        sa.Column("note", sa.String(300)),
        sa.Column("imported_at", TS),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("source IN ('lms', 'manual')", name="ck_class_plans_source"),
        sa.CheckConstraint("status IN ('planned', 'skipped', 'opened', 'done', 'failed')", name="ck_class_plans_status"),
        sa.CheckConstraint("preferred_engine IS NULL OR preferred_engine IN ('desktop', 'web')", name="ck_class_plans_engine"),
    )
    op.create_index("ix_class_plans_coordinator_id", "class_plans", ["coordinator_id"])
    op.create_index("ix_class_plans_date", "class_plans", ["session_date", "start_time"])
    op.execute(
        "CREATE UNIQUE INDEX uq_class_plans_class ON class_plans "
        "(coordinator_id, group_name, session_date, coalesce(start_time, ''))"
    )


def downgrade() -> None:
    op.execute("DROP INDEX IF EXISTS uq_class_plans_class")
    op.drop_index("ix_class_plans_date", table_name="class_plans")
    op.drop_index("ix_class_plans_coordinator_id", table_name="class_plans")
    op.drop_table("class_plans")
    op.drop_table("run_delegations")

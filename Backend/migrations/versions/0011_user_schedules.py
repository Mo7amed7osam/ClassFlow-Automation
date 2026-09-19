"""The classes a person's own PC opens by itself, kept against their account: user_schedules.

Additive only: one new table. It is what lets somebody sign in on a new PC - a cloud one, say - and
find their classes there instead of setting them up again.

Revision ID: 0011_user_schedules
Revises: 0010_zoom_accounts
Create Date: 2026-09-19
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0011_user_schedules"
down_revision = "0010_zoom_accounts"
branch_labels = None
depends_on = None

UUID = postgresql.UUID(as_uuid=True)
JSONB = postgresql.JSONB
TS = sa.DateTime(timezone=True)


def upgrade() -> None:
    op.create_table(
        "user_schedules",
        sa.Column("user_id", UUID, sa.ForeignKey("users.id", ondelete="CASCADE"), primary_key=True),
        sa.Column("schedules", JSONB, nullable=False, server_default=sa.text("'[]'::jsonb")),
        sa.Column("count", sa.Integer, nullable=False, server_default="0"),
        sa.Column("device_name", sa.String(100)),
        sa.Column("updated_at", TS, nullable=False, server_default=sa.func.now()),
    )


def downgrade() -> None:
    op.drop_table("user_schedules")

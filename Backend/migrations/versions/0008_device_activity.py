"""What each PC did by itself, reported to the server: device_activity.

Additive only: one new table; nothing existing is touched.

Revision ID: 0008_device_activity
Revises: 0007_user_data
Create Date: 2026-09-16
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0008_device_activity"
down_revision = "0007_user_data"
branch_labels = None
depends_on = None

UUID = postgresql.UUID(as_uuid=True)
JSONB = postgresql.JSONB
TS = sa.DateTime(timezone=True)


def upgrade() -> None:
    op.create_table(
        "device_activity",
        sa.Column("id", sa.BigInteger, primary_key=True, autoincrement=True),
        sa.Column("device_id", UUID, sa.ForeignKey("devices.id", ondelete="CASCADE"), nullable=False),
        sa.Column("client_event_id", sa.String(200), nullable=False, unique=True),
        sa.Column("happened_at", TS, nullable=False),
        sa.Column("kind", sa.String(40), nullable=False),
        sa.Column("outcome", sa.String(16), nullable=False),
        sa.Column("group_name", sa.String(100)),
        sa.Column("session_date", sa.Date),
        sa.Column("summary", sa.String(500), nullable=False, server_default=sa.text("''")),
        sa.Column("detail", JSONB),
        sa.Column("created_at", TS, nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("outcome IN ('done', 'failed', 'skipped')", name="ck_device_activity_outcome"),
    )
    op.create_index("ix_device_activity_device", "device_activity", ["device_id", "happened_at"])
    op.create_index("ix_device_activity_class", "device_activity", ["group_name", "session_date"])


def downgrade() -> None:
    op.drop_index("ix_device_activity_class", table_name="device_activity")
    op.drop_index("ix_device_activity_device", table_name="device_activity")
    op.drop_table("device_activity")

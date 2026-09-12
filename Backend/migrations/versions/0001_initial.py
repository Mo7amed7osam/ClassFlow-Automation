"""Initial schema: devices, enrollment_tokens, jobs, job_events.

Revision ID: 0001_initial
Revises:
Create Date: 2026-09-12
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0001_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "devices",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("installation_id", postgresql.UUID(as_uuid=True), nullable=False, unique=True),
        sa.Column("name", sa.String(100), nullable=False),
        sa.Column("version", sa.String(50), nullable=False),
        sa.Column("status", sa.String(16), nullable=False, server_default="offline"),
        sa.Column("agent_state", sa.String(16)),
        sa.Column("capabilities", postgresql.JSONB, nullable=False, server_default=sa.text("'[]'::jsonb")),
        sa.Column("token_hash", sa.String(64)),
        sa.Column("last_heartbeat", sa.DateTime(timezone=True)),
        sa.Column("last_assigned_at", sa.DateTime(timezone=True)),
        sa.Column("connected_at", sa.DateTime(timezone=True)),
        sa.Column("revoked_at", sa.DateTime(timezone=True)),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("status IN ('online', 'offline')", name="ck_devices_status"),
        sa.CheckConstraint("agent_state IS NULL OR agent_state IN ('idle', 'busy')", name="ck_devices_agent_state"),
    )

    op.create_table(
        "enrollment_tokens",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("token_hash", sa.String(64), nullable=False, unique=True),
        sa.Column("label", sa.String(100), nullable=False),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("used_at", sa.DateTime(timezone=True)),
        sa.Column("used_by_device_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("devices.id")),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )

    op.create_table(
        "jobs",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("type", sa.String(64), nullable=False),
        sa.Column("device_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("devices.id")),
        sa.Column("payload", postgresql.JSONB, nullable=False),
        sa.Column("status", sa.String(16), nullable=False, server_default="queued"),
        sa.Column("result", postgresql.JSONB),
        sa.Column("error", postgresql.JSONB),
        sa.Column("idempotency_key", sa.String(200), unique=True),
        sa.Column("attempts", sa.Integer, nullable=False, server_default="0"),
        sa.Column("max_attempts", sa.Integer, nullable=False, server_default="3"),
        sa.Column("available_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("assigned_at", sa.DateTime(timezone=True)),
        sa.Column("accepted_at", sa.DateTime(timezone=True)),
        sa.Column("started_at", sa.DateTime(timezone=True)),
        sa.Column("finished_at", sa.DateTime(timezone=True)),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint(
            "status IN ('queued', 'assigned', 'running', 'succeeded', 'failed', 'cancelled')", name="ck_jobs_status"
        ),
    )
    op.create_index("ix_jobs_dispatch", "jobs", ["status", "available_at", "created_at"])
    op.create_index("ix_jobs_device_status", "jobs", ["device_id", "status"])

    op.create_table(
        "job_events",
        sa.Column("id", sa.BigInteger, primary_key=True, autoincrement=True),
        sa.Column("job_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("jobs.id"), nullable=False),
        sa.Column("device_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("devices.id")),
        sa.Column("event_type", sa.String(40), nullable=False),
        sa.Column("payload", postgresql.JSONB),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_job_events_job", "job_events", ["job_id", "id"])


def downgrade() -> None:
    # Deliberately not automatic: going back would drop every device, job and event.
    raise NotImplementedError(
        "0001_initial is not downgraded automatically; dropping these tables deletes all data. "
        "Back up and drop them by hand if that is really intended."
    )

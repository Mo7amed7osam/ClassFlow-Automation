"""Users (one admin, coordinators), groups and which coordinator sees which group.

Additive only. New tables users, groups, user_groups; a user_id column on admin_sessions and on
admin_audit_log. groups is filled from the group names the recordings already carry.

Sessions created before this migration have no user and stop working: everyone signs in again.

Revision ID: 0005_users_and_groups
Revises: 0004_recording_operations
Create Date: 2026-09-13
"""

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision = "0005_users_and_groups"
down_revision = "0004_recording_operations"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "users",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("username", sa.String(50), nullable=False, unique=True),
        sa.Column("display_name", sa.String(100), nullable=False),
        sa.Column("password_hash", sa.String(200), nullable=False),
        sa.Column("role", sa.String(16), nullable=False),
        sa.Column("status", sa.String(16), nullable=False),
        sa.Column("approved_at", sa.DateTime(timezone=True)),
        sa.Column("approved_by", postgresql.UUID(as_uuid=True), sa.ForeignKey("users.id", ondelete="SET NULL")),
        sa.Column("last_login_at", sa.DateTime(timezone=True)),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
        sa.CheckConstraint("role IN ('admin', 'coordinator')", name="ck_users_role"),
        sa.CheckConstraint("status IN ('pending', 'active', 'rejected', 'disabled')", name="ck_users_status"),
    )
    # One admin, enforced by the database rather than by the application alone.
    op.create_index("uq_users_single_admin", "users", ["role"], unique=True, postgresql_where=sa.text("role = 'admin'"))

    op.create_table(
        "groups",
        sa.Column("id", postgresql.UUID(as_uuid=True), primary_key=True),
        sa.Column("name", sa.String(100), nullable=False, unique=True),
        sa.Column("display_name", sa.String(100)),
        sa.Column("archived_at", sa.DateTime(timezone=True)),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )
    op.execute(
        "INSERT INTO groups (id, name, created_at) "
        "SELECT gen_random_uuid(), group_name, min(created_at) FROM recordings GROUP BY group_name"
    )

    op.create_table(
        "user_groups",
        sa.Column("user_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("users.id", ondelete="CASCADE"), primary_key=True),
        sa.Column("group_id", postgresql.UUID(as_uuid=True), sa.ForeignKey("groups.id", ondelete="CASCADE"), primary_key=True),
        sa.Column("assigned_by", postgresql.UUID(as_uuid=True), sa.ForeignKey("users.id", ondelete="SET NULL")),
        sa.Column("assigned_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.func.now()),
    )
    op.create_index("ix_user_groups_group", "user_groups", ["group_id"])

    op.add_column("admin_sessions", sa.Column("user_id", postgresql.UUID(as_uuid=True),
                                              sa.ForeignKey("users.id", ondelete="CASCADE", name="fk_admin_sessions_user_id")))
    op.create_index("ix_admin_sessions_user_id", "admin_sessions", ["user_id"])
    op.add_column("admin_audit_log", sa.Column("user_id", postgresql.UUID(as_uuid=True)))


def downgrade() -> None:
    raise NotImplementedError(
        "0005_users_and_groups is not downgraded automatically: it would delete every account and group "
        "assignment. Back up and drop them by hand if that is really intended."
    )

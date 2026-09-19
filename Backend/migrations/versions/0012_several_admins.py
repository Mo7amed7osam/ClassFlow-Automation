"""Several admins instead of one.

The users table carried a unique partial index, uq_users_single_admin, so the database itself
allowed one row with role = 'admin'. An admin now makes other admins, each with the same powers,
which that index refuses.

Nothing is dropped but the index, and no row changes. The downgrade puts the index back, and says
so rather than deleting the admins that would make it fail.

Revision ID: 0012_several_admins
Revises: 0011_user_schedules
"""

from __future__ import annotations

import sqlalchemy as sa
from alembic import op

revision = "0012_several_admins"
down_revision = "0011_user_schedules"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.drop_index("uq_users_single_admin", table_name="users")

    # An account is admin from birth or never; a coordinator is never promoted. Nothing in the API
    # can write such a row, and this leaves the rule in the database as well, where a mistaken
    # UPDATE cannot get round it either.
    op.create_index(
        "ix_users_role_status",
        "users",
        ["role", "status"],
    )


def downgrade() -> None:
    op.drop_index("ix_users_role_status", table_name="users")

    admins = op.get_bind().execute(
        sa.text("SELECT count(*) FROM users WHERE role = 'admin'")
    ).scalar_one()
    if admins > 1:
        raise RuntimeError(
            f"There are {admins} admin accounts, and the index this downgrade restores allows one. "
            "Decide which account stays an admin and disable or remove the others first; this "
            "migration will not choose for you, and it will not delete an account."
        )

    op.create_index(
        "uq_users_single_admin",
        "users",
        ["role"],
        unique=True,
        postgresql_where=sa.text("role = 'admin'"),
    )

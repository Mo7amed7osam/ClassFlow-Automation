"""The Zoom password each account signs its browser profile in with: zoom_accounts.password_encrypted.

Additive only: one new nullable column. It is AES-GCM encrypted with CENTRAL_SECRETS_KEY, exactly
as an LMS password is, so a copy of the database alone does not give the passwords.

A browser profile nobody has signed in joins as a guest, and a guest cannot admit anybody. Keeping
the password here is what lets a new PC - or the second profile of two classes at once - sign
itself in instead of waiting for a person.

Revision ID: 0013_zoom_passwords
Revises: 0012_several_admins
Create Date: 2026-09-19
"""

import sqlalchemy as sa
from alembic import op

revision = "0013_zoom_passwords"
down_revision = "0012_several_admins"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.add_column("zoom_accounts", sa.Column("password_encrypted", sa.Text))


def downgrade() -> None:
    op.drop_column("zoom_accounts", "password_encrypted")

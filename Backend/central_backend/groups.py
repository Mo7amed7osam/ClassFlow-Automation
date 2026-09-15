"""Groups: the class groups recordings belong to, and which coordinator sees which."""

from __future__ import annotations

import uuid
from datetime import datetime
from typing import Any

from sqlalchemy import select
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.ext.asyncio import AsyncSession

from .models import Group, UserGroup
from .observability import emit


async def ensure_group(session: AsyncSession, name: str, now: datetime) -> None:
    """Register a group the first time a recording names it (n8n's sync, or an edit that moves a
    recording). Nothing happens for a group that already exists."""
    inserted = (
        await session.execute(
            insert(Group).values(id=uuid.uuid4(), name=name, created_at=now)
            .on_conflict_do_nothing(index_elements=[Group.name]).returning(Group.id)
        )
    ).scalar_one_or_none()
    if inserted is not None:
        emit("group.registered", group=name, groupId=str(inserted))


async def assigned_groups(session: AsyncSession, user_id: uuid.UUID) -> list[Group]:
    """A coordinator's groups, archived ones left out, by name."""
    return list(
        (
            await session.execute(
                select(Group).join(UserGroup, UserGroup.group_id == Group.id)
                .where(UserGroup.user_id == user_id, Group.archived_at.is_(None)).order_by(Group.name)
            )
        ).scalars()
    )


def group_view(group: Group) -> dict[str, Any]:
    return {"id": str(group.id), "name": group.name, "displayName": group.display_name,
            "archived": group.archived_at is not None}

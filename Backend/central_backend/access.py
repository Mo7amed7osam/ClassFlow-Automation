"""What a signed-in user may see. The one rule every dashboard query goes through:

    admin        -> everything
    coordinator  -> only recordings, groups and jobs of the groups assigned to them
                    (none assigned: nothing at all)

Endpoints ask for a Viewer and apply viewer.recording_filter() to their query, or viewer.can_see()
to one recording; a recording outside the viewer's groups is answered as if it did not exist.
"""

from __future__ import annotations

from dataclasses import dataclass

from fastapi import Depends, Request
from sqlalchemy import ColumnElement, true

from .auth import CurrentUser, current_user
from .groups import assigned_groups
from .models import Recording


@dataclass(frozen=True)
class Viewer:
    user: CurrentUser
    groups: frozenset[str] | None       # None: no limit (the admin)

    @property
    def is_admin(self) -> bool:
        return self.user.is_admin

    def can_see(self, group_name: str) -> bool:
        return self.groups is None or group_name in self.groups

    def recording_filter(self) -> ColumnElement[bool]:
        if self.groups is None:
            return true()
        return Recording.group_name.in_(sorted(self.groups))


async def current_viewer(request: Request, user: CurrentUser = Depends(current_user)) -> Viewer:
    if user.is_admin:
        return Viewer(user, None)
    async with request.app.state.sessionmaker() as session:
        names = frozenset(group.name for group in await assigned_groups(session, user.id))
    return Viewer(user, names)

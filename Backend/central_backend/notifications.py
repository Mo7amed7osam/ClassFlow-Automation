"""Saying out loud what needs somebody, instead of waiting to be noticed.

A class run by a worker has nobody watching it: the meeting opens, the LMS steps run and, when one
of them fails for good, the only sign is a red mark on a page somebody has to open. This posts that
to an n8n webhook, and n8n sends the e-mail - so no mailbox password is ever kept here, and the same
workflow serves the Windows app, which posts the same shape (see ClassNotifier.cs).

    GET  /api/v1/dashboard/notifications        whether notices are on, and whether a webhook is set
    PUT  /api/v1/dashboard/notifications        turn them on or off, set the webhook
    POST /api/v1/dashboard/notifications/test   send one now, to see that it arrives

The webhook address is a way in to somebody's workflow, so it is kept like a password: written once,
never sent back to a browser, and never in an answer or a log. The admin sets it; everybody else
cannot even read whether it exists.

Nothing here can affect a class. A webhook that is down, slow, or never set up leaves the class
exactly as it was: a notice is tried three times, a little further apart each time, and then written
off with a line in the log.
"""

from __future__ import annotations

import asyncio
import json
import logging
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any

from fastapi import APIRouter, Depends, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from .api import ApiError, _json
from .auth import CurrentUser, current_user, require_admin, require_dashboard_header
from .models import AppSetting
from .observability import emit

router = APIRouter()

SETTING_KEY = "notifications"
"""Where the webhook and the switch are kept. Deliberately not in user_data.SETTING_KEYS: the
generic settings endpoint gives a value back, and this one holds an address that must not travel."""

TRY_AGAIN_AFTER = (5.0, 20.0)
"""Between tries, in seconds. The same two the Windows app waits."""

TIMEOUT = 20.0


def _no_store(body: Any, status: int = 200) -> JSONResponse:
    return JSONResponse(_json(body), status_code=status, headers={"Cache-Control": "no-store"})


@dataclass(frozen=True)
class Notice:
    """One thing worth telling somebody about, in the shape n8n already receives from the app."""

    kind: str
    title: str
    message: str
    group: str | None = None
    coordinator: str | None = None
    date: str | None = None
    start: str | None = None
    step: str | None = None
    attempts: int | None = None

    def body(self, label: str, at: datetime) -> bytes:
        return json.dumps({
            "kind": self.kind,
            "title": self.title,
            "message": self.message,
            "group": self.group,
            "coordinator": self.coordinator,
            "date": self.date,
            "start": self.start,
            "step": self.step,
            "attempts": self.attempts,
            # What the Windows app calls "pc". A server is not a PC, but the field is what n8n reads,
            # so the name of whoever is speaking goes in it either way.
            "pc": label,
            "at": at.isoformat(),
        }, ensure_ascii=False).encode()


@dataclass
class Notifier:
    """Reads the setting, posts the notice, and never lets either failure reach a class."""

    sessionmaker: async_sessionmaker[AsyncSession]
    clock: Any
    label: str = "server"
    # A notice is sent beside the request that caused it, so the tasks are held until they finish
    # (nothing else keeps a reference, and a task nobody holds can be collected mid-flight).
    running: set[asyncio.Task] = field(default_factory=set)
    last_sent_at: datetime | None = None
    last_error: str | None = None

    async def read(self) -> dict[str, Any]:
        async with self.sessionmaker() as session:
            row = await session.get(AppSetting, SETTING_KEY)
        value = row.value if row and isinstance(row.value, dict) else {}
        return {
            "url": (value.get("url") or "").strip(),
            "enabled": value.get("enabled") is not False,
            "label": (value.get("label") or self.label).strip() or self.label,
            "updatedAt": row.updated_at if row else None,
        }

    async def send(self, notice: Notice) -> bool:
        """True when the webhook took it. False when there is nowhere to send it, or every try failed."""
        try:
            settings = await self.read()
        except Exception as problem:  # noqa: BLE001 - a notice never breaks what it is about
            emit("notify.unreadable", level=logging.WARNING, reason=str(problem))
            return False
        if not settings["enabled"] or not settings["url"]:
            return False

        body = notice.body(settings["label"], self.clock())
        for attempt in range(len(TRY_AGAIN_AFTER) + 1):
            # _post answers rather than raises, but it is the one call here that reaches the world:
            # whatever it does, a class must not hear about it.
            try:
                problem = await _post(settings["url"], body)
            except asyncio.CancelledError:
                raise
            except Exception as thrown:  # noqa: BLE001
                problem = f"{type(thrown).__name__}: {thrown}"
            if problem is None:
                self.last_sent_at, self.last_error = self.clock(), None
                emit("notify.sent", kind=notice.kind, group=notice.group)
                return True
            self.last_error = problem
            if attempt >= len(TRY_AGAIN_AFTER):
                emit("notify.gave_up", level=logging.WARNING, kind=notice.kind, group=notice.group,
                     tries=attempt + 1, reason=problem)
                return False
            await asyncio.sleep(TRY_AGAIN_AFTER[attempt])
        return False

    def send_soon(self, notice: Notice) -> None:
        """Sends beside whatever is happening. The caller is never delayed and never fails for this."""
        try:
            task = asyncio.create_task(self.send(notice))
        except RuntimeError:            # no loop running: a test, or a CLI command
            return
        self.running.add(task)
        task.add_done_callback(self.running.discard)


async def _post(url: str, body: bytes) -> str | None:
    """None when the webhook took it, else why it did not. Never raises."""
    request = urllib.request.Request(url, data=body, method="POST",
                                     headers={"Content-Type": "application/json"})

    def call() -> str | None:
        try:
            with urllib.request.urlopen(request, timeout=TIMEOUT) as answer:  # noqa: S310 - the admin's own webhook
                return None if 200 <= answer.status < 300 else f"the webhook answered {answer.status}"
        except urllib.error.HTTPError as problem:
            # 404 from n8n means the workflow is not listening: a test URL only answers while the
            # editor is open, and a production URL needs the workflow active.
            if problem.code == 404:
                return "the webhook answered 404: the n8n workflow is not active (or the address is a test one)"
            return f"the webhook answered {problem.code}"
        except Exception as problem:  # noqa: BLE001 - whatever the network did, the class is unaffected
            return f"{type(problem).__name__}: {problem}"

    try:
        return await asyncio.to_thread(call)
    except Exception as problem:  # noqa: BLE001
        return f"{type(problem).__name__}: {problem}"


# --------------------------------------------------------------------------- what a job's end means


def _class_of(job: Any) -> dict[str, Any]:
    payload = job.payload or {}
    return {"group": payload.get("group"), "date": payload.get("date"), "start": payload.get("startTime")}


STEP_NAMES = {
    "class.run": "the meeting",
    "lms.run_session": "Run Session",
    "lms.attendance": "the attendance",
    "lms.late_joiners": "the late joiners",
    "lms.complete": "marking it complete",
    "zoom.report": "Zoom's report",
    "zoom.recording": "the recording",
    "recording.process": "putting the recording on the LMS",
}


def job_failed(app: Any, job: Any) -> None:
    """A step that will not be tried again. This is the one a person has to know about."""
    notifier = getattr(app, "notifier", None)
    if notifier is None:
        return
    where = _class_of(job)
    step = STEP_NAMES.get(job.type, job.type)
    error = job.error or {}
    what = error.get("message") or error.get("code") or "it failed"
    group = where["group"] or "a class"
    notifier.send_soon(Notice(
        kind="step.failed",
        title=f"{group}: {step} failed",
        message=f"{what} The class is otherwise as it was; nothing else was changed.",
        step=job.type,
        attempts=job.attempts,
        **where,
    ))


def class_blocked(notifier: Any, plan: Any, step: str, why: str) -> None:
    """A class whose time has come and which cannot be started at all.

    The Windows app says this when a class could not be opened after every try; here it is said when
    the stage cannot even be made - no Zoom link on the class, no account that hosts its group, no
    LMS sign-in chosen for its coordinator. Nobody can find that out from a class that simply never
    happens, which is the whole reason for saying it.
    """
    if notifier is None:
        return
    name = STEP_NAMES.get(step, step)
    notifier.send_soon(Notice(
        kind="class.blocked",
        title=f"{plan.group_name}: {name} could not be started",
        # Only the first letter: capitalize() would lower the rest, and "Zoom" is a name.
        message=f"{why[:1].upper()}{why[1:]}. Nothing was run for this class; it is waiting for somebody.",
        group=plan.group_name,
        date=plan.session_date.isoformat(),
        start=plan.start_time,
        step=step,
    ))


def class_warning(app: Any, job: Any, warning: str) -> None:
    """A step that did its work and left something behind - a meeting nobody could close, most often."""
    notifier = getattr(app, "notifier", None)
    if notifier is None:
        return
    where = _class_of(job)
    group = where["group"] or "a class"
    notifier.send_soon(Notice(
        kind="class.warning",
        title=f"{group}: needs a look",
        message=warning,
        step=job.type,
        attempts=job.attempts,
        **where,
    ))


# --------------------------------------------------------------------------- the admin's own page


class NotifyBody(BaseModel):
    model_config = ConfigDict(extra="forbid")
    enabled: bool | None = None
    """The n8n webhook. Absent leaves the kept one alone; "" removes it."""
    url: str | None = Field(default=None, max_length=2048)
    label: str | None = Field(default=None, max_length=100)


def _view(settings: dict[str, Any], notifier: Notifier) -> dict[str, Any]:
    return {
        "enabled": settings["enabled"],
        # Never the address itself: only whether there is one, and enough of it to recognise.
        "hasUrl": bool(settings["url"]),
        "urlHost": settings["url"].split("/")[2] if settings["url"].count("/") >= 2 else None,
        "label": settings["label"],
        "updatedAt": settings["updatedAt"],
        "lastSentAt": notifier.last_sent_at,
        "lastError": notifier.last_error,
    }


def _notifier(request: Request) -> Notifier:
    notifier = getattr(request.app.state, "notifier", None)
    if notifier is None:
        raise ApiError(503, "Unavailable", "This server has no notifier.")
    return notifier


@router.get("/api/v1/dashboard/notifications", dependencies=[Depends(require_admin)])
async def read_notifications(request: Request) -> JSONResponse:
    notifier = _notifier(request)
    return _no_store(_view(await notifier.read(), notifier))


@router.put("/api/v1/dashboard/notifications",
            dependencies=[Depends(require_admin), Depends(require_dashboard_header)])
async def save_notifications(body: NotifyBody, request: Request,
                             admin: CurrentUser = Depends(current_user)) -> JSONResponse:
    notifier = _notifier(request)
    fields = body.model_dump(exclude_unset=True)
    now = request.app.state.clock()
    async with request.app.state.sessionmaker() as session, session.begin():
        row = await session.get(AppSetting, SETTING_KEY, with_for_update=True)
        value = dict(row.value) if row and isinstance(row.value, dict) else {}
        if "enabled" in fields:
            value["enabled"] = bool(body.enabled)
        if "label" in fields:
            value["label"] = (body.label or "").strip()[:100]
        if "url" in fields:
            url = (body.url or "").strip()
            if url and not url.lower().startswith("https://"):
                raise ApiError(400, "Invalid request", "A webhook address must start with https://.")
            if any(c.isspace() or ord(c) < 32 for c in url):
                raise ApiError(400, "Invalid request", "That is not a web address.")
            value["url"] = url
        if row is None:
            session.add(AppSetting(key=SETTING_KEY, value=value, updated_by=admin.id, updated_at=now))
        else:
            row.value, row.updated_by, row.updated_at = value, admin.id, now
    # The address is not in this line, and never is.
    emit("notify.saved", username=admin.username, enabled=value.get("enabled") is not False,
         hasUrl=bool(value.get("url")))
    return _no_store(_view(await notifier.read(), notifier))


@router.post("/api/v1/dashboard/notifications/test",
             dependencies=[Depends(require_admin), Depends(require_dashboard_header)])
async def test_notification(request: Request, admin: CurrentUser = Depends(current_user)) -> JSONResponse:
    """Sends one now and waits for it, so the page can say whether it arrived rather than 'sent'."""
    notifier = _notifier(request)
    settings = await notifier.read()
    if not settings["url"]:
        raise ApiError(409, "No webhook", "Set the n8n webhook address first.")
    sent = await notifier.send(Notice(
        kind="test",
        title="Test notice from the dashboard",
        message=f"{admin.display_name or admin.username} sent this from the dashboard to see that "
                "notices arrive. No class was touched.",
    ))
    emit("notify.test", username=admin.username, sent=sent)
    return _no_store({"sent": sent, "detail": None if sent else (notifier.last_error or "it was not taken"),
                      "enabled": settings["enabled"]})

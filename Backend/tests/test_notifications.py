"""Telling somebody a class needs them, through the admin's own n8n webhook.

The shape posted is the one the Windows app posts (ClassNotifier.cs), so one workflow serves both.
What matters here is that the address is kept like a password, that a notice goes out when a step
will not be tried again, and that a webhook which is down, slow or wrong changes nothing about the
class it is about.
"""

from __future__ import annotations

import json

from central_backend.notifications import Notice, Notifier, class_warning, job_failed
from conftest import call
from test_dashboard import ADMIN_PASSWORD, DASH, login
from test_delegated_runs import as_user, box, dash, two  # noqa: F401

WEBHOOK = "https://example.invalid/webhook/class-notifications"


class Clock:
    """The server's own clock, as the app's state holds it."""

    def __init__(self, at: str = "2026-09-23T19:00:00+03:00") -> None:
        from datetime import datetime
        self.now = datetime.fromisoformat(at)

    def __call__(self):  # noqa: ANN204
        return self.now


class Posted:
    """A webhook that remembers what it was given, and can be told to refuse."""

    def __init__(self, problem: str | None = None) -> None:
        self.problem = problem
        self.calls: list[tuple[str, dict]] = []

    async def __call__(self, url: str, body: bytes) -> str | None:
        self.calls.append((url, json.loads(body)))
        return self.problem


def notifier(dash, post: Posted, label: str = "cloud") -> Notifier:  # noqa: ANN001
    made = Notifier(dash.app.state.sessionmaker, Clock(), label=label)
    made._post = post          # noqa: SLF001 - the test stands in for the network
    return made


def send(dash, made: Notifier, notice: Notice, post: Posted) -> bool:  # noqa: ANN001
    """Sends on the app's own event loop, which is where its database connections live."""
    import central_backend.notifications as module

    original = module._post
    module._post = post        # noqa: SLF001
    try:
        return call(dash, made.send, notice)
    finally:
        module._post = original


def set_webhook(dash, url: str = WEBHOOK, enabled: bool = True):  # noqa: ANN001, ANN201
    return dash.put("/api/v1/dashboard/notifications", headers=DASH, json={"url": url, "enabled": enabled})


# =========================================================================== the address is a secret


def test_the_webhook_address_is_never_given_back(dash, two):
    as_user(dash, "admin", ADMIN_PASSWORD)
    saved = set_webhook(dash)
    assert saved.status_code == 200, saved.text
    assert WEBHOOK not in saved.text
    assert saved.json()["hasUrl"] is True
    assert saved.json()["urlHost"] == "example.invalid"     # enough to recognise, not enough to use

    read = dash.get("/api/v1/dashboard/notifications")
    assert WEBHOOK not in read.text
    assert read.json()["enabled"] is True


def test_only_the_admin_sees_or_changes_where_notices_go(dash, two):
    as_user(dash, "admin", ADMIN_PASSWORD)
    assert set_webhook(dash).status_code == 200

    as_user(dash, "mona")
    assert dash.get("/api/v1/dashboard/notifications").status_code == 403
    assert dash.put("/api/v1/dashboard/notifications", headers=DASH, json={"enabled": False}).status_code == 403
    assert dash.post("/api/v1/dashboard/notifications/test", headers=DASH).status_code == 403
    # And it is not readable through the settings anybody may read, either.
    assert dash.get("/api/v1/settings/notifications").status_code == 404


def test_a_write_needs_the_dashboard_header(dash, two):
    as_user(dash, "admin", ADMIN_PASSWORD)
    assert dash.put("/api/v1/dashboard/notifications", json={"enabled": False}).status_code == 403


def test_an_address_that_is_not_an_https_url_is_refused(dash, two):
    as_user(dash, "admin", ADMIN_PASSWORD)
    for bad in ("http://example.invalid/hook", "example.invalid/hook", "https://exa mple.invalid/hook"):
        answer = dash.put("/api/v1/dashboard/notifications", headers=DASH, json={"url": bad})
        assert answer.status_code == 400, bad

    # An empty one is how it is taken off again.
    assert set_webhook(dash).json()["hasUrl"] is True
    assert dash.put("/api/v1/dashboard/notifications", headers=DASH, json={"url": ""}).json()["hasUrl"] is False


def test_the_switch_and_the_address_are_kept_apart(dash, two):
    """Turning notices off must not lose the webhook: it is turned on again without typing it."""
    as_user(dash, "admin", ADMIN_PASSWORD)
    set_webhook(dash)
    off = dash.put("/api/v1/dashboard/notifications", headers=DASH, json={"enabled": False})
    assert off.json() == {**off.json(), "enabled": False, "hasUrl": True}
    on = dash.put("/api/v1/dashboard/notifications", headers=DASH, json={"enabled": True})
    assert on.json()["enabled"] is True
    assert on.json()["hasUrl"] is True


# =========================================================================== what is sent


def test_a_notice_carries_what_an_email_is_written_from(dash, two):
    as_user(dash, "admin", ADMIN_PASSWORD)
    set_webhook(dash)
    post = Posted()
    made = notifier(dash, post)

    sent = send(dash, made, Notice(
        kind="step.failed", title="CAI5_AIS4_S7: the attendance failed",
        message="the session list did not load", group="CAI5_AIS4_S7", date="2026-09-23",
        start="19:00", step="lms.attendance", attempts=3), post)

    assert sent is True
    url, body = post.calls[0]
    assert url == WEBHOOK
    # The same fields the Windows app posts, so one n8n workflow reads both.
    assert body == {
        "kind": "step.failed", "title": "CAI5_AIS4_S7: the attendance failed",
        "message": "the session list did not load", "group": "CAI5_AIS4_S7", "coordinator": None,
        "date": "2026-09-23", "start": "19:00", "step": "lms.attendance", "attempts": 3,
        "pc": "cloud", "at": "2026-09-23T19:00:00+03:00",
    }


def test_nothing_is_sent_when_there_is_nowhere_to_send_it_or_notices_are_off(dash, two):
    as_user(dash, "admin", ADMIN_PASSWORD)
    post = Posted()
    made = notifier(dash, post)
    notice = Notice(kind="test", title="t", message="m")

    assert send(dash, made, notice, post) is False       # no webhook set at all
    assert post.calls == []

    set_webhook(dash, enabled=False)
    assert send(dash, made, notice, post) is False       # set, but turned off
    assert post.calls == []


def test_a_webhook_that_refuses_is_tried_again_and_then_written_off(dash, two, monkeypatch):
    as_user(dash, "admin", ADMIN_PASSWORD)
    set_webhook(dash)
    post = Posted(problem="the webhook answered 500")
    made = notifier(dash, post)
    # The waits between tries are what they are; the test does not spend 25 seconds proving it.
    monkeypatch.setattr("central_backend.notifications.TRY_AGAIN_AFTER", (0.0, 0.0))

    sent = send(dash, made, Notice(kind="test", title="t", message="m"), post)

    assert sent is False
    assert len(post.calls) == 3                                  # once, then twice more
    assert made.last_error == "the webhook answered 500"


def test_a_notice_that_cannot_be_sent_never_reaches_what_it_is_about(dash, two, monkeypatch):
    """The whole point: a webhook that throws is not a class's problem."""
    as_user(dash, "admin", ADMIN_PASSWORD)
    set_webhook(dash)
    monkeypatch.setattr("central_backend.notifications.TRY_AGAIN_AFTER", ())

    class Exploding(Posted):
        async def __call__(self, url: str, body: bytes) -> str | None:
            raise RuntimeError("the network is gone")

    exploding = Exploding()
    made = notifier(dash, exploding)

    # It answers False rather than throwing, and says what happened.
    assert send(dash, made, Notice(kind="test", title="t", message="m"), exploding) is False
    assert "the network is gone" in (made.last_error or "")


# =========================================================================== a step that failed


class FakeJob:
    def __init__(self, **fields) -> None:  # noqa: ANN003
        self.type = fields.get("type", "lms.attendance")
        self.payload = fields.get("payload", {"group": "CAI5_AIS4_S7", "date": "2026-09-23", "startTime": "19:00"})
        self.error = fields.get("error", {"code": "lmsUnreachable", "message": "The session list did not load."})
        self.result = fields.get("result")
        self.attempts = fields.get("attempts", 3)


class Held:
    """Somewhere for a notice to go, without a network or an event loop."""

    def __init__(self) -> None:
        self.notices: list[Notice] = []

    def send_soon(self, notice: Notice) -> None:
        self.notices.append(notice)


def test_a_step_that_will_not_be_tried_again_is_told_about():
    state = type("State", (), {"notifier": Held()})()
    job_failed(state, FakeJob())

    notice = state.notifier.notices[0]
    assert notice.kind == "step.failed"
    assert notice.title == "CAI5_AIS4_S7: the attendance failed"
    assert "The session list did not load." in notice.message
    assert notice.step == "lms.attendance"
    assert notice.attempts == 3
    assert notice.date == "2026-09-23"


def test_a_class_held_but_not_closed_is_told_about_although_it_succeeded():
    state = type("State", (), {"notifier": Held()})()
    class_warning(state, FakeJob(type="class.run"), "This account is not the meeting's host, so it was left open.")

    notice = state.notifier.notices[0]
    assert notice.kind == "class.warning"
    assert "left open" in notice.message
    assert notice.step == "class.run"


def test_a_server_with_no_notifier_at_all_is_not_a_crash():
    state = type("State", (), {})()
    job_failed(state, FakeJob())                 # nothing to send to, and nothing thrown
    class_warning(state, FakeJob(), "anything")


# =========================================================================== the test button


def test_the_test_button_says_whether_it_arrived(dash, two, monkeypatch):
    as_user(dash, "admin", ADMIN_PASSWORD)
    set_webhook(dash)
    post = Posted()

    async def stand_in(url: str, body: bytes) -> str | None:
        return await post(url, body)

    monkeypatch.setattr("central_backend.notifications._post", stand_in)
    answer = dash.post("/api/v1/dashboard/notifications/test", headers=DASH)

    assert answer.status_code == 200, answer.text
    assert answer.json()["sent"] is True
    assert post.calls[0][1]["kind"] == "test"
    assert "dashboard" in post.calls[0][1]["message"]


def test_the_test_button_says_what_went_wrong_rather_than_pretending(dash, two, monkeypatch):
    as_user(dash, "admin", ADMIN_PASSWORD)
    set_webhook(dash)
    monkeypatch.setattr("central_backend.notifications.TRY_AGAIN_AFTER", ())

    async def refuse(url: str, body: bytes) -> str | None:
        return "the webhook answered 404: the n8n workflow is not active (or the address is a test one)"

    monkeypatch.setattr("central_backend.notifications._post", refuse)
    answer = dash.post("/api/v1/dashboard/notifications/test", headers=DASH)

    assert answer.json()["sent"] is False
    assert "not active" in answer.json()["detail"]


def test_the_test_button_asks_for_a_webhook_before_it_tries(dash, two):
    as_user(dash, "admin", ADMIN_PASSWORD)
    answer = dash.post("/api/v1/dashboard/notifications/test", headers=DASH)
    assert answer.status_code == 409
    assert "webhook" in answer.json()["details"].lower()


def test_signing_in_still_works(dash):
    assert login(dash, "admin", ADMIN_PASSWORD).status_code == 200


# =========================================================================== a class that cannot open


def test_a_class_that_cannot_be_started_is_said_once_not_every_minute(dash, two):
    """The scheduler passes every minute; a class waiting for a Zoom link is one piece of news."""
    from central_backend.scheduling import Scheduler

    said = Held()
    scheduler = Scheduler(dash.app.state.sessionmaker, dash.app.state.clock, notifier=said)

    class Plan:
        id = "11111111-1111-4111-8111-111111111111"
        group_name = "CAI5_IND1_G1"
        start_time = "18:00"

        class session_date:  # noqa: N801
            @staticmethod
            def isoformat() -> str:
                return "2026-09-23"

    for _ in range(5):
        scheduler._say_blocked(Plan(), "class.run", "no Zoom link on the class")  # noqa: SLF001

    assert len(said.notices) == 1
    notice = said.notices[0]
    assert notice.kind == "class.blocked"
    assert notice.title == "CAI5_IND1_G1: the meeting could not be started"
    assert "No Zoom link on the class" in notice.message
    assert notice.date == "2026-09-23"
    assert notice.start == "18:00"

    # A different stage of the same class is its own piece of news.
    scheduler._say_blocked(Plan(), "lms.run_session", "no LMS sign-in is chosen for this coordinator")  # noqa: SLF001
    assert len(said.notices) == 2


def test_a_scheduler_with_nobody_to_tell_still_schedules(dash, two):
    from central_backend.scheduling import Scheduler

    scheduler = Scheduler(dash.app.state.sessionmaker, dash.app.state.clock)      # no notifier at all
    scheduler._say_blocked(type("P", (), {"id": "x", "group_name": "G", "start_time": None,  # noqa: SLF001
                                          "session_date": type("D", (), {"isoformat": staticmethod(lambda: "2026-09-23")})}),
                           "class.run", "no Zoom link on the class")

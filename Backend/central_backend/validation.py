"""What a job may carry. The rules for `recording.process` mirror the Windows app's
`RecordingApiRequestParser`, which checks the payload again on the agent; refusing a bad payload
here means n8n hears about it at once instead of after a device has picked it up."""

from __future__ import annotations

import re
from datetime import date, time
from typing import Any
from urllib.parse import parse_qs, urlsplit

JOB_TYPES: dict[str, str] = {
    "recording.process": "recording_processing",

    # The stages of one class. Each is its own job because each can fail, be retried and be
    # looked at on its own - a class that opened but whose attendance did not go up is a real
    # state, and one job for the whole class could not say so.
    #
    # They are not a sequence. "zoom.report" can only run after the meeting ended, and
    # "lms.complete" after the class is over, but "class.admit" and "class.attendance" run
    # alongside each other while it is live, and the LMS steps do not wait on Zoom at all.
    # What may run when is the caller's business; this table only says who can run what.
    # A meeting is one long thing, not four short ones. class.run owns it for the whole class:
    # it opens the meeting, admits the waiting room as people arrive, takes the attendance
    # snapshots while it is live, and ends it. Splitting that into separate jobs would mean a
    # browser left alive between them, and a worker restart would orphan it with nobody able to
    # say whether the class was still running.
    "class.run": "zoom_web",           # open the meeting and hold it for the class
    "class.end": "zoom_web",           # end one now, from the dashboard, before its time
    "zoom.report": "zoom_web",         # Zoom's own participants report, once it has ended
    "zoom.recording": "zoom_web",      # the recording's link, once Zoom has made one
    "lms.run_session": "lms",          # press Run Session
    "lms.attendance": "lms",
    "lms.late_joiners": "lms",  # the attendance again, three hours in, for whoever came late           # write the attendance that was collected
    "lms.complete": "lms",             # close the session
}
"""Job type -> the capability a device must report to be given it.

A device that reports neither "zoom_web" nor "lms" is never given a class stage, which is how a
recording-only agent and a cloud worker share one backend without either being handed work it
cannot do.
"""

CLASS_STAGE_TYPES = frozenset(t for t in JOB_TYPES if t != "recording.process")

_GROUP = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$")
_DRIVE_ID = re.compile(r"^[A-Za-z0-9_-]{20,100}$")
_DRIVE_FILE_PATH = re.compile(r"^/file/d/(?P<id>[A-Za-z0-9_-]{20,100})(?:/(?:view|preview|edit))?/?$")
_TIME = re.compile(r"^(?:[01]\d|2[0-3]):[0-5]\d$")
_CAPABILITY = re.compile(r"^[a-z][a-z0-9_.-]{0,40}$")

RECORDING_FIELDS = ("group", "recordLink", "date", "startTime", "replaceExisting", "dryRun", "lmsAccountId")

CLASS_STAGE_FIELDS = (
    "classPlanId", "group", "date", "startTime", "coordinatorId", "title",
    "meetingUrl", "zoomAccountId", "lmsAccountId", "durationMinutes", "dryRun",
)

_UUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


class PayloadError(ValueError):
    """The payload is not acceptable. The message is safe to return to the caller and to log."""


def is_drive_file_link(url: str) -> bool:
    if len(url) > 2048 or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in url):
        return False
    try:
        parts = urlsplit(url)
        port = parts.port
    except ValueError:
        return False
    if parts.scheme != "https" or parts.hostname != "drive.google.com" or port is not None:
        return False
    if parts.username is not None or parts.password is not None or parts.netloc.lower() != "drive.google.com":
        return False
    if _DRIVE_FILE_PATH.match(parts.path):
        return True
    if parts.path in ("/open", "/open/"):
        ids = parse_qs(parts.query).get("id", [])
        return len(ids) == 1 and bool(_DRIVE_ID.match(ids[0]))
    return False


def validate_recording_payload(payload: Any) -> dict[str, Any]:
    """The payload normalised (trimmed, defaults filled), or PayloadError."""
    if not isinstance(payload, dict):
        raise PayloadError("'payload' must be a JSON object.")
    unknown = sorted(set(payload) - set(RECORDING_FIELDS))
    if unknown:
        raise PayloadError(f"Unknown field(s) in payload: {', '.join(unknown)}. Allowed: {', '.join(RECORDING_FIELDS)}.")

    group = payload.get("group")
    if not isinstance(group, str) or not group.strip():
        raise PayloadError("'group' is required.")
    group = group.strip()
    if not _GROUP.match(group):
        raise PayloadError("'group' may hold letters, digits, spaces and _ - . (at most 100 characters).")

    link = payload.get("recordLink")
    if not isinstance(link, str) or not link.strip():
        raise PayloadError("'recordLink' is required.")
    link = link.strip()
    if not is_drive_file_link(link):
        raise PayloadError(
            "'recordLink' must be a Google Drive link to one file, like "
            "https://drive.google.com/file/d/<file id>/view?usp=sharing."
        )

    day = payload.get("date")
    if not isinstance(day, str) or not day.strip():
        raise PayloadError(
            "'date' is required (yyyy-MM-dd): a queued job may run later, so 'today' would be ambiguous."
        )
    try:
        parsed_day = date.fromisoformat(day.strip())
        if len(day.strip()) != 10:
            raise ValueError
    except ValueError as exc:
        raise PayloadError("'date' must be yyyy-MM-dd.") from exc

    normalised: dict[str, Any] = {"group": group, "recordLink": link, "date": parsed_day.isoformat()}

    start = payload.get("startTime")
    if start is not None:
        if not isinstance(start, str) or not _TIME.match(start.strip()):
            raise PayloadError("'startTime' must be HH:mm when given.")
        time.fromisoformat(start.strip())
        normalised["startTime"] = start.strip()

    for flag in ("replaceExisting", "dryRun"):
        value = payload.get(flag, False)
        if not isinstance(value, bool):
            raise PayloadError(f"'{flag}' must be true or false.")
        normalised[flag] = value
    lms_account_id = _identifier(payload, "lmsAccountId", required=False)
    if lms_account_id is not None:
        normalised["lmsAccountId"] = lms_account_id
    return normalised


def _identifier(payload: dict[str, Any], field: str, *, required: bool) -> str | None:
    value = payload.get(field)
    if value is None or (isinstance(value, str) and not value.strip()):
        if required:
            raise PayloadError(f"'{field}' is required.")
        return None
    if not isinstance(value, str) or not _UUID.match(value.strip()):
        raise PayloadError(f"'{field}' must be a UUID.")
    return value.strip().lower()


def validate_class_stage_payload(job_type: str, payload: Any) -> dict[str, Any]:
    """One stage of one class: which class it is, and whose accounts it runs under.

    Every stage carries the same thing, because every stage asks the same question. The rule
    that matters is that a class says whose it is: a stage that ran under whoever happened to be
    signed in would put one coordinator's attendance under another's name, and no later step
    could tell. So coordinatorId is required, and the accounts are named rather than chosen at
    the far end.

    The account fields are ids, never sign-ins. What they point at is kept encrypted on the
    server and handed over one run at a time.
    """
    if not isinstance(payload, dict):
        raise PayloadError("'payload' must be a JSON object.")
    unknown = sorted(set(payload) - set(CLASS_STAGE_FIELDS))
    if unknown:
        raise PayloadError(
            f"Unknown field(s) in payload: {', '.join(unknown)}. Allowed: {', '.join(CLASS_STAGE_FIELDS)}.")

    group = payload.get("group")
    if not isinstance(group, str) or not group.strip():
        raise PayloadError("'group' is required.")
    group = group.strip()
    if not _GROUP.match(group):
        raise PayloadError("'group' may hold letters, digits, spaces and _ - . (at most 100 characters).")

    day = payload.get("date")
    if not isinstance(day, str) or not day.strip():
        raise PayloadError("'date' is required (yyyy-MM-dd): a queued job may run after midnight.")
    try:
        if len(day.strip()) != 10:
            raise ValueError
        parsed_day = date.fromisoformat(day.strip())
    except ValueError as exc:
        raise PayloadError("'date' must be yyyy-MM-dd.") from exc

    normalised: dict[str, Any] = {
        "classPlanId": _identifier(payload, "classPlanId", required=True),
        "group": group,
        "date": parsed_day.isoformat(),
        "coordinatorId": _identifier(payload, "coordinatorId", required=True),
    }

    start = payload.get("startTime")
    if start is not None:
        if not isinstance(start, str) or not _TIME.match(start.strip()):
            raise PayloadError("'startTime' must be HH:mm when given.")
        time.fromisoformat(start.strip())
        normalised["startTime"] = start.strip()

    # The class's name as the timetable gives it ("CAI5_AIS4_S7 • 29 • Freelancing Skills"): what
    # tells a session's kind, and so who teaches it and is made co-host.
    title = payload.get("title")
    if title is not None:
        if not isinstance(title, str) or len(title) > 200 or any(ord(c) < 32 for c in title):
            raise PayloadError("'title' must be a single line of at most 200 characters when given.")
        if title.strip():
            normalised["title"] = title.strip()

    for field in ("zoomAccountId", "lmsAccountId"):
        if (value := _identifier(payload, field, required=False)) is not None:
            normalised[field] = value

    url = payload.get("meetingUrl")
    if url is not None:
        if not isinstance(url, str) or not url.strip():
            raise PayloadError("'meetingUrl' must be a URL when given.")
        url = url.strip()
        if len(url) > 2048 or any(c.isspace() or ord(c) < 32 or ord(c) == 127 for c in url):
            raise PayloadError("'meetingUrl' must be a single-line https URL.")
        parts = urlsplit(url)
        if parts.scheme != "https" or not parts.hostname or parts.username or parts.password:
            raise PayloadError("'meetingUrl' must be an https URL with no user name or password in it.")
        normalised["meetingUrl"] = url

    # Opening a meeting needs somewhere to open. Ending one does not - it finds the live meeting
    # the worker already has - and the LMS stages never touch Zoom at all.
    if job_type == "class.run" and "meetingUrl" not in normalised:
        raise PayloadError("'meetingUrl' is required for class.run: there is nothing to open without it.")

    # How long to hold the meeting. Without a limit a worker that lost touch with the backend
    # would sit in an empty meeting for ever, holding the only slot it has.
    minutes = payload.get("durationMinutes")
    if minutes is not None:
        if not isinstance(minutes, int) or isinstance(minutes, bool) or not 1 <= minutes <= 600:
            raise PayloadError("'durationMinutes' must be a whole number of minutes from 1 to 600.")
        normalised["durationMinutes"] = minutes
    elif job_type == "class.run":
        normalised["durationMinutes"] = 180

    # An LMS stage that does not say whose sign-in to use would fall back to whoever the machine
    # last used, and write one coordinator's class up under another's name.
    if job_type.startswith("lms.") and "lmsAccountId" not in normalised:
        raise PayloadError(f"'lmsAccountId' is required for {job_type}: a class is written up under its own account.")

    value = payload.get("dryRun", False)
    if not isinstance(value, bool):
        raise PayloadError("'dryRun' must be true or false.")
    normalised["dryRun"] = value
    return normalised


def validate_payload(job_type: str, payload: Any) -> dict[str, Any]:
    if job_type == "recording.process":
        return validate_recording_payload(payload)
    if job_type in CLASS_STAGE_TYPES:
        return validate_class_stage_payload(job_type, payload)
    raise PayloadError(f"Unknown job type '{job_type}'. Known: {', '.join(JOB_TYPES)}.")


def clean_capabilities(values: Any) -> list[str]:
    """Capabilities as the agent reports them, kept only if they look like capability names."""
    if not isinstance(values, list):
        return []
    seen: list[str] = []
    for value in values[:20]:
        if isinstance(value, str) and _CAPABILITY.match(value) and value not in seen:
            seen.append(value)
    return seen


# What a result may carry. The list is closed on purpose - an agent's result is written straight
# to a row the dashboard reads, so an agent cannot decide to store something the server never
# agreed to hold. It does have to name every field the stages actually report, though: the class
# stages were reporting what they did and the server was dropping all of it, so a held class and a
# class that opened and closed immediately looked identical on the Sessions page.
_RESULT_FIELDS = {
    "alreadyExists": bool, "dryRun": bool, "message": str,
    # Which class this is about, so a result reads on its own.
    "classPlanId": str, "group": str, "date": str, "startTime": str,
    # What the stage did, in its own words, and how long it was there.
    "did": str, "holdsFor": int, "heldFor": int, "minutes": int,
    "startedAt": str, "endedAt": str,
    # class.run: whether the meeting was actually closed afterwards. Left open is the failure
    # nobody notices until the next class cannot start.
    "endedTheMeeting": bool,
    # Counts, never the names: a roster does not belong in a job row.
    "people": int, "present": int, "attended": int, "rows": int, "instances": int,
    # What went wrong while still succeeding: a class that was held but whose meeting could not
    # be closed is not a clean run, and the difference has to reach the page.
    "warning": str,
    # A recording's link, and the session's name on the LMS.
    "shareUrl": str, "name": str,
}
_ERROR_FIELDS = {"code": str, "message": str, "retryable": bool, "retryAfterSeconds": int}


def _pick(values: Any, allowed: dict[str, type]) -> dict[str, Any]:
    if not isinstance(values, dict):
        return {}
    picked: dict[str, Any] = {}
    for name, kind in allowed.items():
        value = values.get(name)
        if isinstance(value, kind) and not (kind is int and isinstance(value, bool)):
            picked[name] = value[:500] if isinstance(value, str) else value
    return picked


def clean_result(values: Any) -> dict[str, Any]:
    """Only the fields a result may carry; anything else an agent sends is dropped, not stored."""
    return _pick(values, _RESULT_FIELDS)


def clean_error(values: Any) -> dict[str, Any]:
    error = _pick(values, _ERROR_FIELDS)
    error.setdefault("code", "agentError")
    error.setdefault("message", "The agent reported a failure without a message.")
    if "retryAfterSeconds" in error:
        error["retryAfterSeconds"] = max(0, min(3600, error["retryAfterSeconds"]))
    return error

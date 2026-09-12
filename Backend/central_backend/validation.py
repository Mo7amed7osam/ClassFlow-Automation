"""What a job may carry. The rules for `recording.process` mirror the Windows app's
`RecordingApiRequestParser`, which checks the payload again on the agent; refusing a bad payload
here means n8n hears about it at once instead of after a device has picked it up."""

from __future__ import annotations

import re
from datetime import date, time
from typing import Any
from urllib.parse import parse_qs, urlsplit

JOB_TYPES: dict[str, str] = {"recording.process": "recording_processing"}
"""Job type -> the capability a device must report to be given it."""

_GROUP = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$")
_DRIVE_ID = re.compile(r"^[A-Za-z0-9_-]{20,100}$")
_DRIVE_FILE_PATH = re.compile(r"^/file/d/(?P<id>[A-Za-z0-9_-]{20,100})(?:/(?:view|preview|edit))?/?$")
_TIME = re.compile(r"^(?:[01]\d|2[0-3]):[0-5]\d$")
_CAPABILITY = re.compile(r"^[a-z][a-z0-9_.-]{0,40}$")

RECORDING_FIELDS = ("group", "recordLink", "date", "startTime", "replaceExisting", "dryRun")


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
    return normalised


def validate_payload(job_type: str, payload: Any) -> dict[str, Any]:
    if job_type == "recording.process":
        return validate_recording_payload(payload)
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


_RESULT_FIELDS = {"alreadyExists": bool, "dryRun": bool, "message": str, "group": str, "date": str, "startTime": str}
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

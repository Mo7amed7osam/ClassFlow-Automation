"""Structured logs: one JSON object per line, an `event` name plus fields.

Secrets never reach a log line. Callers pass ids, names and states only, and a Drive link only as
`link_preview(...)`. As a second line of defence, any string that looks like one of our tokens or
keys, or like a full Drive file link, is masked before it is written.
"""

from __future__ import annotations

import json
import logging
import re
import sys
from datetime import UTC, datetime
from typing import Any

LOGGER_NAME = "central"
logger = logging.getLogger(LOGGER_NAME)

_SECRET_LIKE = re.compile(r"zaa[kde]_[A-Za-z0-9_.\-]+")
_DRIVE_ID = re.compile(r"(drive\.google\.com/(?:file/d/|open\?id=))([A-Za-z0-9_-]{6})[A-Za-z0-9_-]*")
# In the scrubber only ids longer than a preview are shortened, so a preview is not shortened twice.
_DRIVE_ID_IN_TEXT = re.compile(r"(drive\.google\.com/(?:file/d/|open\?id=))([A-Za-z0-9_-]{6})[A-Za-z0-9_-]+")


def link_preview(url: str | None) -> str:
    """`drive.google.com/file/d/1AbCdE...`: enough to recognise a link, not enough to open it."""
    if not url:
        return ""
    text = re.sub(r"^https?://", "", url.strip())
    match = _DRIVE_ID.search(text)
    if match:
        return f"{match.group(1)}{match.group(2)}..."
    return text[:24] + ("..." if len(text) > 24 else "")


def _scrub(value: Any) -> Any:
    if isinstance(value, str):
        value = _SECRET_LIKE.sub("[redacted]", value)
        return _DRIVE_ID_IN_TEXT.sub(lambda m: f"{m.group(1)}{m.group(2)}...", value)
    if isinstance(value, dict):
        return {k: _scrub(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_scrub(v) for v in value]
    return value


def emit(event: str, /, level: int = logging.INFO, **fields: Any) -> None:
    fields.pop("event", None)  # the event name is the first argument; a field may not replace it
    record = {"event": event, **{k: _scrub(v) for k, v in fields.items() if v is not None}}
    logger.log(level, json.dumps(record, default=str, separators=(",", ":")))


class _JsonLineFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        message = record.getMessage()
        try:
            body = json.loads(message)
            if not isinstance(body, dict):
                body = {"message": message}
        except ValueError:
            body = {"event": "log", "message": _scrub(message)}
        body = {"ts": datetime.now(UTC).isoformat(timespec="milliseconds"), "level": record.levelname.lower(), **body}
        return json.dumps(body, default=str, separators=(",", ":"))


def configure_logging(level: int = logging.INFO) -> None:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(_JsonLineFormatter())
    logger.handlers[:] = [handler]
    logger.setLevel(level)
    logger.propagate = False

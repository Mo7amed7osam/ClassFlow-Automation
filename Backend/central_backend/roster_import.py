"""Reading a roster someone pasted or uploaded: CSV, tab-separated text pasted from Excel, or one name
per line. Pure functions.

Based on the Chrome extension's parser (numbering, bullets, quotes and status words are stripped;
status and header rows are skipped), with its gaps closed: a header row is recognised by its
column names (English or Arabic) and used to find the name, e-mail, id and order columns; quoted
fields may contain commas; an id or e-mail is never taken for the name.
"""

from __future__ import annotations

import csv
import io
import re
from dataclasses import dataclass, field

from .attendance_names import normalize

MAX_ROWS = 2000
MAX_NAME = 300

_EMAIL = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")
_STATUS = re.compile(r"^(?:joined|not[ -]?joined|present|absent|attended|not[ -]?attended|left|waiting(?:[ -]room)?|admitted|in[ -]meeting|حاضر|غائب|غايب)$", re.IGNORECASE)
_TRAILING_STATUS = re.compile(r"\s+(?:joined|not[ -]?joined|present|absent|attended|not[ -]?attended)$", re.IGNORECASE)
_HEADERS = {
    "name": {"name", "full name", "fullname", "student", "student name", "studentname", "participant", "participants",
             "الاسم", "اسم", "اسم الطالب", "الاسم بالكامل", "الاسم الكامل", "الطالب"},
    "email": {"email", "e-mail", "mail", "email address", "البريد", "البريد الالكتروني", "الايميل", "ايميل"},
    "id": {"id", "student id", "studentid", "code", "student code", "national id", "رقم", "الرقم", "كود", "الكود", "رقم الطالب"},
    "order": {"order", "#", "no", "no.", "number", "serial", "م", "مسلسل", "الترتيب"},
    "status": {"status", "attendance", "attendance status", "join status", "الحالة"},
    "group": {"group", "group name", "group id", "groupid", "المجموعة", "الجروب"},
}


@dataclass(frozen=True)
class RosterRow:
    full_name: str
    email: str | None = None
    external_id: str | None = None
    order: int | None = None
    line: int = 0


@dataclass
class ParsedRoster:
    rows: list[RosterRow] = field(default_factory=list)
    invalid: list[str] = field(default_factory=list)
    duplicates: list[str] = field(default_factory=list)
    skipped: list[str] = field(default_factory=list)       # why lines were left out (no names repeated)


def clean_cell(value: str) -> str:
    text = (value or "").strip().strip("\ufeff").strip()
    if len(text) >= 2 and text[0] == text[-1] and text[0] in "'\"":
        text = text[1:-1].strip()
    text = re.sub(r"^[•·▪◦*\-–]+\s*", "", text)               # bullets
    text = re.sub(r"^\d{1,4}\s*[.)\-]\s+", "", text)            # "12. Name", "3) Name"
    text = _TRAILING_STATUS.sub("", text)
    return re.sub(r"\s+", " ", text).strip()


def _header_kind(cell: str) -> str | None:
    key = re.sub(r"[\s_]+", " ", cell.strip().lower()).strip(" :")
    for kind, words in _HEADERS.items():
        if key in words:
            return kind
    return None


def _looks_like_name(cell: str) -> bool:
    return bool(cell) and any(ch.isalpha() for ch in cell) and not _EMAIL.match(cell) and not _STATUS.match(cell) \
        and _header_kind(cell) is None


def _rows(text: str) -> list[list[str]]:
    lines = [line for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n")]
    sample = "\n".join(lines[:20])
    delimiter = "\t" if "\t" in sample else (";" if sample.count(";") > sample.count(",") else ",")
    return [row for row in csv.reader(io.StringIO("\n".join(lines)), delimiter=delimiter, skipinitialspace=True)]


def parse_roster(text: str, *, group: str | None = None) -> ParsedRoster:
    result = ParsedRoster()
    rows = [[c for c in row] for row in _rows((text or "").lstrip("\ufeff")) if any(c.strip() for c in row)]
    if not rows:
        return result

    columns: dict[str, int] = {}
    header = [_header_kind(c) for c in rows[0]]
    if any(h in ("name", "email", "id") for h in header):
        columns = {kind: i for i, kind in reversed(list(enumerate(header))) if kind}
        rows = rows[1:]

    seen_ids: set[str] = set()
    seen_names: set[str] = set()
    seen_emails: set[str] = set()
    for number, raw in enumerate(rows, start=2 if columns else 1):
        if len(result.rows) >= MAX_ROWS:
            result.skipped.append(f"Only the first {MAX_ROWS} students were read.")
            break
        cells = [clean_cell(c) for c in raw]
        if columns:
            get = lambda kind: cells[columns[kind]] if kind in columns and columns[kind] < len(cells) else ""  # noqa: E731
            name, email, ext, order_text = get("name"), get("email"), get("id"), get("order")
            if group is not None and get("group") and get("group") != group:
                result.skipped.append(f"Line {number}: group does not match the selected group.")
                continue
            if not name and "name" not in columns:
                name = next((c for c in cells if _looks_like_name(c)), "")
        else:
            email = next((c for c in cells if _EMAIL.match(c)), "")
            candidates = [c for c in cells if _looks_like_name(c)]
            name = next((c for c in candidates if len(c.split()) >= 2), candidates[0] if candidates else "")
            ext = next((c for c in cells if c and c not in (name, email) and re.fullmatch(r"[A-Za-z0-9_\-/]{2,64}", c)
                        and any(ch.isdigit() for ch in c)), "")
            order_text = ""
        if not name or _STATUS.match(name) or _header_kind(name):
            if any(cells):
                result.skipped.append(f"Line {number}: no student name.")
            continue
        if len(name) > MAX_NAME or len(email) > 320 or len(ext) > 128:
            result.skipped.append(f"Line {number}: name, e-mail or ID is too long.")
            continue
        if ext and ext in seen_ids:
            result.skipped.append(f"Line {number}: the same ID appears earlier.")
            continue
        key = normalize(name)
        if not key:
            result.skipped.append(f"Line {number}: no letters in the name.")
            continue
        if key in seen_names:
            result.skipped.append(f"Line {number}: the same name appears earlier.")
            continue
        email_value = email.lower() if email and _EMAIL.match(email) else None
        if email and not email_value:
            result.skipped.append(f"Line {number}: the e-mail is not valid.")
            continue
        if email_value and email_value in seen_emails:
            result.skipped.append(f"Line {number}: the same e-mail appears earlier.")
            continue
        order = int(order_text) if order_text.isdigit() and 0 < int(order_text) < 100000 else None
        if ext:
            seen_ids.add(ext)
        seen_names.add(key)
        if email_value:
            seen_emails.add(email_value)
        result.rows.append(RosterRow(name, email_value, ext or None, order, number))
    result.duplicates = [s for s in result.skipped if "appears earlier" in s]
    result.invalid = [s for s in result.skipped if s not in result.duplicates]
    return result

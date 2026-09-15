"""Attendance matching and presence timing. Pure functions, no database.

Matching assigns Zoom names to roster students, one name per student and one student per name
(a second name confidently tied to an already-present student is kept as an additional name),
in this order of precedence:

  1. manual decisions (kept as they are by re-matching);
  2. name memory: an accepted alias of exactly one student (a rejected alias blocks that pair);
  3. the name itself: exact / roster alias / first-and-family rules / fuzzy spelling.

A pair is `present` at CONFIRM or above when nobody else is a close second (within MARGIN) and the
Zoom name is a full name (or exact / alias); `needs_review` from REVIEW_MIN; below that it stays
unmatched. Everything else on the roster is `absent`.

Presence timing turns the snapshots (reads of the Zoom list) into intervals per name: a name seen in
consecutive snapshots is one interval; it ends at the midpoint between the last snapshot that listed
it and the first complete one that did not. An incomplete snapshot (a list that may hide rows) never
ends an interval.
"""

from __future__ import annotations

from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field
from datetime import datetime

from .attendance_names import Score, is_full_name, normalize, score

CONFIRM = 90
REVIEW_MIN = 55
MARGIN = 8


# --------------------------------------------------------------------------- presence timing


@dataclass
class Presence:
    key: str
    name: str
    first_seen: datetime
    last_seen: datetime
    sightings: int = 0
    intervals: list[tuple[datetime, datetime]] = field(default_factory=list)

    @property
    def seconds(self) -> int:
        return int(sum((b - a).total_seconds() for a, b in self.intervals))


@dataclass(frozen=True)
class SnapshotView:
    captured_at: datetime
    names: Sequence[str]        # already-cleaned display names
    complete: bool


def presence(snapshots: Iterable[SnapshotView], session_end: datetime | None = None) -> dict[str, Presence]:
    """Per normalized name: first/last seen, sightings and presence intervals."""
    ordered = sorted(snapshots, key=lambda s: s.captured_at)
    result: dict[str, Presence] = {}
    open_since: dict[str, datetime] = {}
    previous_complete: datetime | None = None
    previous_time: datetime | None = None
    for snap in ordered:
        t = snap.captured_at
        seen: dict[str, str] = {}
        for name in snap.names:
            key = normalize(name)
            if key and key not in seen:
                seen[key] = name
        for key, name in seen.items():
            p = result.get(key)
            if p is None:
                p = result[key] = Presence(key, name, t, t)
            p.last_seen = t
            p.sightings += 1
            if key not in open_since:
                # joined between the last complete read without them and now: start at the midpoint
                start = t if previous_complete is None or previous_complete >= t else previous_complete + (t - previous_complete) / 2
                if p.intervals and p.intervals[-1][1] >= start:
                    start = p.intervals[-1][1]
                open_since[key] = start
        if snap.complete:
            for key in [k for k in open_since if k not in seen]:
                p = result[key]
                end = p.last_seen + (t - p.last_seen) / 2
                p.intervals.append((open_since.pop(key), end))
            previous_complete = t
        previous_time = t
    for key, start in open_since.items():
        p = result[key]
        end = p.last_seen
        if session_end is not None and previous_time is not None and p.last_seen == previous_time and session_end > end:
            end = session_end                         # still there when the meeting ended
        p.intervals.append((start, max(start, end)))
    return result


def merge_intervals(intervals: Iterable[tuple[datetime, datetime]]) -> list[tuple[datetime, datetime]]:
    merged: list[tuple[datetime, datetime]] = []
    for a, b in sorted(intervals):
        if merged and a <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], b))
        else:
            merged.append((a, b))
    return merged


# --------------------------------------------------------------------------- matching


@dataclass(frozen=True)
class RosterStudent:
    id: str
    full_name: str
    order: int
    aliases: tuple[str, ...] = ()


@dataclass(frozen=True)
class Observed:
    id: str
    name: str
    key: str          # normalize(name)
    ignored: bool = False


@dataclass(frozen=True)
class AliasMemory:
    student_id: str
    key: str          # normalize(alias)
    accepted: bool


@dataclass(frozen=True)
class ManualDecision:
    student_id: str
    participant_id: str | None   # None: marked absent by hand
    status: str                  # present | absent | needs_review


@dataclass
class Assignment:
    student_id: str
    status: str                  # present | needs_review | absent
    participant_id: str | None = None
    extra_participant_ids: list[str] = field(default_factory=list)
    confidence: int = 0
    source: str = "none"         # exact | alias | memory | rule | fuzzy | ai | manual | none
    reason: str = ""
    manual: bool = False


@dataclass
class MatchResult:
    assignments: dict[str, Assignment]
    unmatched: list[str]         # participant ids nobody got (ignored ones excluded)
    candidates: dict[str, list[tuple[str, int]]]   # participant id -> [(student id, score)], best first


def _pair_scores(students: Sequence[RosterStudent], observed: Sequence[Observed],
                 memory: Sequence[AliasMemory]) -> tuple[dict[tuple[str, str], Score], set[tuple[str, str]]]:
    rejected = {(m.student_id, m.key) for m in memory if not m.accepted}
    owners: dict[str, set[str]] = {}
    for m in memory:
        if m.accepted:
            owners.setdefault(m.key, set()).add(m.student_id)
    roster_ids = {s.id for s in students}
    scores: dict[tuple[str, str], Score] = {}
    for p in observed:
        if p.ignored or not p.key:
            continue
        who = owners.get(p.key, set()) & roster_ids
        for s in students:
            if (s.id, p.key) in rejected:
                continue
            if s.id in who:
                scores[(s.id, p.id)] = Score(100 if len(who) == 1 else REVIEW_MIN + 20, "memory",
                                             "Remembered name." if len(who) == 1 else "Remembered for more than one student.")
                continue
            sc = score(s.full_name, list(s.aliases), p.name)
            if sc.value > 0:
                scores[(s.id, p.id)] = sc
    return scores, rejected


def match(students: Sequence[RosterStudent], observed: Sequence[Observed], memory: Sequence[AliasMemory] = (),
          manual: Sequence[ManualDecision] = ()) -> MatchResult:
    by_id = {p.id: p for p in observed}
    scores, _ = _pair_scores(students, observed, memory)
    order = {s.id: s.order for s in students}
    assignments: dict[str, Assignment] = {}
    used_participants: set[str] = set()
    ambiguous: list[tuple[str, str]] = []     # (student, name) pairs that lost a close call

    # 1. manual decisions stand
    for d in manual:
        if d.student_id not in order:
            continue
        pid = d.participant_id if d.participant_id in by_id else None
        assignments[d.student_id] = Assignment(d.student_id, d.status, pid, [], 100 if pid or d.status == "present" else 0,
                                               "manual", "Set by hand.", manual=True)
        if pid:
            used_participants.add(pid)

    # 2-3. greedy best-first over every remaining pair, one to one
    pairs = sorted(((sc.value, -order[sid], sid, pid, sc) for (sid, pid), sc in scores.items()
                    if sid not in assignments and pid not in used_participants and sc.value >= REVIEW_MIN),
                   key=lambda x: (-x[0], -x[1], x[3]))
    for value, _, sid, pid, sc in pairs:
        if sid in assignments or pid in used_participants:
            continue
        p = by_id[pid]
        rivals = [s2 for (s2, p2), other in scores.items()
                  if p2 == pid and s2 != sid and s2 not in assignments and other.value >= max(REVIEW_MIN, value - MARGIN)]
        strong_identity = sc.source in ("exact", "alias", "memory") or is_full_name(p.name)
        confident = value >= CONFIRM and not rivals and strong_identity
        reason = sc.reason if not rivals else f"{sc.reason} Another student fits almost as well."
        assignments[sid] = Assignment(sid, "present" if confident else "needs_review", pid, [], value, sc.source, reason)
        used_participants.add(pid)
        ambiguous.extend((rival, pid) for rival in rivals)

    # additional names: an unused name that confidently belongs to a student already present
    for p in observed:
        if p.ignored or p.id in used_participants:
            continue
        fits = sorted(((sc.value, sid) for (sid, pid), sc in scores.items() if pid == p.id), reverse=True)
        if not fits:
            continue
        best, sid = fits[0]
        runner_up = fits[1][0] if len(fits) > 1 else 0
        a = assignments.get(sid)
        if a and a.status == "present" and best >= CONFIRM and best - runner_up >= MARGIN and is_full_name(p.name):
            a.extra_participant_ids.append(p.id)
            used_participants.add(p.id)

    # a student who fitted a name almost as well as the one who got it is flagged too, so a
    # person reviewing sees both sides of the close call
    for sid, pid in ambiguous:
        if sid not in assignments:
            assignments[sid] = Assignment(sid, "needs_review", None, [], scores[(sid, pid)].value, scores[(sid, pid)].source,
                                          f"Could be '{by_id[pid].name}', which also fits another student.")

    for s in students:
        assignments.setdefault(s.id, Assignment(s.id, "absent", reason="Not seen in the meeting."))

    candidates: dict[str, list[tuple[str, int]]] = {}
    for (sid, pid), sc in scores.items():
        if sc.value >= REVIEW_MIN - 20:
            candidates.setdefault(pid, []).append((sid, sc.value))
    for pid in candidates:
        candidates[pid].sort(key=lambda x: (-x[1], order[x[0]]))
    unmatched = [p.id for p in observed if not p.ignored and p.id not in used_participants]
    return MatchResult(assignments, unmatched, candidates)

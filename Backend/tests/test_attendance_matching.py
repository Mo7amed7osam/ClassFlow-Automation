"""Attendance names, matching and presence timing: pure functions, no database.

The name cases come from the Windows app's matching tests (so both systems agree) and from the
Chrome extension's known bugs (so they stay fixed)."""

from __future__ import annotations

from datetime import UTC, datetime, timedelta

import pytest

from central_backend.attendance_matching import (
    AliasMemory,
    ManualDecision,
    Observed,
    RosterStudent,
    SnapshotView,
    match,
    presence,
)
from central_backend.attendance_names import clean_display_name, fuzzy_score, is_full_name, normalize, score
from central_backend.roster_import import parse_roster

T0 = datetime(2026, 9, 14, 18, 0, tzinfo=UTC)

# =========================================================================== names


@pytest.mark.parametrize("observed, roster, expected", [
    ("Mohab Osama Sayed Mohamed", "Mohab Osama Sayed Mohamed", 100),
    ("MOHAB  Osama", "Mohab Osama Sayed Mohamed", 95),
    ("Mohab Osama Sayed", "Mohab Osama Sayed Mohamed", 98),
    ("Mo7ab Osama", "Mohab Osama Sayed Mohamed", 95),                          # Arabizi
    ("أَحْمَد مُحَمَّد عَلي", "احمد محمد علي", 100),                               # diacritics
    ("مُهاب أُسامة", "Mohab Osama Sayed Mohamed", 95),                           # Arabic <-> English variants
    ("José Luis", "Jose Luis Silva", 95),                                    # accents dropped, not turned into spaces
    ("د. نور حسن", "Nour Hassan", 100),                                      # a title is ignored
    ("Muhammad Aly", "Mohamed Ali", 100),                                    # spelling variants
])
def test_names_the_windows_app_confirms_score_the_same(observed, roster, expected):
    assert score(roster, [], observed).value == expected


@pytest.mark.parametrize("raw, name, staff", [
    ("Mohab Mohamed __Coordinator,(Guest), Computer audio muted", "Mohab Mohamed __Coordinator", False),
    ("Ahmed Ali (Host, me)", "Ahmed Ali", True),
    ("سارة (مضيف مشارك)", "سارة", True),
    ("Omar (Mobile)", "Omar (Mobile)", False),                               # not Zoom's annotation: kept
    ("  Nour   Hassan  muted ", "Nour Hassan", False),
])
def test_zoom_annotations_are_removed_and_hosts_recognised(raw, name, staff):
    cleaned = clean_display_name(raw)
    assert (cleaned.name, cleaned.is_staff) == (name, staff)


def test_normalization_folds_what_the_extension_missed():
    assert normalize("فاطمة الزهراء") == normalize("فاطمه الزهراء")
    assert normalize("محمـــد") == "mohamed"                                   # tatweel
    assert normalize("رقم ٢٠٢٤") == normalize("رقم 2024")                      # Arabic-Indic digits
    assert normalize("Renée") == "renee"
    assert normalize("  ") == "" and normalize(None) == ""


@pytest.mark.parametrize("spellings", [
    ["مريم عبد الرحمن", "مريم عبدالرحمن"],
    ["Abd El Rahman Ali", "Abdelrahman Ali", "Abdul Rahman Ali", "Abdulrahman Ali", "Abd Elrahman Ali", "Abd Er Rahman Ali",
     "عبد الرحمن علي", "عبدالرحمن علي"],
    ["Abdullah Omar", "Abdallah Omar", "Abd Allah Omar", "عبد الله عمر", "عبدالله عمر"],
    ["Nour El Din Youssef", "Noureldin Youssef", "Nour Eldin Youssef", "Nour El Deen Youssef", "Nourdin Youssef"],
    ["نور الدين يوسف", "نورالدين يوسف"],
    ["Mohamed El Sayed", "Mohamed Elsayed", "Mohamed Al Sayed", "Mohamed Alsayed"],
    ["Abu Bakr Salem", "Abou Bakr Salem", "Aboubakr Salem", "Abo Bakr Salem"],
    ["ابو بكر سالم", "أبوبكر سالم"],
    ["Alaa Eldin Kamal", "Alaa El Din Kamal", "Alaaeldin Kamal"],
])
def test_compound_names_are_one_name_however_they_are_spaced(spellings):
    assert len({normalize(s) for s in spellings}) == 1, {s: normalize(s) for s in spellings}
    assert score(spellings[0], [], spellings[-1]).value == 100


def test_compound_joining_keeps_different_names_apart():
    assert normalize("Ali Hassan") == "ali hassan"                          # short "al" names untouched
    assert normalize("Alaa Mahmoud") != normalize("Mahmoud Alaa")
    assert normalize("عبد الرحمن") != normalize("عبد الله")
    assert normalize("Nadine Adel") == "nadine adel"                        # ends in "-dine" but is no compound
    assert score("Abdelrahman Mohamed Ali", [], "Abdallah Mohamed").value < 90


def test_a_repeated_word_no_longer_makes_a_perfect_match():
    # The extension scored this 1.0 (duplicate tokens counted twice) and learned it for good.
    assert fuzzy_score("Mohamed Mohamed Ali", "Mohamed Ali Hassan") < 90
    assert score("Mohamed Mohamed Ali", [], "Mohamed Ali Hassan").value < 90


def test_one_word_never_identifies_a_student():
    assert not is_full_name("Mohamed") and is_full_name("Mohab Osama")
    assert score("Mohab Osama", [], "Mohab").value <= 60
    assert score("Mohab Osama", [], "iPhone").value < 55


# =========================================================================== matching

ROSTER = [
    RosterStudent("s1", "Mohab Osama Sayed Mohamed", 1),
    RosterStudent("s2", "Ahmed Mohamed Ali", 2),
    RosterStudent("s3", "Ahmed Mohamed Hassan", 3),
    RosterStudent("s4", "Sara Khaled", 4, ("Sara K",)),
    RosterStudent("s5", "Omar Adel", 5),
]


def seen(*names: str, ignored: tuple[str, ...] = ()) -> list[Observed]:
    return [Observed(f"p{i}", n, normalize(n), n in ignored) for i, n in enumerate(names)]


def test_confident_names_are_present_and_the_rest_absent():
    result = match(ROSTER, seen("Mohab Osama", "Sara K", "Unknown Guest"))
    a = result.assignments
    assert (a["s1"].status, a["s1"].confidence, a["s1"].source) == ("present", 95, "rule")
    assert (a["s4"].status, a["s4"].source) == ("present", "alias")
    assert a["s5"].status == "absent" and a["s5"].participant_id is None
    assert result.unmatched == ["p2"]                                         # kept, not thrown away


def test_a_name_that_fits_two_students_flags_both_for_review():
    result = match(ROSTER, seen("Ahmed Mohamed"))
    a = result.assignments
    assert a["s2"].status == a["s3"].status == "needs_review"
    held = [s for s in ("s2", "s3") if a[s].participant_id == "p0"]
    assert len(held) == 1                                                    # one name, one student
    assert "also fits another student" in a["s3" if held == ["s2"] else "s2"].reason


def test_one_name_per_student_and_one_student_per_name():
    result = match(ROSTER, seen("Mohab Osama Sayed Mohamed", "Mohab Osama Sayed Mohamed (2)", "Omar Adel"))
    used = [a.participant_id for a in result.assignments.values() if a.participant_id]
    assert len(used) == len(set(used))
    assert result.assignments["s5"].status == "present"


def test_a_second_device_is_an_additional_name_of_the_same_student():
    result = match(ROSTER, seen("Mohab Osama Sayed Mohamed", "Mohab Osama Sayed"))
    a = result.assignments["s1"]
    assert a.status == "present" and a.participant_id == "p0" and a.extra_participant_ids == ["p1"]
    assert result.unmatched == []


def test_ignored_names_are_never_matched():
    result = match(ROSTER, seen("Omar Adel", ignored=("Omar Adel",)))
    assert result.assignments["s5"].status == "absent" and result.unmatched == []


def test_name_memory_accepts_and_rejects():
    names = seen("Moh Os", "Omar Adel")
    remembered = [AliasMemory("s1", normalize("Moh Os"), True), AliasMemory("s5", normalize("Omar Adel"), False)]
    result = match(ROSTER, names, remembered)
    assert (result.assignments["s1"].status, result.assignments["s1"].source) == ("present", "memory")
    assert result.assignments["s5"].status == "absent"                       # rejected: never this student again
    assert "p1" in result.unmatched


def test_manual_decisions_win_and_stay():
    names = seen("Mohab Osama", "Omar Adel")
    result = match(ROSTER, names, manual=[ManualDecision("s5", "p0", "present"), ManualDecision("s1", None, "absent")])
    assert (result.assignments["s5"].participant_id, result.assignments["s5"].manual) == ("p0", True)
    assert result.assignments["s1"].status == "absent" and result.assignments["s1"].manual
    # Omar's own exact name is his too: a second name of the same (present) student, nobody else's
    assert result.assignments["s5"].extra_participant_ids == ["p1"] and result.unmatched == []


# =========================================================================== presence timing


def snaps(*frames: tuple[int, list[str], bool]) -> list[SnapshotView]:
    return [SnapshotView(T0 + timedelta(minutes=m), names, complete) for m, names, complete in frames]


def test_join_leave_and_duration_from_consecutive_reads():
    result = presence(snaps((0, ["Mohab Osama"], True), (2, ["Mohab Osama", "Sara Khaled"], True),
                            (4, ["Sara Khaled"], True), (6, ["Sara Khaled", "Mohab Osama"], True)))
    mohab, sara = result[normalize("Mohab Osama")], result[normalize("Sara Khaled")]
    # Mohab: 0 -> left between 2 and 4 (midpoint 3), back between 4 and 6 (midpoint 5) -> still there at 6
    assert mohab.intervals == [(T0, T0 + timedelta(minutes=3)), (T0 + timedelta(minutes=5), T0 + timedelta(minutes=6))]
    assert mohab.seconds == 4 * 60 and mohab.sightings == 3
    assert sara.intervals == [(T0 + timedelta(minutes=1), T0 + timedelta(minutes=6))]


def test_an_incomplete_read_never_ends_a_presence():
    result = presence(snaps((0, ["Mohab Osama"], True), (2, [], False), (4, ["Mohab Osama"], True)))
    assert result[normalize("Mohab Osama")].intervals == [(T0, T0 + timedelta(minutes=4))]


def test_still_present_at_the_end_counts_until_the_meeting_ends():
    result = presence(snaps((0, ["Omar Adel"], True), (10, ["Omar Adel"], True)), session_end=T0 + timedelta(minutes=15))
    assert result[normalize("Omar Adel")].seconds == 15 * 60


# =========================================================================== roster import


def test_a_roster_pasted_from_excel_keeps_its_columns():
    parsed = parse_roster("Order\tName\tEmail\tStudentId\n1\tMohab Osama\tmohab@example.com\t1001\n2\tSara Ali\t\t1002\n")
    assert [(r.order, r.full_name, r.email, r.external_id) for r in parsed.rows] == [
        (1, "Mohab Osama", "mohab@example.com", "1001"), (2, "Sara Ali", None, "1002")]


def test_extension_roster_bugs_are_fixed():
    assert [r.full_name for r in parse_roster('Name,Email\n"Ali, Ahmed Hassan",ali@example.com\n').rows] == ["Ali, Ahmed Hassan"]
    assert [r.full_name for r in parse_roster("12345,Ahmed Mohamed\n").rows] == ["Ahmed Mohamed"]   # the id is not the name
    assert [r.full_name for r in parse_roster("Name,Email,Group\nMona Adel,m@example.com,S7\n").rows] == ["Mona Adel"]  # headers are no students
    parsed = parse_roster("1. Mohab Osama\n2) Sara Ali Joined\n• Omar Khaled\nJoined\nName\nmohab  osama\n")
    assert [r.full_name for r in parsed.rows] == ["Mohab Osama", "Sara Ali", "Omar Khaled"]
    assert len(parsed.skipped) == 3


def test_arabic_headers_are_recognised():
    parsed = parse_roster("الاسم,البريد\nأحمد محمد علي,a@example.com\n")
    assert [(r.full_name, r.email) for r in parsed.rows] == [("أحمد محمد علي", "a@example.com")]

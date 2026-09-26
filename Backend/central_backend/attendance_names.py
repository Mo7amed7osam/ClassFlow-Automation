"""Names: cleaning Zoom display names, normalising for comparison, and scoring a Zoom name against a
roster name. Pure functions, no database.

It merges the two earlier implementations and fixes their known gaps:
* the Windows app's rules (NameNormalizer / RuleBasedNameMatcher): name-variant spellings,
  Arabizi digits, and the first/family-name rules with their scores;
* the Chrome extension's fuzzy similarity (token overlap and character-bigram Dice), with the
  duplicate-token inflation fixed ("Mohamed Mohamed Ali" no longer scores 1.0 against
  "Mohamed Ali Hassan"), accents dropped rather than turned into spaces, tatweel, Persian ی/ک and
  Arabic-Indic digits folded, and honorific titles ignored.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

MAX_NAME = 300

# --------------------------------------------------------------------------- cleaning Zoom names

# Zoom's own annotations after a display name: "(Host)", "(Co-host, me)", "(مضيف مشارك)", ...
_ROLE_WORDS = {"host", "co-host", "cohost", "co host", "me", "guest", "مضيف", "مضيف مشارك", "أنا", "انا", "ضيف"}
_ROLE_GROUP = re.compile(r"\(([^()]{1,60})\)")
_STATUS_WORDS = re.compile(r"\b(?:un)?muted\b|\bspeaking\b|\braised? hand\b|\bvideo (?:on|off)\b|\bcomputer audio\b", re.IGNORECASE)
STAFF_ROLES = {"host", "co-host", "cohost", "co host", "مضيف", "مضيف مشارك"}


@dataclass(frozen=True)
class CleanName:
    name: str                 # what is shown and stored
    roles: frozenset[str]     # Zoom annotations that were removed (lower-case)

    @property
    def is_staff(self) -> bool:
        return bool(self.roles & STAFF_ROLES) or is_globally_ignored(self.name)


def clean_display_name(raw: str) -> CleanName:
    """A Zoom participant label without Zoom's annotations. The Windows app's UI Automation labels
    look like "Name,(Guest), Computer audio muted": everything from the first comma is Zoom's."""
    text = (raw or "").replace("\u200f", "").replace("\u200e", "").strip()
    roles: set[str] = set()

    def strip_role(match: re.Match[str]) -> str:
        parts = [p.strip().lower() for p in match.group(1).split(",")]
        if parts and all(p in _ROLE_WORDS for p in parts):
            roles.update(parts)
            return " "
        return match.group(0)

    text = _ROLE_GROUP.sub(strip_role, text)
    if "," in text:
        text = text.split(",", 1)[0]
    text = _STATUS_WORDS.sub(" ", text)
    text = re.sub(r"\s+", " ", text).strip(" -–—|•·")
    return CleanName(text[:MAX_NAME], frozenset(roles))


# --------------------------------------------------------------------------- normalising

_FOLD = {
    "أ": "ا", "إ": "ا", "آ": "ا", "ٱ": "ا",   # أ إ آ ٱ -> ا
    "ى": "ي",                                                                  # ى -> ي
    "ة": "ه",                                                                  # ة -> ه
    "ی": "ي", "ک": "ك", "ۀ": "ه",                          # Persian ی ک ۀ
}
_ARABIZI = (("3", "a"), ("7", "h"), ("5", "kh"), ("6", "t"), ("8", "gh"), ("9", "s"), ("2", "a"))
_RAW_VARIANTS = {
    "mohammed": "mohamed", "mohammad": "mohamed", "muhammad": "mohamed", "muhammed": "mohamed", "mohamad": "mohamed",
    "mhmd": "mohamed", "محمد": "mohamed", "ahmad": "ahmed", "احمد": "ahmed", "مهاب": "mohab", "اسامة": "osama",
    "اسامه": "osama", "usama": "osama", "ossama": "osama", "سيد": "sayed", "sayyed": "sayed", "saied": "sayed",
    "سعيد": "saeed", "said": "saeed", "علي": "ali", "aly": "ali", "حسن": "hassan", "hasan": "hassan", "عمر": "omar",
    "umar": "omar", "يوسف": "youssef", "yousef": "youssef", "yusuf": "youssef", "youssif": "youssef",
    "خالد": "khaled", "khalid": "khaled", "ابراهيم": "ibrahim", "ebrahim": "ibrahim", "mostafa": "mustafa",
    "moustafa": "mustafa", "مصطفى": "mustafa", "مصطفي": "mustafa", "mahmoud": "mahmoud", "mahmud": "mahmoud",
    "محمود": "mahmoud", "abdelrahman": "abdulrahman", "abdalrahman": "abdulrahman", "abdurrahman": "abdulrahman",
    "عبدالرحمن": "abdulrahman", "عبدالله": "abdullah", "abdulla": "abdullah", "fatma": "fatima", "fatmah": "fatima", "فاطمه": "fatima", "فاطمة": "fatima",
    "مريم": "mariam", "maryam": "mariam", "nour": "nour", "noor": "nour", "نور": "nour",
}
# Honorifics are ignored, unless the whole name is one ("Dr." alone stays "dr").
_TITLES = {"dr", "eng", "engr", "mr", "mrs", "ms", "miss", "prof", "sir", "د", "م", "ا", "دكتور", "دكتوره", "مهندس",
           "مهندسه", "استاذ", "استاذه", "الاستاذ", "الدكتور", "المهندس"}


def _fold_chars(value: str) -> str:
    out: list[str] = []
    for ch in unicodedata.normalize("NFKD", value).lower():
        category = unicodedata.category(ch)
        if category in ("Mn", "Me", "Cf") or ch == "\u0640":          # diacritics, joiners, tatweel
            continue
        ch = _FOLD.get(ch, ch)
        if category == "Nd":                                          # Arabic-Indic digits -> 0-9
            ch = str(unicodedata.decimal(ch, 0))
        out.append(ch if ch.isalpha() or ch.isdigit() else " ")
    return "".join(out)


_VARIANTS = {" ".join(_fold_chars(k).split()): v for k, v in _RAW_VARIANTS.items()}


def _canonical(token: str) -> str:
    if any("a" <= c <= "z" for c in token):                          # Arabizi only inside Latin words
        for digit, letters in _ARABIZI:
            token = token.replace(digit, letters)
    return _VARIANTS.get(token, token)


# Compound names are one name however they are spaced: عبد الرحمن / عبدالرحمن, Abd El Rahman /
# Abdelrahman / Abdul Rahman, Nour El Din / Noureldin / نور الدين, Abu Bakr / Aboubakr, El Sayed /
# Elsayed / Al Sayed. Each spelling is brought to one form, on both sides of every comparison.
_AR_JOIN_NEXT = {"عبد", "ابو"}                        # joined to the word after them
_AR_JOIN_PREVIOUS = {"الدين", "الله", "الاسلام"}      # joined to the word before them
_ABD = {"abd", "abdel", "abdal", "abdul", "abdoul", "abdl"}
_ABU = {"abu", "abou", "abo"}
_PARTICLE = {"el", "al", "ul", "ol", "ar", "er", "ur", "as", "es", "an", "en", "ad", "ed", "ud"}
_ABD_JOINED = re.compile(r"^abd(?:el|al|ul|oul|ol|ar|er|as|es|an|en)?([a-z]{3,})$")
_ABU_JOINED = re.compile(r"^(?:abou|abu)([a-z]{3,})$")
_DIN = re.compile(r"^(?:el|al|ed|ad|ud|e)?d(?:ee|i)ne?$")
_DIN_JOINED = re.compile(r"^([a-z]{3,}?)(?:el|al|ed|ad|ud|e)?d(?:ee|i)ne?$")


def _join_compounds(words: list[str]) -> list[str]:
    out: list[str] = []
    i = 0
    while i < len(words):
        w = words[i]
        after = words[i + 1] if i + 1 < len(words) else None
        if after is not None and w in _AR_JOIN_NEXT:
            out.append(w + after)
            i += 2
            continue
        if out and w in _AR_JOIN_PREVIOUS:
            out[-1] += w
            i += 1
            continue
        if after is not None and w in _ABD | _ABU:
            j = i + 1
            if words[j] in _PARTICLE and j + 1 < len(words):
                j += 1
            rest = re.sub(r"^(?:el|al|ul)(?=[a-z]{3,})", "", words[j])
            out.append(("abdul" if w in _ABD else "abu") + rest)
            i = j + 1
            continue
        if out and (_DIN.match(w) or (w in _PARTICLE and after is not None and _DIN.match(after))):
            out[-1] += "eldin"
            i += 1 if _DIN.match(w) else 2
            continue
        if after is not None and w in ("el", "al") and after.isalpha():
            out.append("el" + after)
            i += 2
            continue
        if m := _ABD_JOINED.match(w):
            w = "abdul" + m.group(1)
        elif m := _ABU_JOINED.match(w):
            w = "abu" + m.group(1)
        elif len(w) >= 7 and (m := _DIN_JOINED.match(w)):
            w = m.group(1) + "eldin"
        elif len(w) >= 5 and w.startswith("al") and w.isascii():
            w = "el" + w[2:]
        out.append(w)
        i += 1
    return out


def normalize(value: str | None) -> str:
    """The comparable form of a name: folded, lower-case, words separated by one space."""
    if not value or not value.strip():
        return ""
    words = [_canonical(t) for t in _join_compounds(_fold_chars(value[:MAX_NAME]).split())]
    without_titles = [w for w in words if w not in _TITLES]
    return " ".join(without_titles or words)


GLOBAL_IGNORED_NAMES: frozenset[str] = frozenset({
    "eyouth coordinator",
    "depi wavz",
    "youssef ayoub",
    "yossef ayoub",
    "mohamed hosam",
})


def is_globally_ignored(value: str | None) -> bool:
    """True if the display name belongs to global staff, coordinators, or ignored monitors."""
    if not value or not value.strip():
        return False
    norm = normalize(value)
    if not norm:
        return False
    for ign in GLOBAL_IGNORED_NAMES:
        ign_norm = normalize(ign)
        if ign_norm == norm or ign_norm in norm or norm in ign_norm:
            return True
    return False


def tokens(value: str) -> list[str]:
    n = normalize(value)
    return n.split(" ") if n else []


def is_full_name(value: str) -> bool:
    """At least two different words of two letters or more: enough to identify someone alone."""
    words = tokens(value)
    return len(set(words)) >= 2 and all(len(w) >= 2 and w.isalpha() for w in words)


# --------------------------------------------------------------------------- scoring


def rule_score(roster_name: str, observed: str) -> tuple[int, str]:
    """The Windows app's first/family-name rules (0-100)."""
    r, o = tokens(roster_name), tokens(observed)
    if not r or not o:
        return 0, "No comparable names."
    first, family = r[0] == o[0], r[-1] == o[-1]
    if len(o) == 1:
        if first:
            return 55, "First name only; identity is incomplete."
        if family:
            return 35, "Family name only; insufficient evidence."
        return (25, "One middle name only.") if o[0] in r else (0, "No name overlap.")
    if is_full_name(observed):
        if r == o:
            return 100, "Exact normalized full name."
        if len(r) >= len(o) and r[: len(o)] == o:
            return (98 if len(o) >= 3 else 95), "The observed name is the start of the full name."
    if first and len(o) == 2 and family:
        return 88, "First and family names; middle names omitted."
    if first and len(r) > 1 and o[1] == r[1]:
        return 75, "First and second names agree but other names differ."
    if first:
        return 60, "First name agrees; other names need review."
    if len(r) > 1 and r[1] in o:
        return 45, "Second-name evidence without matching first name."
    if family:
        return 35, "Family name only; insufficient evidence."
    return (25, "Middle-name overlap only.") if any(t in r for t in o) else (0, "No name overlap.")


def _bigrams(value: str) -> set[str]:
    s = value.replace(" ", "")
    return {s[i : i + 2] for i in range(len(s) - 1)}


def fuzzy_score(roster_name: str, observed: str) -> int:
    """The extension's similarity (0-100): the better of word overlap and character-bigram Dice.
    Words are compared as sets, so a repeated word counts once."""
    a, b = normalize(roster_name), normalize(observed)
    if not a or not b:
        return 0
    if a == b:
        return 100
    ta = {t for t in a.split(" ") if len(t) > 1}
    tb = {t for t in b.split(" ") if len(t) > 1}
    token = len(ta & tb) / max(len(ta), len(tb), 1)
    ba, bb = _bigrams(a), _bigrams(b)
    dice = (2 * len(ba & bb) / (len(ba) + len(bb))) if (ba or bb) else 0.0
    return round(100 * max(token, dice))


@dataclass(frozen=True)
class Score:
    value: int      # 0-100
    source: str     # exact | alias | rule | fuzzy
    reason: str


SINGLE_WORD_CAP = 60   # one word ("Ahmed", "iPhone") never identifies a student on its own


def score(roster_name: str, roster_aliases: list[str], observed: str) -> Score:
    """How well a Zoom name fits a roster student, from the name itself and the roster's aliases."""
    n_obs = normalize(observed)
    if not n_obs:
        return Score(0, "rule", "Empty name.")
    if normalize(roster_name) == n_obs:
        return Score(100, "exact", "Exact normalized full name.")
    if any(normalize(a) == n_obs for a in roster_aliases if a):
        return Score(100, "alias", "An alias on the roster.")
    rule_value, reason = rule_score(roster_name, observed)
    fuzzy_value = fuzzy_score(roster_name, observed)
    if len(n_obs.split(" ")) == 1:
        fuzzy_value = min(fuzzy_value, SINGLE_WORD_CAP)
    if fuzzy_value > rule_value:
        return Score(fuzzy_value, "fuzzy", "Similar spelling.")
    return Score(rule_value, "rule", reason)

"""Changing the footage at a moment the pharmacist chose.

<b>The pharmacist chooses the shot; nothing here guesses it.</b> The refine
dialog shows every moment of the video as a thumbnail. The pharmacist taps the
ones to change and types what each should show, and that arrives as
``[{index, show}]`` (`from_picker`). A typed request may name a shot only by
position — "first", "last", "second", "shot 3" (`from_typed`) — read by code,
not by a model.

This replaced a model that matched the request against a description of each
shot. Those descriptions are stock-library page titles, often vague or wrong,
and the model believed them over the pharmacist: "remove the last shot which
shows medicine" was refused with "no moment shows medicine — the last moment
shows employees on a couch". The pharmacist had watched the video. A request
that names no position is now answered with "tap the shot", never a guess.

<b>A moment cannot be removed, only re-filled.</b> The video is exactly as long
as its narration, so deleting a shot would leave the voice talking over nothing.
"Remove the last shot" means "put something else there"; with nothing said
about what, the video's own subject is searched.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from typing import Any

log = logging.getLogger(__name__)

#: More than this in one instruction is a rewrite, not an edit. The cap exists
#: so a runaway reply cannot turn one request into thirty stock searches.
MAX_EDITS = 6

#: Long enough for "an elderly couple walking in a park", short enough that a
#: whole sentence cannot be pasted into a stock search that then matches nothing.
MAX_QUERY_CHARS = 80


@dataclass
class ShotEdit:
    """Give beat `index` different footage, found by searching for `query`."""

    index: int
    query: str


@dataclass
class ShotEdits:
    edits: list[ShotEdit] = field(default_factory=list)

    #: Parts of the request this cannot do — anything needing the words, the
    #: voice or the length to change. Carried so the caller can say which part,
    #: rather than refusing the whole sentence or silently doing less than asked.
    unsupported: list[str] = field(default_factory=list)

    def is_empty(self) -> bool:
        return not self.edits

    def by_index(self) -> dict[int, str]:
        """`{beat: query}`.

        A beat named twice keeps the *first* answer — `parse` drops the repeat
        before it reaches here. Worth stating because the dict comprehension
        below would do the opposite on its own, and a later reader changing
        `parse` would have no reason to think this depended on it.
        """
        return {e.index: e.query for e in self.edits}


def _clean_query(raw: Any) -> str:
    """A stock search, or empty when there is nothing usable."""
    text = " ".join(str(raw or "").split())
    # Punctuation helps nobody searching a stock library and quotes would break
    # nothing but read oddly in the plan the pharmacist is shown.
    text = re.sub(r"[^\w\s-]", " ", text)
    text = " ".join(text.split())[:MAX_QUERY_CHARS]

    # A search with no letters in it is not a search. `7` or `2024` survives
    # every check above and then asks a stock library for whatever it thinks
    # "7" looks like — which is footage nobody chose, in a video somebody had
    # already approved.
    return text if re.search(r"[^\W\d_]", text) else ""


def parse(payload: Any, segment_count: int) -> ShotEdits:
    """Validate `{"edits": [{"index", "query"}]}` into edits, dropping anything unusable.

    Every edit arrives from outside this process — the picker via the API, or
    a typed request — so each rule below holds the line: a moment that does not
    exist, a repeated moment, a non-number index, an empty search.
    """
    result = ShotEdits()
    if not isinstance(payload, dict):
        return result

    raw_edits = payload.get("edits")
    if isinstance(raw_edits, list):
        seen: set[int] = set()
        for item in raw_edits:
            if len(result.edits) >= MAX_EDITS:
                log.warning("Shot edits: ignoring beyond %d changes in one request", MAX_EDITS)
                break
            if not isinstance(item, dict):
                continue

            index = item.get("index")
            # `bool` is an `int` in Python, and `True` would index beat 1.
            if isinstance(index, bool) or not isinstance(index, int):
                continue
            if not 0 <= index < segment_count:
                # A moment that does not exist. Dropping it is better than
                # applying it to whichever beat the number happens to hit.
                log.warning("Shot edits: moment %r is not in this video", index)
                continue
            if index in seen:
                continue

            query = _clean_query(item.get("query"))
            if not query:
                continue

            seen.add(index)
            result.edits.append(ShotEdit(index=index, query=query))

    raw_unsupported = payload.get("unsupported")
    if isinstance(raw_unsupported, list):
        result.unsupported = [
            " ".join(str(u).split())[:200] for u in raw_unsupported if str(u).strip()
        ][:MAX_EDITS]

    return result


# ── choosing a shot without guessing ────────────────────────────────────
#
# A shot is chosen by the pharmacist, never inferred from what they say is in
# it. The picker sends `{index, show}` for the tile they tapped; a typed request
# may name a shot only by position — "first", "last", "second", "shot 3". Both
# are read here by code, not by a model. What a clip "shows" comes from a stock
# library's page title and was wrong often enough to refuse real requests
# ("no moment currently shows medicine" — the pharmacist had watched it).

_ORDINALS = {
    "first": 1, "1st": 1, "opening": 1, "second": 2, "2nd": 2, "third": 3, "3rd": 3,
    "fourth": 4, "4th": 4, "fifth": 5, "5th": 5, "sixth": 6, "6th": 6,
    "seventh": 7, "7th": 7, "eighth": 8, "8th": 8, "ninth": 9, "9th": 9,
    "tenth": 10, "10th": 10, "eleventh": 11, "11th": 11, "twelfth": 12, "12th": 12,
}
_NOUN = r"(?:shot|clip|scene|moment|video|part|bit|image|picture|one)s?"
_POSITION = re.compile(
    r"\b(?:(?P<pen>second[\s-]+to[\s-]+last|penultimate)"
    r"|(?P<last>last|final|ending)"
    r"|(?P<ord>" + "|".join(_ORDINALS) + r"))\b"
    # "the first shot", and also "the first and last shots": a position word
    # counts when a shot noun follows it, possibly after a list of others.
    r"(?=(?:\s*(?:,|and|&)\s*(?:the\s+)?[\w-]+)*\s+" + _NOUN + r"\b)"
    r"|\b" + _NOUN + r"\s*(?:no\.?|number|#)?\s*(?P<num>\d{1,2})\b"
    r"|#(?P<hash>\d{1,2})\b",
    re.I,
)
# Where the replacement starts. "shows"/"showing" are deliberately absent: in
# "the last shot showing medicine" they describe what is there now.
_TARGET = re.compile(
    r"\b(?:to|with|into|for|about|(?:should|to|so it|so that it)\s+show)\b\s+(?P<rest>.+)$", re.I)
_FILLER = re.compile(
    r"^(?:(?:something|anything|some footage|footage|a shot|a clip|a video|a scene|an? image|a picture)"
    r"\s*(?:that\s*(?:is|'s)\s*|that\s+shows\s+)?(?:related\s+to|about|showing|of|with|on)?\s+)+",
    re.I,
)


def positions(instruction: str, count: int) -> list[int] | None:
    """Which moments a typed request names by position, or None if it names none.

    Only positions: first / second / … / last / second-to-last / "shot 3" /
    "#3". A request naming no position — "the one with medicine" — returns
    None, and the caller asks the pharmacist to tap the shot instead of guessing.
    Positions past the end are dropped.
    """
    found: list[int] = []
    for m in _POSITION.finditer(instruction or ""):
        if m.group("pen"):
            index = count - 2
        elif m.group("last"):
            index = count - 1
        elif m.group("ord"):
            index = _ORDINALS[m.group("ord").lower()] - 1
        else:
            index = int(m.group("num") or m.group("hash")) - 1
        if 0 <= index < count and index not in found:
            found.append(index)
    return found or None


def query_from(text: str) -> str:
    """The stock search in what somebody asked a shot to show.

    "change the last shot to something related to weight management" →
    "weight management". Words that only point at a shot ("the last shot to")
    and filler ("something related to") are dropped; what is left is searched
    as written. Empty when nothing is left — the caller then searches the
    video's subject.
    """
    text = " ".join(str(text or "").split())
    named = list(_POSITION.finditer(text))
    after = text[named[-1].end():] if named else text
    target = _TARGET.search(after)
    if target:
        rest = target.group("rest")
    else:
        # A shot named with nothing to put there ("change the last shot"), or
        # picker text that is only the replacement ("a pharmacist").
        rest = "" if named else text
    rest = _FILLER.sub("", rest).strip(" .!?,;:")
    return _clean_query(rest)


#: What a refine that names no shot is told. The one place the wording lives.
TAP_THE_SHOT = (
    "Tap the shot you want to change in the list, or name it by position — "
    "\"the first shot\", \"the last shot\", \"shot 3\". We don't pick a shot "
    "from a description of what is in it."
)


def _search_for(show: str, topic: str) -> str:
    """The stock search for a replacement: what was asked for, else the subject."""
    return query_from(show) or _clean_query(topic)


def from_picker(items: Any, count: int, topic: str) -> ShotEdits:
    """Edits for the tiles the pharmacist tapped: ``[{"index", "show"}]``.

    Taken as given — the index is the tile, so nothing about what the shot
    "shows" can overrule it. Validated by `parse` all the same, because it
    crossed two services on the way here.
    """
    edits = [
        {"index": item.get("index"), "query": _search_for(item.get("show"), topic)}
        for item in (items if isinstance(items, list) else [])
        if isinstance(item, dict)
    ]
    return parse({"edits": edits}, count)


def from_typed(instruction: str, count: int, topic: str) -> ShotEdits | None:
    """Edits for a typed request that names shots by position; None if it names none."""
    named = positions(instruction, count)
    if not named:
        return None
    query = _search_for(instruction, topic)
    return parse({"edits": [{"index": i, "query": query} for i in named]}, count)

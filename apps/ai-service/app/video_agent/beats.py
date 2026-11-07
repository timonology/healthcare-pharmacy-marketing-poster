"""Which footage each moment of a video shows, recorded so a refine can find it.

A video is a list of moments ("beats"), one per script segment. The render
records where every stock clip came from (`clipSources` on the document) so a
refine can fetch the same footage again instead of storing the files.

**Each record says which moment it belongs to.** The records used to be a flat
list, and a refine paired the nth *aligned* record with the nth moment. That
holds only while every moment downloaded its own clip. Two ordinary things
break it:

* a moment showing the pharmacy's own photograph has no stock record at all;
* a moment whose search found nothing borrows a clip from elsewhere in the
  video, and borrowing recorded nothing.

After the first such moment every later record is one place early. The
consequences were all reported as "the change did nothing" or "it changed the
wrong thing": a caption re-cut dropped the pharmacist's photo and slid every
stock shot one moment earlier; a shot edit kept the wrong shots; and the model
asked "which moment shows the person washing their hands?" could not be told,
because nothing linked the footage to a moment.

So every moment now gets exactly one record, tagged `segment`:

* ``role: "aligned"`` — stock footage, with `link` to fetch it again, `shows`
  describing what is in it, and `borrowed: True` if it is a copy of a clip
  downloaded for something else;
* ``role: "user"`` — the pharmacy's own photograph, rebuilt from `images` on the
  document rather than fetched.

Fill clips that belong to no moment keep ``role: "extra"``.

Records written before this existed have no `segment`. They are read back
best-effort (`by_beat`): pinned-photo moments are known from the document and
skipped, and the rest are assumed to be in order — which is exact unless the
first render also had a failed search, and never worse than before.

Pure functions, no I/O: the node and the refine both use them, and the tests
exercise them without moviepy or a network.
"""
from __future__ import annotations

import re
from typing import Any

#: The Pexels page slug is the only plain-English description of a clip that
#: the API returns: https://www.pexels.com/video/a-woman-washing-her-hands-3997798/
_SLUG = re.compile(r"/video/(?P<slug>[^/?#]+?)(?:-\d+)?/?(?:[?#].*)?$")


def describe(pexels_url: Any) -> str:
    """What a Pexels clip shows, from its page URL. Empty when it does not say."""
    match = _SLUG.search(str(pexels_url or ""))
    if not match:
        return ""
    slug = match.group("slug")
    if slug.isdigit():  # "/video/3997798/" carries no description
        return ""
    return " ".join(slug.replace("-", " ").split())[:120]


def record_by_beat(
    segment_count: int,
    beat_paths: list[str],
    user_beats: dict[int, int],
    own: dict[int, dict],
    all_records: list[dict],
) -> list[dict]:
    """One record per moment, in moment order, for storing on the document.

    `beat_paths` is the clip each moment ended up with. `own` is the record of
    the clip a moment downloaded or kept for itself. A moment with neither was
    filled with a clip from elsewhere, which is found by its path among
    `all_records` and recorded as borrowed — the link is the same file, so a
    refine can fetch it just the same.

    `user_beats` is ``{moment: index into the video's images}`` for the moments
    that showed one of the pharmacy's photographs.
    """
    by_path = {r.get("path"): r for r in all_records if isinstance(r, dict) and r.get("path")}
    out: list[dict] = []
    for i in range(segment_count):
        if i in user_beats:
            out.append({"segment": i, "role": "user", "image": user_beats[i]})
            continue
        mine = own.get(i)
        borrowed = False
        if not mine:
            path = beat_paths[i] if i < len(beat_paths) else ""
            mine = by_path.get(path)
            borrowed = mine is not None
        if not mine:
            # No clip we can name. Left out rather than invented: `by_beat` reads
            # the gap as "search this moment again", which is honest.
            continue
        record = {k: v for k, v in mine.items() if k not in ("segment", "borrowed")}
        record.update({"segment": i, "role": "aligned"})
        if borrowed:
            record["borrowed"] = True
        out.append(record)
    return out


def by_beat(
    records: list[dict],
    segment_count: int,
    pinned: set[int] | frozenset[int] = frozenset(),
) -> tuple[list[dict | None], list[dict]]:
    """Stored records laid back out as ``(one per moment, extras)``.

    A moment with no stock record comes back as None: a photograph moment (the
    caller rebuilds it) or one that must be searched again.

    `pinned` is only consulted for records written before moments were tagged,
    to step over the photograph moments that recorded nothing.
    """
    per_beat: list[dict | None] = [None] * max(0, segment_count)
    extras: list[dict] = []
    legacy: list[dict] = []

    for record in records:
        if not isinstance(record, dict):
            continue
        role = record.get("role")
        if role == "extra":
            extras.append(record)
            continue
        segment = record.get("segment")
        if isinstance(segment, int) and not isinstance(segment, bool):
            if 0 <= segment < segment_count and role == "aligned":
                per_beat[segment] = record
            # role "user", or a moment the script no longer has: nothing to fetch.
            continue
        if role == "aligned":
            legacy.append(record)
        else:
            # Older still: no role at all. Treated as fill, as it always was.
            extras.append(record)

    if legacy:
        free = (i for i in range(segment_count) if i not in pinned)
        for record, i in zip(legacy, free):
            per_beat[i] = record

    return per_beat, extras


# ── not showing the same footage twice ──────────────────────────────────
#
# Pharmacists reported "a duplicate video": the same shot appearing twice. It
# had four causes. A moment whose search failed borrowed `pool[i % len(pool)]`
# from a pool that *started* with clips already on screen. The editor topped
# up short footage by cycling the same pool. Every reuse cut the identical
# middle section, so a repeat was frame-for-frame. And a refine's new searches
# did not exclude clips the video already had. The first three are answered
# here; the fourth where the searches run.


def fill_gaps(beat_paths: list[str], stock: list[str], spares: list[str]) -> list[str]:
    """Give every empty moment a clip, repeating as little as possible.

    Unused spare clips go first — they were downloaded for exactly this and
    are not on screen anywhere. After that, the least-used clip that is not on
    either neighbouring moment, so a repeat is at least not back to back.
    Returns a new list; moments that already have a clip are untouched.
    """
    out = list(beat_paths)
    uses: dict[str, int] = {}
    for p in out:
        if p:
            uses[p] = uses.get(p, 0) + 1

    unused = [p for p in spares if p and p not in uses]
    candidates = [p for p in dict.fromkeys(list(stock) + list(spares)) if p]

    for i, p in enumerate(out):
        if p:
            continue
        if unused:
            choice = unused.pop(0)
        elif candidates:
            neighbours = {out[j] for j in (i - 1, i + 1) if 0 <= j < len(out) and out[j]}
            apart = [c for c in candidates if c not in neighbours] or candidates
            # Least used first; ties keep pool order so the result is stable.
            choice = min(apart, key=lambda c: (uses.get(c, 0), candidates.index(c)))
        else:
            continue
        out[i] = choice
        uses[choice] = uses.get(choice, 0) + 1
    return out


def subclip_start(duration: float, target: float, use: int) -> float:
    """Where to cut `target` seconds from a clip on its `use`-th appearance.

    The first use takes the middle, as it always did. A clip used again starts
    somewhere else in it — the opening, then the end, then points between — so
    a repeat shows different frames instead of an identical copy. A clip too
    short to offer a second window still starts at the middle; nothing better
    exists in it.
    """
    spare = duration - target
    if spare <= 0.1:
        return 0.0
    middle = max(0.1, spare * 0.5)
    middle = min(middle, spare - 0.05)
    if use <= 0 or spare < target * 0.5:
        return middle
    # Opening, end, then quarter points: as far from the middle as the clip
    # allows, and from each other.
    spots = [0.05, spare - 0.05, spare * 0.25, spare * 0.75]
    return max(0.0, min(spots[(use - 1) % len(spots)], spare - 0.05))


def recorded_photos(records: Any, segment_count: int) -> dict[int, int | None] | None:
    """``{moment: image index}`` for the photographs the last render really showed.

    None when the records predate moment tags, so the caller falls back to
    planning. **This, not the list of images, is the truth after a render.**
    The image list can hold pictures that were never shown: .NET adds them when
    an "add pictures" change is queued, and until now nothing took them off if
    that change was refused. Planning from the list then called the first and
    last moments photographs in a video that showed stock there, and refused to
    change "the last video" because it was "your own photo".

    An index is None for records written before the index was stored; the
    caller fills it from the plan.
    """
    if not isinstance(records, list):
        return None
    tagged = [r for r in records if isinstance(r, dict)
              and isinstance(r.get("segment"), int) and not isinstance(r.get("segment"), bool)]
    if not tagged:
        return None
    photos: dict[int, int | None] = {}
    for r in tagged:
        if r.get("role") == "user" and 0 <= r["segment"] < segment_count:
            image = r.get("image")
            photos[r["segment"]] = image if isinstance(image, int) and not isinstance(image, bool) else None
    return photos


#: Added to a clip's use count when it will not open, so least-used selection
#: stops offering it without forgetting the order of everything else.
UNOPENABLE = 1_000


def pick_topup(pool: list[str], uses: dict[str, int], turn: int) -> str:
    """The clip to extend short footage with: the least shown so far.

    Replaced a fixed rotation that began with clips already on screen, which is
    how topping up the length became a repeated shot. Ties go round in pool
    order from `turn`, so equally-used clips take turns and a clip that failed
    to open (penalised by `UNOPENABLE`) is not retried back to back.
    """
    return min(
        pool,
        key=lambda p: (uses.get(p, 0), (pool.index(p) - turn) % len(pool)),
    )


# ── every shot on screen is a moment ────────────────────────────────────
#
# The editor used to give each moment its *planned* length (the scriptwriter's
# estimate, clamped to 1.5–8s) and then, when the narration ran longer than
# the plan, append "top-up" clips from the spare pool until the picture caught
# up. Those clips belonged to no moment. A 4-moment script over 19.8s of
# narration rendered 6 shots; the refine only knew about 4, so "change the
# last shot" pointed at a shot the model had never been shown — and it
# refused, correctly, because the last *moment* did not show what was named.
#
# Now the real narration is shared out across the moments in proportion to
# their planned lengths. Nothing is left over, so nothing is appended, and the
# shots on screen are exactly the moments a refine can see and change.

#: A moment shorter than this reads as a flash, not a shot.
MIN_SHOT_SECONDS = 1.0


def fit_timeline(segments: list[dict], total: float) -> list[tuple[float, float]]:
    """``(start, duration)`` for each moment, filling exactly `total` seconds.

    Durations keep the proportions the script planned — a line planned twice
    as long as its neighbour still gets twice the screen time — scaled so they
    add up to the narration the voice actually produced. Unreadable timings
    count as an average moment rather than failing the render.
    """
    count = len(segments)
    if count == 0 or total <= 0:
        return []

    planned: list[float] = []
    for seg in segments:
        try:
            span = float(seg.get("end")) - float(seg.get("start"))
        except (TypeError, ValueError, AttributeError):
            span = 0.0
        planned.append(span if span > 0 else 0.0)
    known = [p for p in planned if p > 0]
    typical = sum(known) / len(known) if known else 1.0
    planned = [p if p > 0 else typical for p in planned]

    # Raise any moment below the minimum, then share the rest out by plan.
    floor = min(MIN_SHOT_SECONDS, total / count)
    scale = total / sum(planned)
    durations = [max(floor, p * scale) for p in planned]
    excess = sum(durations) - total
    if excess > 1e-9:
        flexible = [i for i, d in enumerate(durations) if d > floor]
        room = sum(durations[i] - floor for i in flexible)
        for i in flexible:
            durations[i] -= excess * (durations[i] - floor) / room

    timeline: list[tuple[float, float]] = []
    start = 0.0
    for d in durations:
        timeline.append((start, d))
        start += d
    # Rounding must not leave a sliver uncovered at the end.
    last_start, last = timeline[-1]
    timeline[-1] = (last_start, total - last_start)
    return timeline


def timed_segments(segments: list[dict], total: float) -> list[dict]:
    """The segments with `start`/`end` replaced by where they really fall.

    Captions are placed from these too, so a caption appears with the shot it
    belongs to rather than on the scriptwriter's estimate.
    """
    return [
        {**seg, "start": round(start, 3), "end": round(start + duration, 3)}
        for seg, (start, duration) in zip(segments, fit_timeline(segments, total))
    ]


# ── How many shots a video has ────────────────────────────────────────────
#
# One shot per script moment, so the number of moments *is* the number of
# shots. It used to be whatever the scriptwriter felt like: nothing asked for a
# count. That went unnoticed while scripts ran long — a 26-second script came
# back in five or six moments — and surfaced when the word budget brought a
# 15-second script down to ~36 words: the writer then often wrote three, and the
# tightening pass merged sentences to fit and took a four down to three.
#
# A photograph makes it worse, not by changing the count but by spending it:
# with three moments and one attached picture, two stock shots are left.

#: Seconds a shot should hold. 15s → 4 shots, 30s → 9, which is the pace the
#: product had when it looked right.
SECONDS_PER_SHOT = 3.5
MIN_SHOTS = 4
MAX_SHOTS = 10

#: Below this many words a moment is not split further; it would be a caption
#: of two words over a clip of one second.
_MIN_SPLIT_WORDS = 4


def shot_count(duration_seconds: float) -> int:
    """How many moments — and so shots — a clip of this length should have."""
    try:
        wanted = round(float(duration_seconds) / SECONDS_PER_SHOT)
    except (TypeError, ValueError):
        wanted = MIN_SHOTS
    return max(MIN_SHOTS, min(MAX_SHOTS, wanted))


def _split_point(words: list[str]) -> int:
    """Where to cut a moment in two: a sentence end, else a clause, else the middle.

    Nearest the middle wins within each kind, so the halves are close in length.
    Never at either end, so both halves keep at least two words.
    """
    middle = len(words) / 2
    candidates = range(2, len(words) - 1)
    for enders in (".!?", ",;:—–"):
        # Closing quotes and brackets do not hide the punctuation inside them:
        # `…today."` still ends a sentence.
        cuts = [i for i in candidates if words[i - 1].rstrip("\"'”’)]")[-1:] in enders]
        if cuts:
            return min(cuts, key=lambda i: abs(i - middle))
    return round(middle)


def ensure_shot_count(segments: list[dict], wanted: int) -> list[dict]:
    """The script's moments, split until there are at least `wanted`.

    A safety net under the prompt, which asks for the count but cannot make the
    model give it. The longest moment is split at its most natural boundary;
    the narration is unchanged — the moments are cut, not rewritten — so the
    voice, the captions and the joined script still agree word for word.

    The new half needs its own stock search, so it gets a keyword of its own:
    the original with a different framing. Two moments sharing one keyword
    would come back as the same clip twice.
    """
    out = [dict(s) for s in segments if isinstance(s, dict)]
    framings = ("close-up", "wide shot", "detail", "in use")
    turn = 0

    while 0 < len(out) < wanted:
        sizes = [len(str(s.get("text") or "").split()) for s in out]
        i = max(range(len(out)), key=lambda k: sizes[k])
        words = str(out[i].get("text") or "").split()
        if len(words) < _MIN_SPLIT_WORDS:
            break

        cut = _split_point(words)
        first, second = " ".join(words[:cut]), " ".join(words[cut:])

        try:
            start = float(out[i].get("start", 0))
            end = float(out[i].get("end", 0))
        except (TypeError, ValueError):
            start = end = 0.0
        middle = start + (end - start) * cut / len(words) if end > start else start

        keyword = str(out[i].get("visual_keyword") or "").strip()
        alternate = f"{keyword} {framings[turn % len(framings)]}".strip() if keyword else ""
        turn += 1

        out[i:i + 1] = [
            {**out[i], "text": first, "start": round(start, 3), "end": round(middle, 3)},
            {**out[i], "text": second, "start": round(middle, 3), "end": round(end, 3),
             "visual_keyword": alternate},
        ]
    return out


def replacement_queries(asked: str, topic: str) -> list[str]:
    """The stock searches for a shot the pharmacist asked to change, in order.

    What they asked for, then that with the video's subject attached. Nothing
    else: no subject-only search and no generic pharmacy list. If both miss,
    the caller reuses a clip already in this video (`fill_gaps` over the other
    moments), because a shot they chose must end up on-topic and never an
    unrelated stock default.
    """
    asked = " ".join(str(asked or "").split())
    topic = " ".join(str(topic or "").split())
    candidates = [asked, f"{asked} {topic}".strip() if asked and topic else ""]
    return [q for q in dict.fromkeys(candidates) if q]

"""
Validation for AI-generated canvas documents.

Schema validity is necessary but nowhere near sufficient. A canvas can satisfy
every Pydantic constraint and still be unprintable: type off the page, a photo
covering the headline, an invented image URL that 404s during PDF export, or
white text on a white background.

Every rule here was chosen because it catches a failure that is invisible in
JSON but obvious on paper. Violations are returned as plain-English strings and
fed back to the model for repair, so the wording is aimed at the model as much
as at us.
"""

from __future__ import annotations

import re
from typing import Any

from .design_system import (
    APPROVED_PHOTOS,
    CANVAS_HEIGHT,
    CANVAS_WIDTH,
    SAFE_MARGIN,
)

HEX_RE = re.compile(r"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")

# Unsplash asset id, e.g. "photo-1559757175-5700dde675bc".
_UNSPLASH_ID_RE = re.compile(r"(photo-[0-9a-z]+-[0-9a-z]+)", re.IGNORECASE)


def _photo_identity(url: str) -> str | None:
    """
    Identity of a stock photo, ignoring transform query parameters.

    The same asset legitimately appears at several widths across the gallery
    (?w=900, ?w=1000, ?w=1400), so comparing whole URLs rejected three existing
    templates that use exactly the approved photographs. What must be pinned is
    *which photograph* it is, not how it was resized.
    """
    m = _UNSPLASH_ID_RE.search(url or "")
    return m.group(1).lower() if m else None


#: Our own brand routes. A canvas carries these paths and every renderer
#: resolves them per pharmacy: the logo, and the booking QR (dropped cleanly
#: when the pharmacy has no booking link — no grey box).
BRAND_PATHS = ("/api/brand-kit/logo", "/api/brand-kit/booking-qr")

_APPROVED_IDS = {
    pid for pid in (_photo_identity(u) for u in APPROVED_PHOTOS.values()) if pid
}

MAX_SHAPES = 90

# Below this, a "headline" is really body copy and the poster reads as flat.
MIN_HEADLINE_FONT = 60.0

# The prompt asks for a 90px text margin (design_system.SAFE_MARGIN); the
# validator only fails below 32px. The gap is deliberate: the prompt states the
# target, the validator catches mistakes. Calibrated against the hand-authored
# gallery, which sets body type at x=80 and corner labels at x=40 quite happily
# — failing those would burn repair rounds on design decisions, not defects.
VALIDATION_MARGIN = 32

# Text this large is a display or decorative device (an oversized year numeral,
# a watermark word) and is allowed to bleed off the trim edge the way any other
# decoration can. Below it, text running off the page is always a defect.
DECORATIVE_TEXT_FONT = 100.0

# ...but a bleed is only a bleed if most of the word is still on the page. This
# separates "giant 2026 running off the right edge" (60% visible, intentional)
# from "headline positioned at x=1180 on a 1240px page" (6% visible, a mistake
# that the size exemption would otherwise wave straight through).
MIN_DECORATIVE_ON_PAGE = 0.5

# How far a decorative shape may bleed past the trim edge before it looks like
# an arithmetic error rather than a design choice. Real templates place circles
# at x=-110 and wedges spanning -120..1400 on purpose.
MAX_BLEED = 400

# Contrast floor.
#
# This is a LEGIBILITY check, not an accessibility guarantee — do not read a
# pass here as WCAG conformance, which would need 4.5:1 for body text and 3.0:1
# for large text.
#
# It sits at 2.2 because the existing gallery, which is the quality bar, leans
# on patterns that sit between 2.5 and 2.7: white on emerald #10b981 chips
# (2.5:1) and lilac numerals on white (2.7:1). Both are perfectly readable on a
# poster viewed at distance, and three templates use the emerald chip. A 3.0
# floor rejected 8 of 24 hand-authored designs, which would have meant the
# generator burning repair rounds fighting the house style.
#
# The failures this is actually here to stop — orange on off-white at 1.5:1,
# near-black on navy at 1.2:1 — are still caught with room to spare.
MIN_CONTRAST = 2.2


# --------------------------------------------------------------------------- #
# Colour helpers
# --------------------------------------------------------------------------- #

def _parse_hex(color: str) -> tuple[float, float, float] | None:
    if not isinstance(color, str) or not HEX_RE.match(color.strip()):
        return None
    h = color.strip().lstrip("#")
    if len(h) == 3:
        h = "".join(c * 2 for c in h)
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


def _relative_luminance(rgb: tuple[float, float, float]) -> float:
    """WCAG 2.1 relative luminance."""
    def channel(v: float) -> float:
        s = v / 255.0
        return s / 12.92 if s <= 0.04045 else ((s + 0.055) / 1.055) ** 2.4

    r, g, b = (channel(c) for c in rgb)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast_ratio(fg: str, bg: str) -> float | None:
    a, b = _parse_hex(fg), _parse_hex(bg)
    if a is None or b is None:
        return None
    la, lb = _relative_luminance(a), _relative_luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


# --------------------------------------------------------------------------- #
# Geometry helpers
# --------------------------------------------------------------------------- #

def _bounds(shape: dict[str, Any]) -> tuple[float, float, float, float] | None:
    """Axis-aligned bounds. Returns None when the shape has no sensible box."""
    pos = shape.get("position") or {}
    x, y = float(pos.get("x", 0)), float(pos.get("y", 0))
    kind = shape.get("kind")

    if kind == "circle":
        r = float(shape.get("radius", 0))
        return (x - r, y - r, x + r, y + r)

    if kind in ("rect", "image"):
        return (x, y, x + float(shape.get("width", 0)), y + float(shape.get("height", 0)))

    if kind == "text":
        w = shape.get("width")
        fs = float(shape.get("fontSize", 0))
        # Konva Text without an explicit width auto-sizes; approximate from the
        # glyph count so we can still catch type running off the page.
        width = float(w) if w else len(str(shape.get("text", ""))) * fs * 0.55
        lines = str(shape.get("text", "")).count("\n") + 1
        return (x, y, x + width, y + fs * 1.3 * lines)

    if kind == "line":
        pts = shape.get("points") or []
        xs = [x + float(p) for p in pts[0::2]]
        ys = [y + float(p) for p in pts[1::2]]
        if not xs or not ys:
            return None
        return (min(xs), min(ys), max(xs), max(ys))

    return None


def _covers(outer: tuple[float, float, float, float],
            inner: tuple[float, float, float, float]) -> bool:
    return (outer[0] <= inner[0] and outer[1] <= inner[1]
            and outer[2] >= inner[2] and outer[3] >= inner[3])


def _behind_fill(shape: dict[str, Any], others: list[dict[str, Any]]) -> str | None:
    """
    Best-effort background colour behind a text shape.

    Only the TOPMOST shape that actually covers the text determines what the
    text is read against, so coverage is tested before anything else. An
    earlier version bailed on the first lower-z image or translucent shape it
    encountered anywhere on the canvas, which meant a full-bleed hero photo at
    the bottom of the stack suppressed the contrast check for every text shape
    on the poster — including type sitting on a solid panel far away from it.

    Returns None when the nearest cover is a photo or is translucent: the
    effective colour there is a blend we can't compute, and a wrong number is
    worse than no number.
    """
    tb = _bounds(shape)
    if tb is None:
        return None

    z = shape.get("zIndex", 0)
    top: dict[str, Any] | None = None
    top_z = -(10 ** 9)

    for o in others:
        if o is shape or not isinstance(o, dict):
            continue
        if o.get("zIndex", 0) >= z or o.get("kind") not in ("rect", "image", "circle"):
            continue
        ob = _bounds(o)
        if ob and _covers(ob, tb) and o.get("zIndex", 0) > top_z:
            top_z, top = o.get("zIndex", 0), o

    if top is None:
        return None
    if top.get("kind") == "image" or float(top.get("opacity", 1)) < 0.9:
        return None
    return top.get("fill")


# --------------------------------------------------------------------------- #
# Rules
# --------------------------------------------------------------------------- #

def validate_canvas(
    doc: dict[str, Any],
    supplied_image: dict[str, Any] | None = None,
) -> list[str]:
    """
    Return a list of human-readable problems. Empty list means publishable.

    `supplied_image` describes an image the administrator uploaded or had
    generated: ``{"url": str, "width": float, "height": float}``. When present
    that URL is additionally permitted (it is not in the curated library), and
    the shape using it is checked for distortion.
    """
    problems: list[str] = []
    supplied_url = (supplied_image or {}).get("url")

    if not isinstance(doc, dict):
        return ["The document must be a JSON object."]

    # -- page ---------------------------------------------------------------
    if int(doc.get("width", 0)) != CANVAS_WIDTH or int(doc.get("height", 0)) != CANVAS_HEIGHT:
        problems.append(
            f"Canvas must be exactly {CANVAS_WIDTH}x{CANVAS_HEIGHT} "
            f"(got {doc.get('width')}x{doc.get('height')})."
        )

    bg = doc.get("background")
    if not _parse_hex(str(bg or "")):
        problems.append(f"background must be a hex colour like #0f172a (got {bg!r}).")

    shapes = doc.get("shapes")
    if not isinstance(shapes, list) or not shapes:
        problems.append("shapes must be a non-empty array.")
        return problems

    # Generous: the house-style layouts draw waves, ticks and badges as
    # separate shapes. What this catches is a runaway design, not a rich one.
    if len(shapes) > MAX_SHAPES:
        problems.append(f"{len(shapes)} shapes is too many; keep it under {MAX_SHAPES}.")

    # -- per shape ----------------------------------------------------------
    ids: set[str] = set()
    texts: list[dict[str, Any]] = []

    for i, s in enumerate(shapes):
        if not isinstance(s, dict):
            problems.append(f"Shape {i} is not an object.")
            continue

        sid = s.get("id")
        label = f"shape '{sid}'" if sid else f"shape {i}"

        if not sid or not isinstance(sid, str):
            problems.append(f"Shape {i} needs a non-empty string id.")
        elif sid in ids:
            problems.append(f"Duplicate shape id '{sid}' — ids must be unique.")
        else:
            ids.add(sid)

        kind = s.get("kind")
        if kind not in ("rect", "circle", "line", "text", "image"):
            problems.append(f"{label}: unsupported kind {kind!r}.")
            continue

        if not isinstance(s.get("position"), dict):
            problems.append(f"{label}: position must be {{x, y}}.")
            continue

        if "zIndex" not in s:
            problems.append(f"{label}: zIndex is required — it sets draw order.")

        # colours
        for field in ("fill", "stroke"):
            v = s.get(field)
            if v is not None and not _parse_hex(str(v)):
                problems.append(f"{label}: {field} must be a hex colour (got {v!r}).")

        # images must come from the approved library
        if kind == "image":
            key = str(s.get("blobKey") or "")
            is_supplied = bool(supplied_url) and key == supplied_url
            is_brand = key in BRAND_PATHS

            if not is_supplied and not is_brand and _photo_identity(key) not in _APPROVED_IDS:
                extra = (
                    f" The image you were given is {supplied_url!r} — use it "
                    f"exactly as written."
                    if supplied_url else ""
                )
                problems.append(
                    f"{label}: blobKey must be copied from the approved photo "
                    f"list. {key!r} is not one of them — an invented image URL "
                    f"fails to load when the poster is exported to PDF." + extra
                )

            if is_supplied:
                problems.extend(_check_aspect(s, label, supplied_image or {}))

        if kind == "text":
            texts.append(s)
            if not str(s.get("text", "")).strip():
                problems.append(f"{label}: text is empty.")
            if float(s.get("fontSize", 0)) <= 0:
                problems.append(f"{label}: fontSize must be positive.")

        # Bounds. Decorative shapes are allowed — encouraged, even — to bleed
        # off the trim edge; only text has to stay on the page, because text
        # that runs off is always a mistake and is unrecoverable in print.
        b = _bounds(s)
        if b is not None:
            x0, y0, x1, y1 = b
            decorative = float(s.get("fontSize", 0)) >= DECORATIVE_TEXT_FONT
            if kind == "text" and decorative:
                span = max(x1 - x0, 1.0)
                on_page = max(0.0, min(x1, CANVAS_WIDTH) - max(x0, 0.0))
                if on_page / span < MIN_DECORATIVE_ON_PAGE:
                    problems.append(
                        f"{label}: only {on_page / span:.0%} of this text is on "
                        f"the page (spans x {x0:.0f}..{x1:.0f}). Large type may "
                        f"bleed off the edge, but most of it must stay visible."
                    )
            elif kind == "text":
                if x0 < -1 or y0 < -1 or x1 > CANVAS_WIDTH + 1 or y1 > CANVAS_HEIGHT + 1:
                    problems.append(
                        f"{label}: text runs off the page "
                        f"({x0:.0f},{y0:.0f})-({x1:.0f},{y1:.0f}); "
                        f"the page is {CANVAS_WIDTH}x{CANVAS_HEIGHT}."
                    )
                elif (x0 < VALIDATION_MARGIN - 1
                        or x1 > CANVAS_WIDTH - VALIDATION_MARGIN + 1):
                    problems.append(
                        f"{label}: text is too close to the trim edge (spans x "
                        f"{x0:.0f}..{x1:.0f}); keep it {SAFE_MARGIN}px clear."
                    )
            elif (x1 < -MAX_BLEED or y1 < -MAX_BLEED
                    or x0 > CANVAS_WIDTH + MAX_BLEED
                    or y0 > CANVAS_HEIGHT + MAX_BLEED):
                problems.append(
                    f"{label}: sits entirely off the page "
                    f"({x0:.0f},{y0:.0f})-({x1:.0f},{y1:.0f}) and will never "
                    f"be visible."
                )

    # -- the supplied photograph must actually be in the design ------------
    #
    # Every other rule about the supplied image lives inside `if kind ==
    # "image"`, so they only fire once an image shape exists. Nothing required
    # one to exist at all — a design that simply omitted the photograph passed
    # validation cleanly, and the omission only became visible when someone
    # opened the emailed PDF.
    #
    # This is easy for the model to do by accident: when an attempt fails to
    # parse, the retry regenerates the whole design from scratch and can drop
    # the image on the way.
    if supplied_url:
        using = [
            s for s in shapes
            if isinstance(s, dict)
            and s.get("kind") == "image"
            and s.get("blobKey") == supplied_url
        ]
        if not using:
            other = [
                s for s in shapes
                if isinstance(s, dict) and s.get("kind") == "image"
            ]
            problems.append(
                "The supplied photograph is missing from the design. The brief "
                f"provides an image and the poster must contain exactly one "
                f'image shape with blobKey set to "{supplied_url}"'
                + (
                    f" — there {'is' if len(other) == 1 else 'are'} "
                    f"{len(other)} image shape(s), but none use it."
                    if other else " — the poster currently has no image at all."
                )
            )
        elif len(using) > 1:
            problems.append(
                f"The supplied photograph appears {len(using)} times. Use it "
                "once; repeat it as colour blocks or shapes instead."
            )

    if not texts:
        problems.append("The poster has no text at all — it needs at least a headline.")
        return problems

    # -- composition --------------------------------------------------------
    if not any(float(t.get("fontSize", 0)) >= MIN_HEADLINE_FONT for t in texts):
        biggest = max(float(t.get("fontSize", 0)) for t in texts)
        problems.append(
            f"No headline: the largest text is {biggest:.0f}px. A poster needs a "
            f"dominant headline of at least {MIN_HEADLINE_FONT:.0f}px."
        )

    # text buried under a later-drawn opaque shape
    for t in texts:
        tb = _bounds(t)
        if tb is None:
            continue
        tz = t.get("zIndex", 0)
        for o in shapes:
            if o is t or not isinstance(o, dict):
                continue
            if o.get("zIndex", 0) <= tz or o.get("kind") not in ("rect", "image", "circle"):
                continue
            if float(o.get("opacity", 1)) < 0.85:
                continue
            ob = _bounds(o)
            if ob and _covers(ob, tb):
                problems.append(
                    f"text '{t.get('id')}' is completely hidden behind "
                    f"'{o.get('id')}', which has a higher zIndex and is opaque."
                )
                break

    # contrast, where it can be judged confidently
    for t in texts:
        behind = _behind_fill(t, shapes) or (doc.get("background") if _sits_on_page_bg(t, shapes) else None)
        if not behind:
            continue
        ratio = contrast_ratio(str(t.get("fill", "")), str(behind))
        if ratio is not None and ratio < MIN_CONTRAST:
            problems.append(
                f"text '{t.get('id')}' has poor contrast ({ratio:.1f}:1) — "
                f"{t.get('fill')} on {behind}. Needs at least {MIN_CONTRAST}:1."
            )

    return problems


def _check_aspect(
    shape: dict[str, Any], label: str, supplied: dict[str, Any]
) -> list[str]:
    """
    Reject a supplied image stretched away from its natural proportions.

    Konva and the PDF exporter both draw an image into whatever width/height
    the shape declares — they do not letterbox or cover-crop. So a model that
    picks a pleasing box without checking the source proportions silently
    distorts the picture, and on a photograph of a person that is immediately
    obvious and completely unusable. Stock photos are exempt: they are
    interchangeable, and the model is free to crop the composition.
    """
    natural_w = float(supplied.get("width") or 0)
    natural_h = float(supplied.get("height") or 0)
    drawn_w = float(shape.get("width") or 0)
    drawn_h = float(shape.get("height") or 0)

    if min(natural_w, natural_h, drawn_w, drawn_h) <= 0:
        return []

    natural = natural_w / natural_h
    drawn = drawn_w / drawn_h
    # 12% covers the rounding a model does when snapping to tidy numbers, while
    # still catching the "stretch a landscape photo to fill a portrait panel"
    # mistake, which lands far outside it.
    if abs(drawn - natural) / natural <= 0.12:
        return []

    suggested_h = round(drawn_w / natural)
    suggested_w = round(drawn_h * natural)
    return [
        f"{label}: the image is being stretched. Its real proportions are "
        f"{natural_w:.0f}x{natural_h:.0f} (ratio {natural:.2f}) but it is drawn "
        f"at {drawn_w:.0f}x{drawn_h:.0f} (ratio {drawn:.2f}), which distorts it. "
        f"Use {drawn_w:.0f}x{suggested_h} or {suggested_w}x{drawn_h:.0f}, and "
        f"let it bleed off the page if you need to cover more area."
    ]


def _sits_on_page_bg(shape: dict[str, Any], others: list[dict[str, Any]]) -> bool:
    """True when nothing is drawn beneath this text, so the page colour shows."""
    tb = _bounds(shape)
    if tb is None:
        return False
    z = shape.get("zIndex", 0)
    for o in others:
        if o is shape or not isinstance(o, dict):
            continue
        if o.get("zIndex", 0) >= z or o.get("kind") not in ("rect", "image", "circle"):
            continue
        ob = _bounds(o)
        if ob and _covers(ob, tb):
            return False
    return True

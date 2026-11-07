"""The house style, as a layout engine the AI designer writes copy for.

**Why a layout engine and not a longer prompt.** The old designer asked a model
for raw canvas JSON: coordinates, sizes, colours, z-order. It came back as the
same full-bleed clinical photo under a translucent scrim every time, because
that is the one layout a model can freehand reliably — and the house set is
precisely the kind of design it cannot: circle crops, a teal ring the photo
sits on, sine-wave footers of a hundred points, ticks drawn as lines because
the PDF font has no ✓, and every line of type checked against two renderers.

So the work is split the way a design studio splits it. The model is the
copywriter and art director: it picks an archetype and writes the words. This
module is the layout: it draws the archetype from the same builders as the
built-in house templates (`app/tools/house_templates.py`), sizes the headline
to fit, wraps every line for both the editor and the PDF, and runs the same
`check` the house set ships under. A generated poster cannot drift from the
house style, because it is drawn by the code that defines it.

**What the model can still get wrong** is copy — too long for a layout, a
forbidden word, a pharmacy name it invented. Those come back as problems it is
asked to fix, in plain English, by `template_designer`.

Photographs are baked here too (`bake_photo`): cover-cropped to the slot, never
stretched; circle portraits as transparent PNGs; fades baked into the JPEG,
because the canvas schema has no gradient and stacking translucent rects to
fake one is exactly the look this replaces.
"""

from __future__ import annotations

import io
import math
import re
from dataclasses import dataclass, field
from typing import Any

from PIL import Image, ImageOps

from app.tools import house_templates as ht
from app.tools.house_template_assets import Fade, apply_fade, arch_mask, circle_mask

W, H, MARGIN = ht.W, ht.H, ht.MARGIN
NAVY, TEAL, TEAL_SOFT, TEAL_TEXT = ht.NAVY, ht.TEAL, ht.TEAL_SOFT, ht.TEAL_TEXT
NAVY_SOFT, SKY, INK, SLATE, WHITE = ht.NAVY_SOFT, ht.SKY, ht.INK, ht.SLATE, ht.WHITE

#: The canvas key the composed design uses for its photograph. The API swaps it
#: for a permanent asset URL once it has stored the baked file — storage stays
#: owned by the API, exactly as for every other image this service produces.
PHOTO_KEY = "asset:photo"

#: Every colour a house-style poster may use. Medical blue, white and teal.
HOUSE_COLOURS = frozenset(
    c.lower() for c in (NAVY, ht.BLUE, TEAL, TEAL_SOFT, TEAL_TEXT, NAVY_SOFT, SKY, INK, SLATE, WHITE)
)

#: Never below this, whatever the copy: the brief is a poster, not a leaflet.
MIN_HEADLINE = 64


# ── archetypes ──────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class PhotoSlot:
    """The shape a photograph is baked to for one archetype."""

    width: int
    height: int
    circle: bool = False
    arch: bool = False
    fades: tuple[Fade, ...] = ()
    #: Where the subject usually is, as a fraction of the height. Faces sit in
    #: the upper part of a portrait; cropping on the centre cuts foreheads.
    focus_y: float = 0.42

    @property
    def masked(self) -> bool:
        return self.circle or self.arch

    @property
    def aspect(self) -> float:
        return self.width / self.height

    @property
    def generation_aspect(self) -> str:
        """The image-model aspect closest to this slot (poster_imagery.ASPECT_RATIOS)."""
        if self.aspect >= 1.5:
            return "banner"
        if self.aspect >= 1.1:
            return "hero"
        if self.aspect >= 0.66:
            return "portrait"
        return "full"

    @property
    def stock_orientation(self) -> str:
        return "landscape" if self.aspect > 1.15 else "portrait" if self.aspect < 0.87 else "square"


@dataclass(frozen=True)
class Archetype:
    name: str
    summary: str
    #: When the model should choose it. Shown in the prompt.
    use_for: str
    slot: PhotoSlot
    max_items: int
    #: Whether the archetype shows its items when it also has a photograph.
    items_with_photo: bool = True


ARCHETYPES: dict[str, Archetype] = {
    a.name: a
    for a in (
        Archetype(
            "circle-portrait",
            "Circle-cropped photo on a teal ring, right; eyebrow, headline and a "
            "ticked list on the left; a short lede; contact card and footer bar.",
            "people and trust: a team, a service with a human face, 'trusted local pharmacy'",
            PhotoSlot(660, 660, circle=True),
            max_items=4,
        ),
        Archetype(
            "diagonal-split",
            "Full-width photo cut by a teal diagonal stripe; headline below the cut; "
            "service chips; contact card.",
            "energetic announcements and campaigns: a new service, a clinic starting, "
            "seasonal and urgent messages (flu season, walk-in clinics, deadlines)",
            PhotoSlot(1240, 930, focus_y=0.4),
            max_items=3,
        ),
        Archetype(
            "wave-footer",
            "Headline over a wide photo whose foot dissolves into two layered teal "
            "waves; contact card on the waves. Without a photo, a ticked list takes "
            "the photo's place.",
            "warm, welcoming, short messages: 'meet the team', 'here for you'",
            PhotoSlot(1240, 900, fades=(Fade("left", 0.2, WHITE), Fade("right", 0.2, WHITE),
                                        Fade("top", 0.22, WHITE))),
            max_items=4,
            items_with_photo=False,
        ),
        Archetype(
            "checklist",
            "Pale sky page; tall photo panel on the right fading into the page; "
            "headline and a ticked checklist on the left; optional note card.",
            "advice and information: symptoms, what to bring, who is eligible",
            PhotoSlot(640, 1200, fades=(Fade("left", 0.35, SKY),), focus_y=0.38),
            max_items=5,
        ),
    )
}

DEFAULT_ARCHETYPE = "circle-portrait"

#: "Bold" is a tone, not a layout. There are four house layouts; a brief that
#: asks for a bold poster gets one of them with larger type. (A fifth "bold"
#: archetype — navy page, white type — existed once; it was dropped so every
#: AI design sits in the same set as the built-in templates.)
TONES = ("standard", "bold")

#: How much larger a bold poster sets its headline and list. The headline
#: still shrinks to fit its lines, so this raises the ceiling, never the floor.
BOLD_SCALE = 1.15

_BOLD_BRIEF = re.compile(r"\bbold(er)?\b", re.I)


def tone_for_brief(brief: str | None) -> str:
    """'bold' when the brief asks for it ("a bold flu clinic poster")."""
    return "bold" if _BOLD_BRIEF.search(brief or "") else "standard"

#: Other names the same layouts go by — in briefs, in the design docs ("the
#: checklist bar"), and as a model writes them. Mapped, not rejected: a brief
#: that says "checklist-bar" means the checklist layout, and refusing it would
#: cost a whole repair round for a spelling.
ARCHETYPE_ALIASES = {
    "checklist-bar": "checklist",
    "check-list": "checklist",
    "ticklist": "checklist",
    "circle": "circle-portrait",
    "portrait": "circle-portrait",
    "diagonal": "diagonal-split",
    "wave": "wave-footer",
    "waves": "wave-footer",
}


def canonical_archetype(name: str | None) -> str:
    """'Checklist bar', 'checklist_bar' and 'checklist-bar' all mean 'checklist'."""
    key = "-".join(re.split(r"[\s_\-]+", (name or "").strip().lower())).strip("-")
    return ARCHETYPE_ALIASES.get(key, key)


# ── the plan the model writes ───────────────────────────────────────────────


@dataclass
class Plan:
    """Copy and art direction for one poster. Lengths are checked, not trusted."""

    archetype: str
    name: str
    headline: str
    eyebrow: str = ""
    lede: str = ""
    items: list[str] = field(default_factory=list)
    note: str = ""
    footer: str = ""
    photo_query: str = ""
    #: Not written by the model as copy: set from the brief (see tone_for_brief).
    tone: str = "standard"

    #: Limits per field, in characters. Generous: the layout wraps and sizes,
    #: these only stop a paragraph being poured into a headline.
    LIMITS = {"name": 40, "headline": 60, "eyebrow": 30, "lede": 110, "note": 110,
              "footer": 64, "photo_query": 80, "item": 48}

    @classmethod
    def from_json(cls, data: Any) -> tuple["Plan | None", list[str]]:
        if not isinstance(data, dict):
            return None, ["Return a JSON object with the fields described."]

        def text(key: str) -> str:
            v = data.get(key, "")
            return " ".join(str(v).split()) if isinstance(v, (str, int, float)) else ""

        raw_items = data.get("items") or []
        items = [" ".join(str(i).split()) for i in raw_items if str(i).strip()] \
            if isinstance(raw_items, list) else []
        headline = str(data.get("headline", "")).strip()
        # Keep the model's own line breaks in a headline; they are art direction.
        headline = "\n".join(" ".join(line.split()) for line in headline.split("\n") if line.strip())

        archetype = canonical_archetype(text("archetype")) or DEFAULT_ARCHETYPE
        tone = "standard"
        if archetype == "bold":
            # A model (or an older client) naming the retired "bold" layout
            # means the tone. Drawn as the circle portrait it most resembled,
            # with larger type, rather than spending a repair round on a word.
            archetype, tone = DEFAULT_ARCHETYPE, "bold"

        plan = cls(
            archetype=archetype,
            name=text("name"),
            headline=headline,
            eyebrow=text("eyebrow"),
            lede=text("lede"),
            items=items,
            note=text("note"),
            footer=text("footer"),
            photo_query=text("photo_query"),
            tone=tone,
        )
        return plan, plan.problems()

    def copy(self) -> list[tuple[str, str]]:
        """Every piece of printed text, labelled for error messages."""
        out = [("headline", self.headline), ("eyebrow", self.eyebrow), ("lede", self.lede),
               ("note", self.note), ("footer", self.footer)]
        out += [(f"item {i + 1}", item) for i, item in enumerate(self.items)]
        return [(k, v) for k, v in out if v]

    def problems(self) -> list[str]:
        p: list[str] = []
        if self.archetype not in ARCHETYPES:
            p.append(f"archetype must be one of {', '.join(ARCHETYPES)} (got {self.archetype!r}).")
        if self.tone not in TONES:
            p.append(f"tone must be one of {', '.join(TONES)} (got {self.tone!r}).")
        if not self.headline:
            p.append("headline is required.")
        if not self.name:
            p.append("name is required: a short, specific template name.")

        for key in ("name", "headline", "eyebrow", "lede", "note", "footer", "photo_query"):
            value = getattr(self, key).replace("\n", " ")
            if len(value) > self.LIMITS[key]:
                p.append(f"{key} is {len(value)} characters; keep it under {self.LIMITS[key]}.")
        for i, item in enumerate(self.items):
            if len(item) > self.LIMITS["item"]:
                p.append(f"item {i + 1} is {len(item)} characters; keep each under {self.LIMITS['item']}.")

        arch = ARCHETYPES.get(self.archetype)
        if arch and len(self.items) > arch.max_items:
            p.append(f"{self.archetype} fits at most {arch.max_items} items (got {len(self.items)}).")

        for label, value in self.copy():
            for pattern, why in ht.FORBIDDEN:
                m = pattern.search(value)
                if m:
                    p.append(f"{label} contains {why}: {m.group(0)!r}. Rewrite it without that.")
            if "{{" in value or "}}" in value:
                p.append(f"{label} contains a {{{{token}}}}. Contact details are added by the "
                         "layout; do not write them.")
            if (named := _invented_pharmacy(value)) is not None:
                p.append(f"{label} names a pharmacy ({named!r}). Never invent a pharmacy name: "
                         "the poster's contact card shows the real one.")
            if _PHONE.search(value) or _POSTCODE.search(value) or _URL.search(value):
                p.append(f"{label} contains a phone number, postcode or web address. The contact "
                         "card shows the real ones; do not invent them.")
        return p


_PHONE = re.compile(r"\b0\d{3,4}\s?\d{3}\s?\d{3,4}\b|\+44")
_POSTCODE = re.compile(r"\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b")
_URL = re.compile(r"\bwww\.|https?://|\.co\.uk\b|\.com\b", re.I)
_NAMED_PHARMACY = re.compile(r"\b([A-Z][A-Za-z'&-]+)\s+(?:Community\s+)?Pharmacy\b")
#: Words that may precede "Pharmacy" without making it a name.
_GENERIC = {"Local", "Community", "Our", "The", "Your", "Friendly", "Independent",
            "Neighbourhood", "High", "Street", "NHS", "A", "This", "Every"}


def _invented_pharmacy(text: str) -> str | None:
    for m in _NAMED_PHARMACY.finditer(text):
        if m.group(1) not in _GENERIC and not m.group(1).isupper():
            return m.group(0)
    return None


# ── text fitting ────────────────────────────────────────────────────────────


def _fits(line: str, width: float, size: float) -> bool:
    """Whether one line stays on one line in the PDF *and* the editor."""
    if len(line) > ht.pdf_max_chars(width, size):
        return False
    font = ht._font(round(size))
    return font is None or font.getlength(line) <= width


def wrap(text: str, width: float, size: float) -> list[str] | None:
    """Greedy word wrap that both renderers agree with; None if a word cannot fit."""
    out: list[str] = []
    for hard in text.split("\n"):
        line = ""
        for word in hard.split(" "):
            if not word:
                continue
            test = f"{line} {word}" if line else word
            if _fits(test, width, size):
                line = test
                continue
            if not line or not _fits(word, width, size):
                return None
            out.append(line)
            line = word
        if line:
            out.append(line)
    return out


def fit_text(text: str, width: float, size: float, max_lines: int,
             min_size: float | None = None, step: float = 2) -> tuple[str, float] | None:
    """The text wrapped at the largest size from ``size`` down to ``min_size``."""
    floor = min_size if min_size is not None else size
    s = size
    while s >= floor:
        lines = wrap(text, width, s)
        if lines is not None and len(lines) <= max_lines:
            return "\n".join(lines), s
        s -= step
    return None


def balance(text: str, width: float, size: float) -> str:
    """Re-wrap so the lines are as even as the same line count allows.

    Greedy wrapping fills the first line and leaves an orphan ("Travel clinic
    now / open"). A headline reads as designed when the break falls where the
    lines are closest in length ("Travel clinic / now open"): the narrowest
    width that still takes the same number of lines gives exactly that.
    """
    lines = wrap(text, width, size)
    if lines is None or len(lines) < 2 or "\n" in text:
        return "\n".join(lines or [text])
    lo, hi = width * 0.4, width
    for _ in range(14):
        mid = (lo + hi) / 2
        trial = wrap(text, mid, size)
        if trial is not None and len(trial) == len(lines):
            hi = mid
        else:
            lo = mid
    best = wrap(text, hi, size)
    return "\n".join(best if best is not None and len(best) == len(lines) else lines)


def height_of(text: str, size: float) -> float:
    return (text.count("\n") + 1) * size * 1.15


class LayoutError(ValueError):
    """The copy does not fit the layout. The message is written for the model."""


# ── composition ─────────────────────────────────────────────────────────────


class _Composer:
    def __init__(self, plan: Plan, has_photo: bool):
        self.plan = plan
        self.arch = ARCHETYPES[plan.archetype]
        self.has_photo = has_photo
        self.problems: list[str] = []

    # helpers

    def text(self, c: ht.Canvas, id_: str, x: float, y: float, width: float, text: str,
             size: float, fill: str, max_lines: int, min_size: float | None = None,
             align: str = "left", label: str | None = None) -> float:
        """Place wrapped text and return where it ends. Records a problem if it cannot fit."""
        fitted = fit_text(text, width, size, max_lines, min_size)
        if fitted is None:
            words = max(1, ht.pdf_max_chars(width, min_size or size))
            self.problems.append(
                f"{label or id_} does not fit: at most {max_lines} line(s) of about "
                f"{words} characters here. Shorten it: {text!r}")
            return y
        wrapped, s = fitted
        c.text(id_, x, y, width, wrapped, s, fill, align=align)
        return y + height_of(wrapped, s)

    def _type(self, size: float) -> float:
        """A type size, raised for a bold poster."""
        return round(size * BOLD_SCALE) if self.plan.tone == "bold" else size

    def headline(self, c: ht.Canvas, x: float, y: float, width: float, size: float,
                 max_lines: int, fill: str = NAVY) -> float:
        size = self._type(size)
        fitted = fit_text(self.plan.headline, width, size, max_lines, MIN_HEADLINE)
        if fitted is None:
            return self.text(c, "headline", x, y, width, self.plan.headline, size, fill,
                             max_lines, min_size=MIN_HEADLINE, label="headline")
        wrapped, s = fitted
        even = balance(self.plan.headline, width, s)
        c.text("headline", x, y, width, even, s, fill)
        return y + height_of(even, s)

    def eyebrow(self, c: ht.Canvas, x: float, y: float, width: float, fill: str = TEAL_TEXT):
        if self.plan.eyebrow:
            self.text(c, "eyebrow", x, y, width, self.plan.eyebrow.upper(), 28, fill, 1,
                      min_size=22, label="eyebrow")

    def eyebrow_chip(self, c: ht.Canvas, chip: str, ink: str = WHITE):
        if not self.plan.eyebrow:
            return
        label = self.plan.eyebrow.upper()
        width = min(700, max(300, round(len(label) * 26 * 0.62 + 90)))
        c.rect("eyebrow-chip", MARGIN, 80, width, 60, chip, radius=30)
        self.text(c, "eyebrow", MARGIN, 96, width, label, 26, ink, 1, min_size=20,
                  align="center", label="eyebrow")

    def ticks(self, c: ht.Canvas, x: float, y: float, width: float, bottom: float,
              size: float = 30, ink: str = INK, r: float = 22) -> float:
        """A ticked list, one item below the last; problems if it runs past ``bottom``."""
        size = min(self._type(size), size + 4)
        line = size * 1.15
        for i, item in enumerate(self.plan.items):
            fitted = fit_text(item, width - r * 2 - 18, size, 2, min_size=size - 4)
            if fitted is None:
                self.problems.append(f"item {i + 1} is too long for this layout: {item!r}")
                continue
            wrapped, s = fitted
            if y + height_of(wrapped, s) > bottom:
                self.problems.append(
                    f"the list runs out of room after item {i}. Use fewer or shorter items.")
                break
            c.tick_badge("item-tick", x + r, y + s * 0.65, r, TEAL, WHITE)
            c.text("item", x + r * 2 + 18, y, width - r * 2 - 18, wrapped, s, ink)
            y += line * (wrapped.count("\n") + 1) + 26
        return y

    def chips(self, c: ht.Canvas, y: float):
        x = MARGIN
        for i, item in enumerate(self.plan.items):
            w = round(len(item) * 28 * 0.56 + 70)
            if x + w > W - MARGIN:
                self.problems.append(
                    "the service chips do not fit on one row. Use at most 3 items of a "
                    "few words each for diagonal-split.")
                return
            c.rect("chip", x, y, w, 64, SKY, radius=32)
            self.text(c, "chip-label", x, y + 17, w, item, 28, NAVY, 1, min_size=26,
                      align="center", label=f"item {i + 1}")
            x += w + 24

    def note_card(self, c: ht.Canvas, y: float, fill: str, disc: str) -> float:
        if not self.plan.note:
            return y
        c.rect("note-card", MARGIN, y, W - 2 * MARGIN, 140, fill, radius=24)
        c.plus_badge("note-icon", MARGIN + 60, y + 70, 34, disc, TEAL)
        self.text(c, "note", MARGIN + 120, y + 28, 900, self.plan.note, 30, NAVY, 2,
                  min_size=26, label="note")
        return y + 140

    def lede(self, c: ht.Canvas, x: float, y: float, width: float, fill: str = SLATE,
             size: float = 34, max_lines: int = 2) -> float:
        if not self.plan.lede:
            return y
        return self.text(c, "lede", x, y, width, self.plan.lede, size, fill, max_lines,
                         min_size=size - 6, label="lede")

    def footer(self, c: ht.Canvas, default: str):
        text = self.plan.footer or default
        fitted = fit_text(text, W - 2 * MARGIN, 22, 1)
        if fitted is None:
            self.problems.append(f"footer is too long for one line: {text!r}")
            text = default
        c.footer_bar(fitted[0] if fitted else text)

    def photo(self, c: ht.Canvas, x: float, y: float):
        slot = self.arch.slot
        c.image("photo", x, y, slot.width, slot.height, PHOTO_KEY)

    # archetypes

    def circle_portrait(self) -> ht.Canvas:
        c = ht.Canvas(WHITE)
        c.circle("backdrop", 1010, 640, 430, SKY)
        c.circle("photo-ring", 840, 830, 346, TEAL)
        if self.has_photo:
            self.photo(c, 840 - 330, 830 - 330)
        else:
            c.plus("photo-plus", 840, 830, 300, WHITE)
        c.plus("accent-plus", 1130, 250, 70, TEAL)
        c.plus("accent-plus", 1060, 1210, 44, TEAL_SOFT)

        self.eyebrow(c, MARGIN, 120, 700)
        end = self.headline(c, MARGIN, 170, 960, 104, max_lines=3)
        top = max(end + 50, 520)
        self.ticks(c, MARGIN, top, 470, 1190, size=32, r=30)
        self.lede(c, MARGIN, 1230, 1060, fill=INK, max_lines=2)
        c.contact_card(1400, SKY)
        self.footer(c, "Here for you and your family")
        return c

    def diagonal_split(self) -> ht.Canvas:
        c = ht.Canvas(WHITE)
        if self.has_photo:
            self.photo(c, 0, 0)
        else:
            c.rect("hero-block", 0, 0, W, 940, NAVY)
            c.circle("hero-disc", 980, 330, 240, NAVY_SOFT)
            c.plus("hero-plus", 980, 330, 220, TEAL)
            c.plus("accent-plus", 700, 560, 56, TEAL_SOFT)
        c.polygon("diagonal-stripe", [(0, 640), (W, 840), (W, 890), (0, 690)], TEAL)
        c.polygon("diagonal-cut", [(0, 690), (W, 890), (W, 940), (0, 940)], WHITE)
        self.eyebrow_chip(c, NAVY if self.has_photo else TEAL_TEXT)
        c.plus("accent-plus", 1100, 1010, 60, TEAL)

        end = self.headline(c, MARGIN, 930, 960, 100, max_lines=2)
        end = self.lede(c, MARGIN, end + 20, 1060, size=32)
        if self.plan.items:
            self.chips(c, max(end + 30, 1290))
        c.contact_card(1410, SKY)
        self.footer(c, "Your health, close to home")
        return c

    def wave_footer(self) -> ht.Canvas:
        c = ht.Canvas(WHITE)
        self.eyebrow(c, MARGIN, 110, 700)
        end = self.headline(c, MARGIN, 160, 960, 100, max_lines=2)
        c.plus("accent-plus", 1110, 170, 66, TEAL)
        end = self.lede(c, MARGIN, end + 16, 1060, size=32, max_lines=1)

        if self.has_photo:
            self.photo(c, 0, 500)
        else:
            c.circle("backdrop", 1010, 760, 300, TEAL_SOFT)
            c.circle("backdrop-small", 820, 1060, 150, SKY)
            c.plus("photo-plus", 1010, 760, 220, WHITE)
            self.ticks(c, MARGIN, max(end + 50, 520), 620, 1240, size=30)

        c.wave("wave-back", base_y=1300, amp=34, period=900, phase=0.0, fill=TEAL)
        c.wave("wave-front", base_y=1360, amp=30, period=1100, phase=math.pi * 0.8, fill=SKY)
        c.contact_card(1440, WHITE)
        self.footer(c, "Here to help, every day we're open")
        return c

    def checklist(self) -> ht.Canvas:
        c = ht.Canvas(SKY)
        if self.has_photo:
            self.photo(c, W - 640, 0)
        else:
            c.circle("icon-disc", 960, 420, 230, TEAL)
            c.plus("icon-plus", 960, 420, 200, WHITE)
            c.circle("icon-dot", 1150, 700, 40, TEAL_SOFT)
            c.circle("icon-dot", 780, 200, 26, TEAL_SOFT)
        self.eyebrow(c, MARGIN, 120, 520)
        end = self.headline(c, MARGIN, 170, 520, 96, max_lines=3)
        c.plus("accent-plus", MARGIN + 20, end + 40, 40, TEAL)
        bottom = 1200 if self.plan.note else 1340
        self.ticks(c, MARGIN, end + 90, 500, bottom, size=30, r=28)
        self.note_card(c, 1220, WHITE, SKY)
        c.contact_card(1390, WHITE)
        self.footer(c, "Free, confidential advice from a qualified pharmacist")
        return c


def compose(plan: Plan, has_photo: bool) -> tuple[dict[str, Any], list[str]]:
    """Draw the plan in the house style. Returns (canvas, problems)."""
    if plan.archetype not in ARCHETYPES:
        return {}, [f"Unknown archetype {plan.archetype!r}."]
    composer = _Composer(plan, has_photo)
    draw = getattr(composer, plan.archetype.replace("-", "_"))
    canvas = draw()
    doc = {"name": plan.name, "background": canvas.background, "shapes": canvas.shapes,
           "width": W, "height": H}
    problems = list(composer.problems)
    if not problems:
        problems = house_check(doc)
    return doc, problems


def house_check(doc: dict[str, Any]) -> list[str]:
    """The checks the built-in house set ships under, plus the house palette."""
    problems = ht.check([{"id": "design", "shapes": doc["shapes"],
                          "background": doc["background"]}])
    colours = {doc["background"].lower()}
    for s in doc["shapes"]:
        for key in ("fill", "stroke"):
            if isinstance(s.get(key), str):
                colours.add(s[key].lower())
        # Translucent rects stacked to fake a gradient are the old look; fades
        # are baked into photographs instead.
        if s.get("kind") == "rect" and float(s.get("opacity", 1)) < 1:
            problems.append(f"{s['id']}: translucent rect — bake the fade into the photo instead.")
        if s.get("kind") == "text" and s.get("fontFamily") != ht.FONT:
            problems.append(f"{s['id']}: font must be {ht.FONT}, the face the PDF carries.")
    off_palette = colours - HOUSE_COLOURS
    if off_palette:
        problems.append(f"colours outside the house palette: {sorted(off_palette)}")
    return [p.replace("design/", "").replace("design:", "") for p in problems]


# ── photographs ─────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class BakedPhoto:
    data: bytes
    content_type: str
    width: int
    height: int


#: About 40 megapixels: far above any real poster photo, far below what would
#: exhaust the service's memory.
MAX_SOURCE_PIXELS = 40_000_000


def load_image(data: bytes) -> Image.Image:
    """Decode an upload or download, upright, as RGB on white."""
    try:
        img = Image.open(io.BytesIO(data))
        # Checked before decoding: a 12MB file can declare 30,000 x 30,000
        # pixels and take gigabytes to decode. No poster photo needs more than
        # this (the browser sends at most 1600px; Pexels about 1900).
        if img.width * img.height > MAX_SOURCE_PIXELS:
            raise ValueError("too large")
        img.load()
    except Exception as exc:  # noqa: BLE001 — Pillow raises a zoo of types
        raise ValueError("That file is not an image we can read.") from exc
    img = ImageOps.exif_transpose(img)
    if img.mode in ("RGBA", "LA", "P"):
        rgba = img.convert("RGBA")
        page = Image.new("RGB", rgba.size, (255, 255, 255))
        page.paste(rgba, mask=rgba.split()[-1])
        return page
    return img.convert("RGB")


def cover_crop(img: Image.Image, slot: PhotoSlot) -> Image.Image:
    """Crop to the slot's proportions and size — never stretch.

    Horizontally centred; vertically weighted towards ``focus_y``, where faces
    tend to be. The crop is the largest box of the right shape inside the
    picture, so nothing is distorted and as little as possible is lost.
    """
    sw, sh = img.size
    target = slot.aspect
    if sw / sh > target:
        cw, ch = round(sh * target), sh
    else:
        cw, ch = sw, round(sw / target)
    x0 = (sw - cw) // 2
    y_centre = sh * slot.focus_y
    y0 = int(min(max(0, y_centre - ch / 2), sh - ch))
    return img.crop((x0, y0, x0 + cw, y0 + ch)).resize(
        (slot.width, slot.height), Image.Resampling.LANCZOS)


def bake_photo(data: bytes, archetype: str) -> BakedPhoto:
    """The photograph exactly as this archetype draws it."""
    slot = ARCHETYPES[archetype].slot
    img = cover_crop(load_image(data), slot)
    for fade in slot.fades:
        img = apply_fade(img, fade)

    buf = io.BytesIO()
    if slot.masked:
        rgba = img.convert("RGBA")
        rgba.putalpha(circle_mask(slot.width) if slot.circle else arch_mask((slot.width, slot.height)))
        rgba.save(buf, "PNG", optimize=True)
        return BakedPhoto(buf.getvalue(), "image/png", slot.width, slot.height)
    img.save(buf, "JPEG", quality=86, optimize=True, progressive=True)
    return BakedPhoto(buf.getvalue(), "image/jpeg", slot.width, slot.height)


# ── prompt material ─────────────────────────────────────────────────────────


def archetype_block() -> str:
    return "\n".join(
        f"- {a.name}: {a.summary} Use for {a.use_for}. Up to {a.max_items} items"
        + ("" if a.items_with_photo else " (items only show when there is no photograph)")
        + "."
        for a in ARCHETYPES.values()
    )

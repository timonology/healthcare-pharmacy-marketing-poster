"""The house-style system templates (two sets of six), as canvas JSON.

Generates ``apps/api/src/Acme.Infrastructure/Persistence/HouseTemplates/templates.json``,
which the API embeds and `TemplateSeeder` inserts (insert-only, like every
built-in template). Run after changing a design:

    python -m app.tools.house_templates            # write the JSON
    python -m app.tools.house_templates --preview out/   # also draw PNG previews

**Why generated, when the other 24 are hand-written C#.** Two things here are
computed rather than typed — the waves are polygons of a hundred-odd points —
and every line of text is checked against *both* renderers before it ships
(see `check`). Neither is possible in a C# string literal that nobody can run
until the API is built. The JSON is committed and a test fails if it drifts
from this file.

**What the schema can and cannot do, and what that decided.**

- No clipping, no gradients. Circle crops and photo fades are baked into the
  image files (`house_template_assets.py`); everything else is plain shapes, so
  it stays recolourable in the editor.
- A closed ``line`` is a filled polygon in the stroke colour, in every
  renderer. The waves and the diagonal are built from it.
- No font weight. "Large Inter headlines" are large, not bold. The editor draws
  ``Inter, sans-serif`` with the system sans, and the PDF with Liberation Sans;
  both are Arial-metric, which is why one wrap check covers both.
- Glyphs: nothing outside plain Latin-1 punctuation. A tick (✓) is drawn as a
  line, because the PDF font stack cannot be relied on to carry it — Skia draws
  nothing for a missing glyph.

**House rules** (enforced by `check`): tokens for every contact detail, exactly
one booking QR, no prices or percentages, no reviews or ratings, no medicine
brands, and the bottom-right corner left clear for the pharmacy's logo, which
"Apply pharmacy info" places there on any template without a logo slot.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path

W, H = 1240, 1754
MARGIN = 90
FONT = "Inter, sans-serif"

NAVY = "#0b2e59"
BLUE = "#1565c0"
TEAL = "#0fa3b1"
TEAL_SOFT = "#bfe8ee"
#: A lifted navy for cards on a navy page.
NAVY_SOFT = "#123d73"
#: Teal for *text*. The shape teal is 3:1 on white — fine for a badge, too faint
#: for a phone number read from across a shop. This is 5.8:1 on white.
TEAL_TEXT = "#087079"
SKY = "#e8f5fb"
INK = "#1e293b"
SLATE = "#475569"
WHITE = "#ffffff"

BOOKING_QR_ID = "pharmacy-booking-qr"
BOOKING_QR_SRC = "/api/brand-kit/booking-qr"

#: Where "Apply pharmacy info" puts a logo on a template with no slot for one
#: (`apply-pharmacy.ts`: 16% of the width, half as tall, 80px in from the right,
#: 90px up from the bottom). Nothing may sit here but plain page.
LOGO_ZONE = (W - round(W * 0.16) - 80, H - round(W * 0.16 * 0.5) - 90,
             W - 80, H - 90)

OUT = Path("apps/api/src/Acme.Infrastructure/Persistence/HouseTemplates/templates.json")

# Asset files, from `house_template_assets.SPECS` — imported rather than
# retyped so a renamed file cannot leave a canvas pointing at nothing.
from .house_template_assets import SPECS  # noqa: E402

ASSET = {s.name: f"/api/assets/templates/{s.file_name}" for s in SPECS}
ASSET_SIZE = {s.name: s.out for s in SPECS}


# ── shape builders ────────────────────────────────────────────────────────


class Canvas:
    """Shapes in paint order; z-index is assigned as they are added."""

    def __init__(self, background: str):
        self.background = background
        self.shapes: list[dict] = []
        self._ids: dict[str, int] = {}

    def _base(self, prefix: str, kind: str, x: float, y: float, opacity: float = 1) -> dict:
        n = self._ids.get(prefix, 0) + 1
        self._ids[prefix] = n
        return {
            "id": prefix if n == 1 else f"{prefix}-{n}",
            "kind": kind,
            "position": {"x": round(x, 1), "y": round(y, 1)},
            "rotation": 0,
            "scale": {"x": 1, "y": 1},
            "opacity": opacity,
            "draggable": True,
            "zIndex": len(self.shapes) + 1,
        }

    def rect(self, id_: str, x, y, w, h, fill, radius=0, opacity=1):
        s = self._base(id_, "rect", x, y, opacity) | {"width": w, "height": h, "fill": fill}
        if radius:
            s["cornerRadius"] = radius
        self.shapes.append(s)
        return s

    def circle(self, id_: str, cx, cy, r, fill, opacity=1):
        # Konva and the PDF both position a circle by its centre.
        self.shapes.append(self._base(id_, "circle", cx, cy, opacity) | {"radius": r, "fill": fill})

    def polygon(self, id_: str, points: list[tuple[float, float]], fill, opacity=1):
        flat = [round(v, 1) for p in points for v in p]
        self.shapes.append(self._base(id_, "line", 0, 0, opacity)
                           | {"points": flat, "stroke": fill, "strokeWidth": 1, "closed": True})

    def polyline(self, id_: str, points: list[tuple[float, float]], stroke, width):
        flat = [round(v, 1) for p in points for v in p]
        self.shapes.append(self._base(id_, "line", 0, 0)
                           | {"points": flat, "stroke": stroke, "strokeWidth": width, "closed": False})

    def text(self, id_: str, x, y, w, text, size, fill, align="left"):
        self.shapes.append(self._base(id_, "text", x, y) | {
            "text": text, "fontSize": size, "fontFamily": FONT,
            "fill": fill, "width": w, "align": align})

    def image(self, id_: str, x, y, w, h, src):
        self.shapes.append(self._base(id_, "image", x, y) | {"blobKey": src, "width": w, "height": h})

    def photo(self, asset: str, x, y):
        w, h = ASSET_SIZE[asset]
        self.image("photo", x, y, w, h, ASSET[asset])

    def scaled_photo(self, asset: str, x, y, w):
        """The photo at a width; height follows, so it is never stretched."""
        aw, ah = ASSET_SIZE[asset]
        self.image("photo", x, y, w, round(w * ah / aw), ASSET[asset])

    # ── motifs ──

    def plus(self, id_: str, cx, cy, size, fill, opacity=1):
        """A medical plus: two bars. Recolourable, and no glyph to go missing."""
        bar = size * 0.32
        self.rect(id_, cx - size / 2, cy - bar / 2, size, bar, fill, radius=bar * 0.2, opacity=opacity)
        self.rect(id_, cx - bar / 2, cy - size / 2, bar, size, fill, radius=bar * 0.2, opacity=opacity)

    def tick_badge(self, id_: str, cx, cy, r, disc, mark):
        """A tick in a disc, drawn as a line — the ✓ glyph is not safe in PDF."""
        self.circle(id_, cx, cy, r, disc)
        self.polyline(id_ + "-mark", [
            (cx - r * 0.42, cy + r * 0.02),
            (cx - r * 0.12, cy + r * 0.32),
            (cx + r * 0.45, cy - r * 0.30),
        ], mark, max(4, round(r * 0.2)))

    def step_badge(self, id_: str, cx, cy, r, number: int):
        """A numbered disc for "how it works" steps.

        The disc is the deep text-teal: a white number on the lighter shape
        teal is 3:1, too faint for a numeral read from across a shop.
        """
        self.circle(id_, cx, cy, r, TEAL_TEXT)
        size = round(r * 1.05)
        self.text(id_ + "-number", cx - r, cy - size * 0.58, 2 * r, str(number), size, WHITE, align="center")

    def capsule(self, id_: str, x, y, w, h, left, right):
        """A two-tone capsule: one rounded bar, its right half recoloured.

        The right half is a rounded rect plus a square one over the seam, so
        the join is straight rather than notched.
        """
        self.rect(id_, x, y, w, h, left, radius=h / 2)
        self.rect(id_ + "-half", x + w / 2, y, w / 2, h, right, radius=h / 2)
        self.rect(id_ + "-seam", x + w / 2, y, h / 2, h, right)

    def plus_badge(self, id_: str, cx, cy, r, disc, mark):
        self.circle(id_, cx, cy, r, disc)
        self.plus(id_ + "-mark", cx, cy, r * 0.9, mark)

    def wave(self, id_: str, base_y, amp, period, phase, fill, bottom=H + 10):
        """A band whose top edge is a sine wave, filled to ``bottom``."""
        pts = []
        x = -20.0
        while x <= W + 20:
            pts.append((x, base_y + amp * math.sin(2 * math.pi * x / period + phase)))
            x += 20
        pts += [(W + 20, bottom), (-20, bottom)]
        self.polygon(id_, pts, fill)

    def contact_card(self, y, card_fill, name_fill=NAVY, detail_fill=SLATE, accent=TEAL_TEXT):
        """Name, phone, address and the booking QR — the same block everywhere.

        Left-aligned text and the QR on the card's right, so a pharmacy with no
        booking link (the QR is then not drawn) loses a square at the edge, not
        the start of every line. Ends at x=870, clear of the logo corner.
        """
        # Room for a two-line name and a two-line address: a long pharmacy
        # name wraps, and its second line must land in space, not on the phone.
        self.rect("contact-card", MARGIN, y, 780, 240, card_fill, radius=28)
        self.text("contact-name", MARGIN + 40, y + 28, 530, "{{pharmacy_name}}", 30, name_fill)
        self.text("contact-phone", MARGIN + 40, y + 104, 520, "{{phone}}", 30, accent)
        self.text("contact-address", MARGIN + 40, y + 152, 520, "{{address}}", 24, detail_fill)
        self.image(BOOKING_QR_ID, MARGIN + 780 - 30 - 170, y + 35, 170, 170, BOOKING_QR_SRC)

    def footer_bar(self, text, fill=NAVY, ink=WHITE):
        self.rect("footer-bar", 0, H - 54, W, 54, fill)
        self.text("footer-text", MARGIN, H - 54 + 15, W - 2 * MARGIN, text, 22, ink, align="center")


# ── the six designs ───────────────────────────────────────────────────────


def trusted_circle() -> Canvas:
    c = Canvas(WHITE)
    # Soft backing disc and ring behind the photo: the ring is a larger teal
    # circle the transparent PNG sits on, so it recolours in the editor.
    c.circle("backdrop", 1010, 640, 430, SKY)
    c.circle("photo-ring", 840, 830, 346, TEAL)
    c.scaled_photo("trusted-circle", 840 - 330, 830 - 330, 660)
    c.plus("accent-plus", 1130, 250, 70, TEAL)
    c.plus("accent-plus", 1060, 1210, 44, TEAL_SOFT)

    c.text("eyebrow", MARGIN, 120, 700, "YOUR LOCAL PHARMACY TEAM", 28, TEAL_TEXT)
    c.text("headline", MARGIN, 170, 1000, "Trusted care,\nevery visit.", 104, NAVY)

    for i, (title, sub) in enumerate([
        ("Expert advice", "No appointment\nneeded"),
        ("Prescriptions", "Ready when\nyou need them"),
        ("Health checks", "Quick, private\nand friendly"),
    ]):
        y = 560 + i * 190
        c.tick_badge("item-tick", MARGIN + 34, y + 30, 34, TEAL, WHITE)
        c.text("item-title", MARGIN + 90, y, 380, title, 36, NAVY)
        c.text("item-sub", MARGIN + 90, y + 50, 380, sub, 26, SLATE)

    c.text("lede", MARGIN, 1230, 1060,
           "Friendly, qualified pharmacists who take the time\nto listen and help.", 34, INK)

    c.contact_card(1400, SKY)
    c.footer_bar("Here for you and your family")
    return c


def diagonal_care() -> Canvas:
    c = Canvas(WHITE)
    c.photo("diagonal-care", 0, 0)
    # The split: a teal stripe along the cut, then white covering the rest of
    # the photo. Real polygons, so both recolour.
    c.polygon("diagonal-stripe", [(0, 640), (W, 840), (W, 890), (0, 690)], TEAL)
    c.polygon("diagonal-cut", [(0, 690), (W, 890), (W, 940), (0, 940)], WHITE)
    c.rect("eyebrow-chip", MARGIN, 90, 420, 64, NAVY, radius=32)
    c.text("eyebrow", MARGIN, 108, 420, "PHARMACY SERVICES", 26, WHITE, align="center")
    c.plus("accent-plus", 1100, 1010, 60, TEAL)

    c.text("headline", MARGIN, 930, 1000, "Care that fits\naround your day", 100, NAVY)
    c.text("lede", MARGIN, 1180, 1060,
           "Prescriptions, health checks and friendly advice,\nall under one roof.", 32, SLATE)

    x = MARGIN
    for label, w in [("Prescriptions", 290), ("Health checks", 290), ("Everyday advice", 330)]:
        c.rect("chip", x, 1290, w, 64, SKY, radius=32)
        c.text("chip-label", x, 1307, w, label, 28, NAVY, align="center")
        x += w + 24

    c.contact_card(1410, SKY)
    c.footer_bar("Your health, close to home")
    return c


def team_wave() -> Canvas:
    c = Canvas(WHITE)
    c.text("eyebrow", MARGIN, 110, 700, "MEET THE TEAM", 28, TEAL_TEXT)
    c.text("headline", MARGIN, 160, 1060, "Friendly faces,\nexpert advice", 100, NAVY)
    c.plus("accent-plus", 1110, 170, 66, TEAL)
    c.photo("team-wave", 0, 400)

    # Two waves over the foot of the photo: teal behind, sky in front, so a
    # teal ribbon runs between them. The photo's lower edge is hidden.
    c.wave("wave-back", base_y=1300, amp=34, period=900, phase=0.0, fill=TEAL)
    c.wave("wave-front", base_y=1360, amp=30, period=1100, phase=math.pi * 0.8, fill=SKY)

    c.contact_card(1440, WHITE)
    c.footer_bar("Here to help, every day we're open")
    return c


def advice_checklist() -> Canvas:
    c = Canvas(SKY)
    c.photo("advice-checklist", W - 640, 0)
    c.text("eyebrow", MARGIN, 120, 520, "ASK YOUR PHARMACIST", 28, TEAL_TEXT)
    c.text("headline", MARGIN, 170, 540, "Not sure?\nJust ask.", 96, NAVY)
    c.plus("accent-plus", MARGIN + 20, 440, 40, TEAL)

    items = [
        "Coughs, colds and\nsore throats",
        "Minor skin conditions",
        "Aches and pains",
        "Hay fever and allergies",
        "Getting the most from\nyour medicines",
    ]
    y = 500
    for item in items:
        c.tick_badge("item-tick", MARGIN + 30, y + 22, 30, TEAL, WHITE)
        c.text("item", MARGIN + 80, y, 440, item, 32, INK)
        y += 70 + 38 * item.count("\n") + 30

    c.rect("note-card", MARGIN, 1230, W - 2 * MARGIN, 120, WHITE, radius=24)
    c.plus_badge("note-icon", MARGIN + 60, 1290, 34, SKY, TEAL)
    c.text("note", MARGIN + 120, 1250, 900,
           "A private consultation room is available.\nNo appointment needed.", 30, NAVY)

    c.contact_card(1390, WHITE)
    c.footer_bar("Free, confidential advice from a qualified pharmacist")
    return c


def book_with_us() -> Canvas:
    c = Canvas(NAVY)
    c.photo("book-with-us", 0, 0)
    c.plus("accent-plus", 1110, 130, 70, WHITE, opacity=0.9)
    c.plus("accent-plus", 1170, 230, 36, TEAL)

    c.text("headline", MARGIN, 890, 1060, "Book with us", 120, WHITE)
    c.text("lede", MARGIN, 1040, 1060,
           "Appointments that suit you, with a pharmacist\nwho has time to talk.", 36, TEAL_SOFT)

    # The one QR on this design, large, beside words that still make sense
    # when a pharmacy has no booking link and the code is not drawn.
    c.rect("book-card", MARGIN, 1160, W - 2 * MARGIN, 290, WHITE, radius=32)
    c.image(BOOKING_QR_ID, MARGIN + 40, 1180, 250, 250, BOOKING_QR_SRC)
    c.text("book-title", MARGIN + 330, 1205, 690, "Book your appointment", 44, NAVY)
    c.text("book-how", MARGIN + 330, 1275, 690, "Online, by phone or at the counter", 30, SLATE)
    c.text("book-phone", MARGIN + 330, 1335, 690, "{{phone}}", 40, TEAL_TEXT)

    # White foot, so the auto-placed logo lands on white, not navy.
    c.rect("foot", 0, 1500, W, H - 1500, WHITE)
    c.text("foot-name", MARGIN, 1545, 780, "{{pharmacy_name}}", 36, NAVY)
    c.text("foot-address", MARGIN, 1600, 780, "{{address}}", 26, SLATE)
    c.footer_bar("Consultations  |  Health checks  |  Medicine reviews", TEAL_TEXT, WHITE)
    return c


def local_pharmacy() -> Canvas:
    c = Canvas(WHITE)
    c.text("headline", MARGIN, 110, 1060, "YOUR LOCAL\nPHARMACY", 116, NAVY)
    c.rect("headline-rule", MARGIN, 400, 130, 8, TEAL)
    c.plus("accent-plus", 1110, 170, 60, TEAL)
    c.plus("accent-plus", 1150, 1110, 44, TEAL)

    c.text("lede", MARGIN, 440, 520, "Here for your health,\nevery step of the way.", 38, SLATE)
    c.text("promise", MARGIN, 560, 520, "Trusted care. Local support.", 30, TEAL_TEXT)

    c.circle("photo-ring", 890, 760, 318, TEAL)
    c.scaled_photo("local-pharmacy", 890 - 300, 760 - 300, 600)

    for i, (title, sub) in enumerate([
        ("HEALTH ADVICE", "We're here to listen\nand help."),
        ("PRESCRIPTIONS", "Fast, reliable\nprescription service."),
        ("SUPPORT & CARE", "From everyday health\nto long-term support."),
    ]):
        y = 680 + i * 170
        c.plus_badge("service-icon", MARGIN + 48, y + 45, 48, SKY, TEAL)
        c.text("service-title", MARGIN + 120, y, 370, title, 32, NAVY)
        c.text("service-sub", MARGIN + 120, y + 44, 370, sub, 26, SLATE)
        if i < 2:
            c.rect("service-rule", MARGIN, y + 150, 420, 2, TEAL_SOFT)

    c.wave("wave-back", base_y=1250, amp=28, period=1000, phase=0.4, fill=SKY)
    c.wave("wave-front", base_y=1330, amp=34, period=820, phase=math.pi, fill=TEAL_SOFT)

    c.contact_card(1420, WHITE)
    c.footer_bar("We're more than a pharmacy. We're part of your community.")
    return c


# ── second set: awareness, vaccination, travel, weight ────────────────────


def know_your_numbers() -> Canvas:
    c = Canvas(WHITE)
    c.photo("know-your-numbers", 0, 0)
    c.rect("eyebrow-chip", MARGIN, 80, 340, 60, NAVY, radius=30)
    c.text("eyebrow", MARGIN, 96, 340, "HEART HEALTH", 26, WHITE, align="center")
    c.plus_badge("photo-badge", 1080, 640, 72, TEAL, WHITE)

    c.text("headline", MARGIN, 700, 1000, "Know your\nnumbers", 116, NAVY)
    c.text("lede", MARGIN, 990, 1060,
           "A blood pressure check takes a few minutes\nand could make a big difference.", 34, SLATE)

    c.text("list-title", MARGIN, 1110, 600, "Why check?", 32, TEAL_TEXT)
    for i, item in enumerate([
        "High blood pressure often has no symptoms",
        "It raises the risk of heart attack and stroke",
        "Just ask any member of our team",
    ]):
        y = 1160 + i * 62
        c.tick_badge("item-tick", MARGIN + 22, y + 18, 22, TEAL, WHITE)
        c.text("item", MARGIN + 62, y, 960, item, 30, INK)

    c.contact_card(1400, SKY)
    c.footer_bar("Looking after your heart, together")
    return c


def diabetes_risk() -> Canvas:
    c = Canvas(SKY)
    c.text("headline", MARGIN, 110, 1060, "Could you be\nat risk of\ntype 2 diabetes?", 92, NAVY)
    c.plus("accent-plus", 1120, 170, 66, TEAL)

    # Photo on the left this time; the first set's circles all sit right.
    c.circle("photo-ring", 380, 760, 296, TEAL)
    c.scaled_photo("diabetes-risk", 380 - 280, 760 - 280, 560)

    c.text("list-title", 720, 490, 430, "Common risk factors", 30, TEAL_TEXT)
    y = 550
    for item in ["Being over 40", "Family history of\ndiabetes", "Carrying extra\nweight",
                 "High blood pressure"]:
        c.tick_badge("item-tick", 742, y + 18, 22, TEAL, WHITE)
        c.text("item", 782, y, 366, item, 28, INK)
        y += 62 + 32 * item.count("\n") + 14

    c.rect("note-card", MARGIN, 1110, W - 2 * MARGIN, 120, WHITE, radius=24)
    c.plus_badge("note-icon", MARGIN + 60, 1170, 34, SKY, TEAL)
    c.text("note", MARGIN + 120, 1130, 900,
           "Ask us about a type 2 diabetes risk check.\nMaking changes early can help reduce your risk.", 30, NAVY)

    c.contact_card(1400, WHITE)
    c.footer_bar("Know your risk. Talk to your pharmacist.")
    return c


def flu_vaccination() -> Canvas:
    c = Canvas(WHITE)
    # The arch: a baked PNG, rounded on top, on a soft disc for depth.
    c.circle("backdrop", 1010, 330, 250, SKY)
    c.scaled_photo("flu-vaccination", 620, 170, 560)
    c.plus_badge("photo-badge", 640, 1040, 64, TEAL, WHITE)
    c.plus("accent-plus", 1170, 1100, 40, TEAL_SOFT)

    c.text("eyebrow", MARGIN, 190, 520, "FLU SEASON", 28, TEAL_TEXT)
    c.text("headline", MARGIN, 240, 520, "Get your\nflu vaccine\nhere", 80, NAVY)
    c.text("lede", MARGIN, 540, 500, "Protect yourself and\nthe people around you.", 32, SLATE)

    y = 680
    for item in ["Quick and simple", "Walk in or book ahead",
                 "Ask if you're eligible\nfor an NHS vaccine"]:
        c.tick_badge("item-tick", MARGIN + 22, y + 18, 22, TEAL, WHITE)
        c.text("item", MARGIN + 62, y, 440, item, 28, INK)
        y += 62 + 32 * item.count("\n") + 12

    c.rect("note-card", MARGIN, 1150, W - 2 * MARGIN, 150, SKY, radius=24)
    c.text("note", MARGIN + 40, 1180, 980,
           "It only takes a few minutes, and it's the best\nway to help protect against flu.", 32, NAVY)

    c.contact_card(1400, SKY)
    c.footer_bar("Flu can be serious. Protect yourself this winter.")
    return c


def winter_vaccines() -> Canvas:
    c = Canvas(NAVY)
    c.text("headline", MARGIN, 120, 1060, "Stay well\nthis winter", 110, WHITE)
    c.text("lede", MARGIN, 400, 1060, "Ask us which vaccinations are right for you.", 34, TEAL_SOFT)
    c.plus("accent-plus", 1120, 180, 64, TEAL)

    c.circle("photo-ring", 870, 820, 318, TEAL)
    c.scaled_photo("winter-vaccines", 870 - 300, 820 - 300, 600)

    for i, name in enumerate(["Flu", "COVID-19", "Pneumococcal"]):
        y = 540 + i * 118
        c.rect("vaccine-card", MARGIN, y, 400, 96, NAVY_SOFT, radius=22)
        c.plus_badge("vaccine-icon", MARGIN + 50, y + 48, 28, TEAL, WHITE)
        c.text("vaccine", MARGIN + 100, y + 29, 280, name, 32, WHITE)
    c.text("eligibility", MARGIN, 920, 420, "Eligibility depends on\nyour age and health.", 26, TEAL_SOFT)

    # White lower half: contact details, and a light corner for the logo.
    c.rect("foot", 0, 1210, W, H - 1210, WHITE)
    c.text("foot-title", MARGIN, 1250, 1000, "Walk in or book ahead", 40, NAVY)
    c.contact_card(1330, SKY)
    c.footer_bar("Protect yourself and the people you love")
    return c


def travel_clinic() -> Canvas:
    c = Canvas(SKY)
    # The suitcase stands on the bottom edge of its photo, so nothing may be
    # laid over the photo's foot: it ends on a straight teal rule instead of a
    # wave, and the wave moves down behind the contact card.
    c.photo("travel-clinic", 0, 0)
    c.rect("photo-rule", 0, 738, W, 14, TEAL)
    c.rect("eyebrow-chip", MARGIN, 80, 360, 60, NAVY, radius=30)
    c.text("eyebrow", MARGIN, 96, 360, "TRAVEL HEALTH", 26, WHITE, align="center")
    c.plus("accent-plus", 1130, 840, 56, TEAL)

    c.text("headline", MARGIN, 800, 1000, "Travelling\nabroad?", 104, NAVY)
    c.text("lede", MARGIN, 1050, 1060, "Get travel-ready at your local pharmacy.", 32, SLATE)

    for i, label in enumerate([
        "Travel vaccinations", "Malaria prevention advice",
        "Advice for your destination", "Book ahead: some vaccines\nneed time to work",
    ]):
        x = MARGIN + (i % 2) * 540
        y = 1120 + (i // 2) * 128
        c.rect("service-card", x, y, 520, 108, WHITE, radius=22)
        c.plus_badge("service-icon", x + 50, y + 54, 28, SKY, TEAL)
        c.text("service", x + 96, y + (39 if "\n" not in label else 24), 400, label, 26, NAVY)

    c.wave("wave", base_y=1400, amp=22, period=900, phase=0.5, fill=TEAL_SOFT)
    c.contact_card(1440, WHITE)
    c.footer_bar("Plan ahead and travel with confidence")
    return c


def weight_support() -> Canvas:
    c = Canvas(WHITE)
    c.text("headline", MARGIN, 110, 1060, "Support for a\nhealthier weight", 96, NAVY)
    c.text("lede", MARGIN, 350, 1060, "Friendly, private support from our team.", 34, SLATE)
    c.plus("accent-plus", 1150, 150, 56, TEAL)

    # Photo with an offset teal block behind it — depth without a shadow.
    c.rect("photo-block", MARGIN + 60, 500, 1000, 640, TEAL)
    c.photo("weight-support", MARGIN, 450)

    y = 1180
    for item in ["Friendly, private conversations", "Practical tips on food and activity",
                 "Regular check-ins to keep you on track"]:
        c.tick_badge("item-tick", MARGIN + 22, y + 18, 22, TEAL, WHITE)
        c.text("item", MARGIN + 62, y, 960, item, 30, INK)
        y += 62

    c.contact_card(1400, SKY)
    c.footer_bar("Small changes can make a big difference")
    return c



# ── third set: one or two per category ────────────────────────────────────


def tick_list(c: Canvas, x, y, width, items, size=28, gap=14, ink=INK):
    """Ticked items, each placed below the last however many lines it takes."""
    line = size * 1.15
    for item in items:
        c.tick_badge("item-tick", x + 22, y + size * 0.65, 22, TEAL, WHITE)
        c.text("item", x + 62, y, width - 62, item, size, ink)
        y += line * (item.count("\n") + 1) + gap + 8
    return y


def step_list(c: Canvas, x, y, width, steps, size=30, gap=22):
    line = size * 1.15
    for n, step in enumerate(steps, start=1):
        c.step_badge("step", x + 30, y + size * 0.6, 30, n)
        c.text("step-text", x + 80, y, width - 80, step, size, INK)
        y += max(60, line * (step.count("\n") + 1)) + gap
    return y


def shingles_vaccination() -> Canvas:
    c = Canvas(WHITE)
    c.circle("backdrop", 1040, 500, 300, SKY)
    c.circle("photo-ring", 880, 700, 318, TEAL)
    c.scaled_photo("shingles-vaccination", 580, 400, 600)
    c.plus("accent-plus", 620, 1060, 44, TEAL)

    c.text("eyebrow", MARGIN, 150, 480, "VACCINATION", 28, TEAL_TEXT)
    c.text("headline", MARGIN, 200, 540, "Protect\nyourself\nfrom shingles", 72, NAVY)
    tick_list(c, MARGIN, 500, 460, [
        "Shingles can be very\npainful",
        "The vaccine helps\nreduce your risk",
        "Ask if you're eligible\nfor an NHS vaccine",
    ])

    c.rect("note-card", MARGIN, 1130, W - 2 * MARGIN, 150, SKY, radius=24)
    c.plus_badge("note-icon", MARGIN + 60, 1205, 34, WHITE, TEAL)
    c.text("note", MARGIN + 120, 1165, 900,
           "Shingles is caused by the same virus\nas chickenpox.", 32, NAVY)

    c.contact_card(1400, SKY)
    c.footer_bar("Protect yourself against shingles")
    return c


def pneumonia_vaccination() -> Canvas:
    c = Canvas(NAVY)
    c.photo("pneumonia-vaccination", 0, 0)
    c.rect("eyebrow-chip", MARGIN, 80, 460, 60, NAVY, radius=30)
    c.text("eyebrow", MARGIN, 96, 460, "PROTECT YOUR HEALTH", 26, WHITE, align="center")

    c.text("headline", MARGIN, 700, 1060, "Pneumonia\nvaccination", 100, WHITE)
    c.text("lede", MARGIN, 950, 1060,
           "The pneumococcal vaccine helps protect\nagainst serious illnesses like pneumonia.",
           32, TEAL_SOFT)
    tick_list(c, MARGIN, 1060, 1060, [
        "Most people only need it once",
        "Ask if you're eligible for an NHS vaccine",
    ], size=30, ink=WHITE)

    c.rect("foot", 0, 1220, W, H - 1220, WHITE)
    c.text("foot-title", MARGIN, 1270, 1000, "Speak to our pharmacy team", 40, NAVY)
    c.contact_card(1350, SKY)
    c.footer_bar("Protect yourself all year round")
    return c


def stop_smoking() -> Canvas:
    c = Canvas(WHITE)
    c.photo("stop-smoking", W - 720, 0)
    c.text("headline", MARGIN, 140, 470, "It's never\ntoo late\nto quit", 84, NAVY)
    c.text("lede", MARGIN, 470, 430, "Stop smoking support,\nright here.", 32, SLATE)
    tick_list(c, MARGIN, 620, 440, [
        "Friendly support\nfrom our team",
        "Help choosing stop\nsmoking aids",
        "You're more likely to\nquit with support",
    ])

    c.rect("note-card", MARGIN, 1150, W - 2 * MARGIN, 140, SKY, radius=24)
    c.plus_badge("note-icon", MARGIN + 60, 1220, 34, WHITE, TEAL)
    c.text("note", MARGIN + 120, 1185, 900,
           "Quitting is one of the best things you can\ndo for your health.", 32, NAVY)

    c.contact_card(1400, SKY)
    c.footer_bar("Ready when you are")
    return c


def contraception() -> Canvas:
    c = Canvas(WHITE)
    c.text("headline", MARGIN, 120, 1060, "Contraception\nadvice you\ncan trust", 96, NAVY)
    c.plus("accent-plus", 1120, 170, 60, TEAL)

    c.rect("band", 0, 540, W, 460, SKY)
    c.circle("photo-ring", 880, 770, 300, TEAL)
    c.scaled_photo("contraception", 880 - 282, 770 - 282, 564)
    tick_list(c, MARGIN, 600, 470, [
        "Private and confidential",
        "Ask about starting or\ncontinuing the pill",
        "Ask us about emergency\ncontraception",
    ])

    c.text("note", MARGIN, 1130, 1060,
           "Judgement-free advice from a qualified pharmacist.", 30, SLATE)
    c.contact_card(1400, SKY)
    c.footer_bar("Your health, your choice")
    return c


PHARMACY_FIRST_CONDITIONS = [
    "Sinusitis", "Sore throat", "Earache", "Infected insect bites",
    "Impetigo", "Shingles", "Uncomplicated UTIs in women",
]


def pharmacy_first() -> Canvas:
    c = Canvas(WHITE)
    c.text("headline", MARGIN, 110, 1060, "Feeling unwell?\nSee us first.", 96, NAVY)
    c.text("lede", MARGIN, 350, 1060,
           "Advice and treatment for 7 common conditions,\nwithout a GP appointment.", 32, SLATE)

    c.scaled_photo("pharmacy-first", MARGIN, 470, 480)
    c.plus_badge("photo-badge", MARGIN + 440, 1200, 56, TEAL, WHITE)

    # The seven NHS Pharmacy First conditions, one chip each.
    for i, name in enumerate(PHARMACY_FIRST_CONDITIONS):
        y = 470 + i * 78
        c.rect("condition", 620, y, 530, 66, SKY, radius=33)
        c.plus("condition-mark", 652, y + 33, 26, TEAL)
        c.text("condition-name", 684, y + 18, 450, name, 26, NAVY)
    c.text("condition-note", 620, 1030, 530,
           "Age limits apply. NHS Pharmacy First\nis available in England.", 24, SLATE)

    c.contact_card(1400, SKY)
    c.footer_bar("Ask us first for common conditions")
    return c


def prescription_delivery() -> Canvas:
    c = Canvas(WHITE)
    c.photo("prescription-delivery", 0, 0)
    c.rect("photo-rule", 0, 786, W, 14, TEAL)
    c.rect("eyebrow-chip", MARGIN, 80, 360, 60, NAVY, radius=30)
    c.text("eyebrow", MARGIN, 96, 360, "HOME DELIVERY", 26, WHITE, align="center")

    c.text("headline", MARGIN, 840, 1060, "Prescriptions,\ndelivered", 100, NAVY)
    step_list(c, MARGIN, 1100, 1060, [
        "Order your repeat prescription",
        "We prepare your medicines",
        "We bring them to your door",
    ])
    c.text("note", MARGIN, 1330, 1060, "Ask about delivery in your area.", 26, SLATE)

    c.contact_card(1410, SKY)
    c.footer_bar("Your medicines, without the trip")
    return c


def hay_fever() -> Canvas:
    c = Canvas(SKY)
    c.photo("hay-fever", 0, 0)
    c.rect("eyebrow-chip", MARGIN, 80, 420, 60, NAVY, radius=30)
    c.text("eyebrow", MARGIN, 96, 420, "SPRING AND SUMMER", 26, WHITE, align="center")
    c.wave("wave-back", base_y=796, amp=22, period=980, phase=0.6, fill=TEAL)
    c.wave("wave-front", base_y=836, amp=20, period=820, phase=math.pi, fill=SKY)
    c.plus("accent-plus", 1130, 930, 56, TEAL)

    c.text("headline", MARGIN, 880, 1000, "Hay fever\nseason?", 104, NAVY)
    c.text("lede", MARGIN, 1130, 1060, "Ask us which treatment suits you best.", 32, SLATE)
    tick_list(c, MARGIN, 1190, 1060, [
        "Keep windows and doors shut",
        "Shower and change after being outside",
        "Wear wraparound sunglasses",
    ], gap=4)

    c.contact_card(1420, WHITE)
    c.footer_bar("Here to help all season")
    return c


def repeat_prescriptions() -> Canvas:
    c = Canvas(WHITE)
    c.text("headline", MARGIN, 140, 540, "Repeat\nprescriptions,\nsorted", 70, NAVY)
    c.circle("photo-ring", 920, 400, 278, TEAL)
    c.scaled_photo("repeat-prescriptions", 920 - 260, 400 - 260, 520)
    c.plus("accent-plus", MARGIN + 30, 470, 44, TEAL)

    step_list(c, MARGIN, 760, 1060, [
        "Nominate us for your prescriptions",
        "Order through the NHS App or your GP practice",
        "Collect from us, or ask about delivery",
    ])

    c.rect("note-card", MARGIN, 1110, W - 2 * MARGIN, 150, SKY, radius=24)
    c.plus_badge("note-icon", MARGIN + 60, 1185, 34, WHITE, TEAL)
    c.text("note", MARGIN + 120, 1145, 900,
           "Need a hand setting up the NHS App?\nJust ask us.", 32, NAVY)

    c.contact_card(1400, SKY)
    c.footer_bar("Your medicines, ready when you need them")
    return c


def antibiotics_awareness() -> Canvas:
    """Photo-free on purpose: antibiotics photos are packets and blister packs."""
    c = Canvas(WHITE)
    c.circle("backdrop", 1100, 250, 300, TEAL_SOFT)
    c.circle("backdrop-small", 960, 520, 140, SKY)
    c.capsule("capsule", 700, 300, 380, 150, NAVY, TEAL)
    c.plus("accent-plus", 640, 170, 48, TEAL)

    c.text("eyebrow", MARGIN, 140, 520, "KEEP ANTIBIOTICS WORKING", 28, TEAL_TEXT)
    c.text("headline", MARGIN, 540, 1060, "Antibiotics\ndon't work for\neverything", 96, NAVY)
    tick_list(c, MARGIN, 900, 1060, [
        "They don't work for colds or flu",
        "Take them exactly as prescribed",
        "Never share them or keep them for later",
        "Ask us how to ease cold and flu symptoms",
    ], size=30, gap=10)

    c.contact_card(1400, SKY)
    c.footer_bar("Use antibiotics wisely")
    return c


def medicine_returns() -> Canvas:
    """Photo-free on purpose: returned-medicine photos are branded boxes."""
    c = Canvas(SKY)
    c.circle("icon-disc", 960, 320, 200, TEAL)
    c.plus("icon-plus", 960, 320, 170, WHITE)
    c.circle("icon-dot", 1150, 520, 34, TEAL_SOFT)
    c.circle("icon-dot", 760, 140, 22, TEAL_SOFT)

    c.text("headline", MARGIN, 140, 640, "Unused\nmedicines?", 104, NAVY)
    c.text("lede", MARGIN, 430, 640, "Bring them back to us.", 40, TEAL_TEXT)

    step_list(c, MARGIN, 580, 1060, [
        "Gather unused or out-of-date medicines",
        "Bring them to our pharmacy counter",
        "We dispose of them safely",
    ])

    c.rect("note-card", MARGIN, 860, W - 2 * MARGIN, 190, WHITE, radius=24)
    c.plus_badge("note-icon", MARGIN + 60, 955, 34, SKY, TEAL)
    c.text("note", MARGIN + 120, 890, 900,
           "Please don't flush medicines or put them\nin the household bin.", 32, NAVY)
    c.text("note-sharps", MARGIN + 120, 975, 900, "Ask us about sharps and needles.", 28, SLATE)

    c.contact_card(1400, WHITE)
    c.footer_bar("Safe disposal protects people and the environment")
    return c


#: Designed without a photograph, on purpose — their natural photos are medicine
#: packaging, which carries brand names. Every other template must have one.
PHOTO_FREE = {"sys-house-antibiotics", "sys-house-medicine-returns"}

#: Release dates order the gallery newest first; one per batch.
FIRST_SET = "2026-09-29"
SECOND_SET = "2026-09-30"
THIRD_SET = "2026-10-01"

TEMPLATES = [
    ("sys-house-trusted-circle", "Trusted circle", "General", FIRST_SET,
     "Circle-cropped pharmacist beside a checklist of services. Clean medical blue and white.",
     ["house", "modern", "photo", "services", "advice"], trusted_circle),
    ("sys-house-diagonal-care", "Diagonal care", "General", FIRST_SET,
     "Photo cut by a teal diagonal, with service chips below.",
     ["house", "modern", "photo", "services", "consultation"], diagonal_care),
    ("sys-house-team-wave", "Team wave", "General", FIRST_SET,
     "Two members of the team over a teal wave. For introducing your staff.",
     ["house", "modern", "photo", "team", "welcome"], team_wave),
    ("sys-house-advice-checklist", "Advice checklist", "General", FIRST_SET,
     "Everyday conditions a pharmacist can help with, beside a faded photo panel.",
     ["house", "modern", "photo", "advice", "minor ailments"], advice_checklist),
    ("sys-house-book-with-us", "Book with us", "General", FIRST_SET,
     "Photo-led booking poster with a large QR for your booking link.",
     ["house", "modern", "photo", "booking", "appointments", "qr"], book_with_us),
    ("sys-house-local-pharmacy", "Local pharmacy", "General", FIRST_SET,
     "Circle photo, three services and a wave footer. Your community pharmacy, at a glance.",
     ["house", "modern", "photo", "community", "services"], local_pharmacy),

    ("sys-house-know-your-numbers", "Know your numbers", "Awareness", SECOND_SET,
     "Blood pressure check awareness: full-width photo, why-check list, plus badge.",
     ["house", "modern", "photo", "blood pressure", "heart", "screening"], know_your_numbers),
    ("sys-house-diabetes-risk", "Diabetes risk check", "Awareness", SECOND_SET,
     "Type 2 diabetes risk factors beside a circle photo, with a risk-check prompt.",
     ["house", "modern", "photo", "diabetes", "screening", "risk check"], diabetes_risk),
    ("sys-house-flu-vaccination", "Flu vaccination", "Vaccination", SECOND_SET,
     "Arch-framed photo and a short checklist for your flu vaccination service.",
     ["house", "modern", "photo", "flu", "vaccination", "winter"], flu_vaccination),
    ("sys-house-winter-vaccines", "Winter vaccinations", "Vaccination", SECOND_SET,
     "Navy winter poster listing flu, COVID-19 and pneumococcal vaccinations.",
     ["house", "modern", "photo", "vaccination", "winter", "covid"], winter_vaccines),
    ("sys-house-travel-clinic", "Travel clinic", "Vaccination", SECOND_SET,
     "Travel health services in four cards under a photo and teal wave.",
     ["house", "modern", "photo", "travel", "vaccination", "malaria"], travel_clinic),
    ("sys-house-weight-support", "Weight management", "Awareness", SECOND_SET,
     "Supportive weight management poster: offset photo and a practical checklist.",
     ["house", "modern", "photo", "weight", "healthy lifestyle", "support"], weight_support),

    ("sys-house-shingles-vaccination", "Shingles vaccination", "Vaccination", THIRD_SET,
     "Circle photo, three facts and an NHS eligibility prompt for shingles vaccination.",
     ["house", "modern", "photo", "shingles", "vaccination", "over 65"], shingles_vaccination),
    ("sys-house-pneumonia-vaccination", "Pneumonia vaccination", "Vaccination", THIRD_SET,
     "Navy poster for the pneumococcal vaccine, with a white contact foot.",
     ["house", "modern", "photo", "pneumonia", "pneumococcal", "vaccination"], pneumonia_vaccination),
    ("sys-house-stop-smoking", "Stop smoking support", "Awareness", THIRD_SET,
     "Positive quit-smoking poster with a sky photo panel and support checklist.",
     ["house", "modern", "photo", "smoking", "quit", "support"], stop_smoking),
    ("sys-house-contraception", "Contraception advice", "Awareness", THIRD_SET,
     "Private, judgement-free contraception advice, with a circle photo on a soft band.",
     ["house", "modern", "photo", "contraception", "sexual health", "women's health"], contraception),
    ("sys-house-pharmacy-first", "Pharmacy First", "Promotion", THIRD_SET,
     "The seven NHS Pharmacy First conditions beside an arch photo (England).",
     ["house", "modern", "photo", "pharmacy first", "nhs", "minor illness"], pharmacy_first),
    ("sys-house-prescription-delivery", "Prescription delivery", "Promotion", THIRD_SET,
     "Three-step delivery explainer under a full-width photo.",
     ["house", "modern", "photo", "delivery", "prescriptions"], prescription_delivery),
    ("sys-house-antibiotics", "Antibiotics awareness", "Safety", THIRD_SET,
     "Photo-free poster with a capsule motif: when antibiotics help, and how to use them.",
     ["house", "modern", "antibiotics", "safety", "amr"], antibiotics_awareness),
    ("sys-house-medicine-returns", "Return unused medicines", "Safety", THIRD_SET,
     "Photo-free three-step guide to returning unused medicines safely.",
     ["house", "modern", "disposal", "safety", "medicines"], medicine_returns),
    ("sys-house-hay-fever", "Hay fever help", "Seasonal", THIRD_SET,
     "Spring photo over a teal wave, with NHS hay fever tips.",
     ["house", "modern", "photo", "hay fever", "allergy", "spring"], hay_fever),
    ("sys-house-repeat-prescriptions", "Repeat prescriptions", "General", THIRD_SET,
     "Three steps to repeat prescriptions, with NHS App help and a circle photo.",
     ["house", "modern", "photo", "repeat prescriptions", "nhs app"], repeat_prescriptions),
]


def build() -> list[dict]:
    out = []
    for tid, name, category, released, description, tags, fn in TEMPLATES:
        canvas = fn()
        out.append({
            "id": tid, "name": name, "description": description, "category": category,
            "createdAt": released,
            "tags": tags, "background": canvas.background, "shapes": canvas.shapes,
            "assets": sorted({ASSET[a].rsplit("/", 1)[1] for a in ASSET
                              if any(s.get("blobKey") == ASSET[a] for s in canvas.shapes)}),
        })
    return out


# ── checks ────────────────────────────────────────────────────────────────

#: Values of a realistic length for the tokens, and a long one for each.
TOKEN_SAMPLES = {
    "{{pharmacy_name}}": ["Riverside Community Pharmacy", "Greenfield Road Community Pharmacy Ltd"],
    "{{phone}}": ["020 7946 0000", "+44 (0)20 7946 0000"],
    "{{address}}": ["12 High Street, Riverside, RV1 2AB",
                    "Unit 4, Parkway Shopping Centre, Long Eaton, NG10 1AB"],
}

FORBIDDEN = [
    # Mirrors TemplateContactDetailsTests.Forbidden on the API side, so a
    # template that would fail `pnpm test` fails here first. "Your Pharmacy"
    # is matched case-insensitively there, so even "at your pharmacy" in a
    # sentence trips it — it reads as the placeholder name templates once had.
    (re.compile(r"555-0\d{3}"), "an invented phone number"),
    # Assembled, so the retired name never appears as a literal for
    # check-brand.mjs to (rightly) flag in this file.
    (re.compile("acme" + "pharmacy", re.I), "a website that belongs to nobody"),
    (re.compile(r"marketing25\.co\.uk", re.I), "our own domain on a pharmacy's poster"),
    (re.compile(r"\d+\s+Main Street|Anytown", re.I), "an invented postal address"),
    (re.compile(r"\bYour Pharmacy\b", re.I), "the 'Your Pharmacy' placeholder (API test)"),
    (re.compile(r"%"), "a percentage"),
    (re.compile(r"\b(off|sale|discount|save|free gift)\b", re.I), "a price promotion"),
    # "Medicine reviews" is a pharmacy service, not a customer review.
    (re.compile(r"(?<!medicine )(?<!medicines )(?<!medication )\b(reviews?|rated|stars?|"
                r"testimonials?|5/5)\b", re.I), "a review or rating"),
    (re.compile(r"[★☆✓✔✗•]"), "a glyph the PDF font may not carry"),
    (re.compile(r"\b(calpol|nurofen|panadol|lemsip|sudafed|piriton|beechams|gaviscon|"
                r"strepsils|clarityn|benadryl|anadin|voltarol|canesten|"
                # weight-loss, antimalarial and vaccine brands
                r"wegovy|mounjaro|ozempic|saxenda|alli|xenical|malarone|lariam|"
                r"fluenz|vaxigrip|comirnaty|spikevax|prevenar|shingrix|zostavax|pneumovax|"
                # stop-smoking, contraceptive and hay fever brands
                r"nicorette|niquitin|nicotinell|champix|zyban|microgynon|levonelle|ellaone|"
                r"cerazette|yasmin|beconase|piriteze|zirtek|telfast|optrex|otrivine)\b",
                re.I), "a medicine brand"),
]


#: Fonts with Arial's metrics, in order. The PDF draws with Liberation Sans and
#: the editor with the system sans; both are Arial-metric, so any of these
#: measures a line the way both renderers will. Liberation is what the
#: container ships; Arial is what a Mac or Windows machine has. Without one of
#: them the editor-wrap check cannot run at all — see `measuring_font`.
FONT_CANDIDATES = (
    "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
    "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
    "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
    "/opt/homebrew/share/fonts/LiberationSans-Regular.ttf",
    "/Library/Fonts/LiberationSans-Regular.ttf",
    "/System/Library/Fonts/Supplemental/Arial.ttf",
    "/Library/Fonts/Arial.ttf",
    "C:/Windows/Fonts/arial.ttf",
)


def measuring_font() -> str | None:
    """Path of the font the editor-wrap check measures with, or None."""
    import os
    override = os.getenv("HOUSE_FONT")
    if override and Path(override).exists():
        return override
    return next((p for p in FONT_CANDIDATES if Path(p).exists()), None)


def _font(size: int):
    from PIL import ImageFont
    path = measuring_font()
    return ImageFont.truetype(path, size) if path else None


def pdf_max_chars(width: float, size: float) -> int:
    """The PDF exporter's wrap rule (`QuestPdfExporter.WrapText`)."""
    return max(1, math.floor(width / (size * 0.55)))


def check(templates: list[dict] | None = None, measure: bool = True) -> list[str]:
    """Everything that would look wrong, or differ, between editor and PDF."""
    problems: list[str] = []
    for t in templates or build():
        tid, shapes = t["id"], t["shapes"]
        texts = [s for s in shapes if s["kind"] == "text"]
        all_text = " ".join(s["text"] for s in texts)

        qr = [s for s in shapes if s["id"] == BOOKING_QR_ID]
        if len(qr) != 1:
            problems.append(f"{tid}: {len(qr)} booking QR slots, want exactly 1")
        for token in TOKEN_SAMPLES:
            n = all_text.count(token)
            if n != 1:
                problems.append(f"{tid}: {token} appears {n} times, want 1")
        for pattern, why in FORBIDDEN:
            m = pattern.search(all_text)
            if m:
                problems.append(f"{tid}: {why}: {m.group(0)!r}")

        ids = [s["id"] for s in shapes]
        if len(ids) != len(set(ids)):
            problems.append(f"{tid}: duplicate shape ids")

        for s in shapes:
            x, y = s["position"]["x"], s["position"]["y"]
            if s["kind"] in ("rect", "image", "text"):
                w = s.get("width", 0)
                h = s.get("height", 0) if s["kind"] != "text" else s["fontSize"] * 1.15 * (s["text"].count("\n") + 1)
                if s["kind"] == "text" and (x < MARGIN - 1 or x + w > W - MARGIN + 1 or y + h > H):
                    problems.append(f"{tid}/{s['id']}: text outside the safe area")
                lx0, ly0, lx1, ly1 = LOGO_ZONE
                overlaps = x < lx1 and x + w > lx0 and y < ly1 and y + h > ly0
                if overlaps and not _is_page(s):
                    problems.append(f"{tid}/{s['id']}: sits in the logo corner")

        for s in texts:
            under = _colour_under(t, s)
            if under is not None:
                ratio = contrast(s["fill"], under)
                need = 3.0 if s["fontSize"] >= 60 else 4.5
                if ratio < need:
                    problems.append(f"{tid}/{s['id']}: contrast {ratio:.2f} on {under}, want {need}")

        for s in texts:
            size, width = s["fontSize"], s["width"]
            variants = [s["text"]]
            for token, samples in TOKEN_SAMPLES.items():
                if token in s["text"]:
                    variants = [s["text"].replace(token, v) for v in samples[:1]]
            for text in variants:
                for line in text.split("\n"):
                    if len(line) > pdf_max_chars(width, size):
                        problems.append(
                            f"{tid}/{s['id']}: {line!r} wraps in the PDF "
                            f"({len(line)} chars > {pdf_max_chars(width, size)})")
                    if measure and (font := _font(size)):
                        px = font.getlength(line)
                        if px > width:
                            problems.append(
                                f"{tid}/{s['id']}: {line!r} wraps in the editor ({px:.0f}px > {width})")
        problems += _overlaps(t)
        problems += _wave_gaps(t)
    return problems


def _wave_gaps(template: dict) -> list[str]:
    """A wave laid over the foot of a photo must be backed by it everywhere.

    Where the wave's top edge dips lower than the photo's bottom, a sliver of
    bare page shows between the two.
    """
    out = []
    images = [s for s in template["shapes"] if s["kind"] == "image" and s["id"] != BOOKING_QR_ID]

    def top_edge(w):
        top = list(zip(w["points"][0::2], w["points"][1::2]))[:-2]  # last two close the band
        return min(y for _, y in top), max(y for _, y in top)

    waves = [s for s in template["shapes"]
             if s["kind"] == "line" and s.get("closed") and len(s["points"]) >= 12]
    for w in waves:
        highest, lowest = top_edge(w)
        for im in images:
            iy0 = im["position"]["y"]
            iy1 = iy0 + im["height"]
            # A wave painted earlier whose dips all stay above the photo's foot
            # already covers the strip, as the teal ribbon does under a front wave.
            backed = any(o["zIndex"] < w["zIndex"] and top_edge(o)[1] <= iy1 + 0.5 for o in waves)
            if iy0 < highest < iy1 and lowest > iy1 + 0.5 and not backed:
                out.append(f"{template['id']}/{w['id']}: dips to {lowest:.0f}, below the foot of "
                           f"{im['id']} at {iy1:.0f} — bare page shows between them")
    return out


def _rendered_height(s: dict, longest: bool) -> float:
    """How tall a text is in the PDF, with the longest realistic token values."""
    text = s["text"]
    for token, samples in TOKEN_SAMPLES.items():
        text = text.replace(token, max(samples, key=len) if longest else samples[0])
    return len(_pdf_wrap(text, s["width"], s["fontSize"])) * s["fontSize"] * 1.15


def _overlaps(template: dict) -> list[str]:
    """Texts that would run into the text below them.

    With the *longest* sample values: a long pharmacy name or address wraps to
    a second line, and that line must land in space, not on the phone number.
    """
    texts = [s for s in template["shapes"] if s["kind"] == "text"]
    out = []
    for a in texts:
        ax, ay, aw = a["position"]["x"], a["position"]["y"], a["width"]
        bottom = ay + _rendered_height(a, longest=True)
        for b in texts:
            if b is a:
                continue
            bx, by, bw = b["position"]["x"], b["position"]["y"], b["width"]
            side_by_side = bx >= ax + aw or ax >= bx + bw
            if not side_by_side and ay < by < bottom - 2:
                out.append(f"{template['id']}/{a['id']}: runs into {b['id']} "
                           f"(ends at {bottom:.0f}, next starts at {by:.0f})")
    return out


def _luminance(hex_color: str) -> float:
    h = hex_color.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    c = [x / 12.92 if x <= 0.03928 else ((x + 0.055) / 1.055) ** 2.4 for x in c]
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]


def contrast(a: str, b: str) -> float:
    """WCAG contrast ratio between two colours."""
    hi, lo = sorted((_luminance(a), _luminance(b)), reverse=True)
    return (hi + 0.05) / (lo + 0.05)


def _colour_under(template: dict, text: dict) -> str | None:
    """The colour behind the middle of a text's first line, or None over a photo.

    Checks the topmost opaque shape beneath it — a rect, circle or polygon —
    and falls back to the page. A photo underneath is not a colour; its
    legibility is the fade's job, checked by eye.
    """
    x = text["position"]["x"] + text["width"] / 2
    y = text["position"]["y"] + text["fontSize"] * 0.6
    below = [s for s in template["shapes"] if s["zIndex"] < text["zIndex"] and s["opacity"] >= 0.99]
    for s in sorted(below, key=lambda s: s["zIndex"], reverse=True):
        sx, sy = s["position"]["x"], s["position"]["y"]
        if s["kind"] == "rect" and sx <= x <= sx + s["width"] and sy <= y <= sy + s["height"]:
            return s["fill"]
        if s["kind"] == "circle" and (x - sx) ** 2 + (y - sy) ** 2 <= s["radius"] ** 2:
            return s["fill"]
        if s["kind"] == "line" and s.get("closed") and _inside(x, y, s["points"]):
            return s["stroke"]
        if s["kind"] == "image" and sx <= x <= sx + s["width"] and sy <= y <= sy + s["height"]:
            return None
    return template["background"]


def _inside(x: float, y: float, flat: list[float]) -> bool:
    pts = list(zip(flat[0::2], flat[1::2]))
    inside = False
    for (x1, y1), (x2, y2) in zip(pts, pts[1:] + pts[:1]):
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
            inside = not inside
    return inside


def _is_page(shape: dict) -> bool:
    """Full-width bands and waves: page, not content, as far as a logo goes."""
    if shape["kind"] == "line" and shape.get("closed"):
        return True
    return shape["kind"] == "rect" and shape.get("width", 0) >= W and shape["id"] != "footer-bar"


def write(repo: Path) -> Path:
    path = repo / OUT
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(render_json())
    return path


def render_json() -> str:
    return json.dumps(build(), indent=1, ensure_ascii=False) + "\n"


# ── preview ───────────────────────────────────────────────────────────────


def preview(template: dict, asset_dir: Path | None = None, scale: float = 0.5):
    """A PNG of the design as the PDF would draw it: its fonts, its wrapping.

    For looking at, not for shipping. Photos that have not been built yet are
    drawn as grey stand-ins of the right shape.
    """
    from PIL import Image, ImageDraw

    img = Image.new("RGB", (round(W * scale), round(H * scale)), template["background"])
    for s in sorted(template["shapes"], key=lambda s: s["zIndex"]):
        layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        x, y = s["position"]["x"] * scale, s["position"]["y"] * scale
        a = round(255 * s["opacity"])
        if s["kind"] == "rect":
            fill = _rgba(s["fill"], a)
            d.rounded_rectangle((x, y, x + s["width"] * scale, y + s["height"] * scale),
                                radius=s.get("cornerRadius", 0) * scale, fill=fill)
        elif s["kind"] == "circle":
            r = s["radius"] * scale
            d.ellipse((x - r, y - r, x + r, y + r), fill=_rgba(s["fill"], a))
        elif s["kind"] == "line":
            pts = [(s["points"][i] * scale + x, s["points"][i + 1] * scale + y)
                   for i in range(0, len(s["points"]), 2)]
            if s.get("closed"):
                d.polygon(pts, fill=_rgba(s["stroke"], a))
            else:
                d.line(pts, fill=_rgba(s["stroke"], a), width=max(1, round(s["strokeWidth"] * scale)),
                       joint="curve")
        elif s["kind"] == "text":
            size = s["fontSize"]
            font = _font(max(1, round(size * scale)))
            text = s["text"]
            for token, samples in TOKEN_SAMPLES.items():
                text = text.replace(token, samples[0])
            lines = _pdf_wrap(text, s["width"], size)
            for i, line in enumerate(lines):
                ly = y + i * size * 1.15 * scale
                lw = font.getlength(line) if font else 0
                lx = x + {"center": (s["width"] * scale - lw) / 2,
                          "right": s["width"] * scale - lw}.get(s.get("align"), 0)
                d.text((lx, ly), line, font=font, fill=_rgba(s["fill"], a))
        elif s["kind"] == "image":
            w, h = round(s["width"] * scale), round(s["height"] * scale)
            src = s["blobKey"]
            photo = None
            if asset_dir and src.startswith("/api/assets/templates/"):
                f = asset_dir / src.rsplit("/", 1)[1]
                if f.exists():
                    photo = Image.open(f).convert("RGBA").resize((w, h))
            if photo is None:
                if src == BOOKING_QR_SRC:
                    photo = _qr_standin(w, h)
                else:
                    photo = Image.new("RGBA", (w, h), (148, 163, 184, 255))
                    spec = next((sp for sp in SPECS if src.endswith(sp.file_name)), None)
                    if spec is not None and spec.masked:
                        mask = Image.new("L", (w, h), 0)
                        md = ImageDraw.Draw(mask)
                        if spec.circle:
                            md.ellipse((0, 0, w - 1, h - 1), fill=255)
                        else:  # arch
                            md.ellipse((0, 0, w - 1, w - 1), fill=255)
                            md.rectangle((0, w // 2, w - 1, h - 1), fill=255)
                        photo.putalpha(mask)
            layer.paste(photo, (round(x), round(y)), photo)
        img = Image.alpha_composite(img.convert("RGBA"), layer).convert("RGB")
    return img


def _pdf_wrap(text: str, width: float, size: float) -> list[str]:
    out = []
    limit = pdf_max_chars(width, size)
    for hard in text.split("\n"):
        if len(hard) <= limit:
            out.append(hard)
            continue
        line = ""
        for word in hard.split(" "):
            test = word if not line else f"{line} {word}"
            if len(test) > limit and line:
                out.append(line)
                line = word
            else:
                line = test
        if line:
            out.append(line)
    return out


def _qr_standin(w: int, h: int):
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", (w, h), (255, 255, 255, 255))
    d = ImageDraw.Draw(img)
    cell = max(1, w // 21)
    for i in range(21):
        for j in range(21):
            if (i * 7 + j * 3 + i * j) % 5 < 2 or (i < 7 and j < 7) or (i < 7 and j > 13) or (i > 13 and j < 7):
                d.rectangle((i * cell, j * cell, (i + 1) * cell - 1, (j + 1) * cell - 1), fill=(15, 23, 42, 255))
    return img


def _rgba(hex_color: str, alpha: int = 255):
    h = hex_color.lstrip("#")
    return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), alpha


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--repo", default=str(Path(__file__).resolve().parents[4]))
    parser.add_argument("--preview", metavar="DIR", help="also write PNG previews here")
    args = parser.parse_args(argv)

    problems = check()
    if problems:
        print("\n".join(problems), file=sys.stderr)
        return 1
    path = write(Path(args.repo))
    print(f"wrote {path}")
    if args.preview:
        out = Path(args.preview)
        out.mkdir(parents=True, exist_ok=True)
        assets = Path(args.repo) / "apps/api/src/Acme.Infrastructure/Persistence/HouseTemplates/assets"
        for t in build():
            preview(t, assets).save(out / f"{t['id']}.png")
        print(f"previews in {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

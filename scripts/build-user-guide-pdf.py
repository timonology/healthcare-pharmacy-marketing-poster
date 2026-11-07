#!/usr/bin/env python3
"""
Builds docs/USER_GUIDE.pdf from docs/USER_GUIDE.html.

<b>The content is read out of the HTML, never retyped.</b> Two copies of the
same guide drift the moment somebody edits one of them, and the drift is silent
— the PDF is the copy that gets emailed to a pharmacy, so it is the copy that
would be wrong. Everything here comes from parsing the page: the wordmark, the
intro, the section headings, all twenty steps and the footer.

What this is *not* is a browser print. Rendering the real stylesheet needs a
browser engine, and there is none in this environment. So the layout is rebuilt
in ReportLab from the same measurements the stylesheet uses — A4, 14/12/12mm
padding, a two-column grid of 90mm columns with a 6mm gutter, 7mm numbered
badges, 8.6pt body text. It reads as the same document rather than a
pixel-identical one.

If you want the pixel-exact version, open the HTML in Chrome and print to PDF:
the stylesheet already sets `@page { size: A4; margin: 0 }`.

Usage: python3 scripts/build-user-guide-pdf.py
"""
from __future__ import annotations

import html as htmllib
import re
import sys

# How many numbered steps the guide should have. A guard against the parser
# silently dropping one, so it moves deliberately when the guide gains a step.
EXPECTED_STEPS = 24
from pathlib import Path

from reportlab.lib.colors import HexColor
from reportlab.lib.utils import simpleSplit
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "docs" / "USER_GUIDE.html"
OUT = ROOT / "docs" / "USER_GUIDE.pdf"

MM = 2.834645669  # points per millimetre
PAGE_W, PAGE_H = 210 * MM, 297 * MM

# Straight from the stylesheet's :root block.
PRIMARY = HexColor("#0e7490")
PRIMARY_DARK = HexColor("#164e63")
EMERALD = HexColor("#10b981")
INK = HexColor("#0f172a")
MUTED = HexColor("#64748b")
LINE = HexColor("#e2e8f0")
ACCENT_SOFT = HexColor("#ecfeff")

PAD_TOP, PAD_X, PAD_BOTTOM = 14 * MM, 12 * MM, 12 * MM
COL_GAP, ROW_GAP = 6 * MM, 3 * MM
BADGE = 7 * MM

FONT_DIR = Path("/usr/share/fonts/truetype/lato")
REG, BOLD, BLACK = "Lato", "Lato-Bold", "Lato-Black"

# Lato covers every character in the guide except the midline ellipsis used to
# name the poster card's overflow button. Three bullets say the same thing and
# are in the font; a missing glyph would draw a solid black box.
SUBSTITUTIONS = {"⋯": "•••"}


def register_fonts() -> None:
    for name, file in ((REG, "Lato-Regular.ttf"), (BOLD, "Lato-Bold.ttf"), (BLACK, "Lato-Black.ttf")):
        path = FONT_DIR / file
        if not path.exists():
            sys.exit(f"Missing font: {path}")
        pdfmetrics.registerFont(TTFont(name, str(path)))


def clean(fragment: str) -> str:
    """HTML fragment -> plain text, with unsupported glyphs swapped."""
    text = re.sub(r"<[^>]*>", " ", fragment)
    text = htmllib.unescape(text)
    text = " ".join(text.split())
    for bad, good in SUBSTITUTIONS.items():
        text = text.replace(bad, good)
    return text


def parse(source: str) -> dict:
    body = source[source.index("<body"):]

    def grab(pattern: str, default: str = "") -> str:
        m = re.search(pattern, body, re.S)
        return clean(m.group(1)) if m else default

    bullets = []
    for m in re.finditer(
        r'<div class="bullet">(.*?)</div>\s*</div>', body, re.S
    ):
        block = m.group(1)
        num = re.search(r'<div class="num">(.*?)</div>', block, re.S)
        strong = re.search(r"<strong>(.*?)</strong>", block, re.S)
        body_div = re.search(r'<div class="body">(.*?)$', block, re.S)
        if not (num and strong and body_div):
            continue
        rest = re.sub(r"<strong>.*?</strong>", "", body_div.group(1), count=1, flags=re.S)
        bullets.append((clean(num.group(1)), clean(strong.group(1)), clean(rest)))

    sections = [clean(m) for m in re.findall(r'<span class="label">(.*?)</span>', body, re.S)]
    if not sections:
        sections = [clean(m) for m in re.findall(r'class="label"[^>]*>(.*?)<', body, re.S)]

    # Which steps sit under which heading, read from the document rather than
    # inferred from a division.
    #
    # The layout used to compute `ceil(steps / sections)` and slice the list.
    # That is right only while every section holds the same number of steps: at
    # twenty steps across five headings it gave four each and looked correct,
    # and the first uneven section — twenty-one steps — silently re-paired every
    # heading with the wrong steps and grew the page by 48mm. A wrong pairing is
    # worse than an overflow, because the overflow is at least visible.
    grouping: list[list[int]] = []
    if sections:
        chunks = re.split(r'<span class="label">.*?</span>', body, flags=re.S)[1:]
        for chunk in chunks:
            nums = re.findall(r'<div class="num">(.*?)</div>', chunk, re.S)
            grouping.append([clean(n) for n in nums])

    return {
        "brand": grab(r'<span class="name">(.*?)</span>', "Marketing25"),
        "tagline": grab(r'<span class="tag">(.*?)</span>'),
        "title": grab(r'<div class="title">(.*?)</div>'),
        "subtitle": grab(r'<div class="subtitle">(.*?)</div>'),
        "intro": grab(r'<div class="intro-text">(.*?)</div>'),
        "sections": sections,
        "grouping": grouping,
        "bullets": bullets,
        "footer": [clean(x) for x in re.findall(r'<div>(©[^<]*)</div>', body)],
        "contact": grab(r"(support@[^<\s]+)"),
        # The domain sits after an inline SVG inside the footer badge, so the
        # whitespace between the tags is not optional.
        "site": grab(r'<span class="badge">(.*?)</span>'),
    }


def wrap(text: str, font: str, size: float, width: float) -> list[str]:
    """ReportLab measures the real glyphs, so this wraps exactly as it draws."""
    return simpleSplit(text, font, size, width)


def draw_mark(c, x: float, y: float, size: float) -> None:
    """The Marketing25 mark (apps/web/src/components/brand/LogoMark.tsx), drawn
    on a 64-unit grid scaled to `size`, with (x, y) its bottom-left corner."""
    k = size / 64
    px = lambda u: x + u * k          # noqa: E731
    py = lambda v: y + (64 - v) * k   # noqa: E731 — SVG y runs downwards
    c.setFillColor(HexColor("#11798A"))
    c.roundRect(x, y, size, size, 16 * k, stroke=0, fill=1)
    pts = [(26, 14), (34, 14), (34, 22), (49, 32), (34, 42), (34, 50), (26, 50),
           (26, 36), (14, 36), (14, 28), (26, 28)]
    path = c.beginPath()
    path.moveTo(px(pts[0][0]), py(pts[0][1]))
    for u, v in pts[1:]:
        path.lineTo(px(u), py(v))
    path.close()
    c.setFillColor(HexColor("#ffffff"))
    c.setStrokeColor(HexColor("#ffffff"))
    c.setLineWidth(4 * k)
    c.setLineJoin(1)
    c.drawPath(path, stroke=1, fill=1)
    c.setFillColor(HexColor("#7FE3D6"))
    c.circle(px(51), py(13), 4 * k, stroke=0, fill=1)


def build(data: dict) -> float:
    c = canvas.Canvas(str(OUT), pagesize=(PAGE_W, PAGE_H))
    c.setTitle(f"{data['brand']} · {data['title']}")
    c.setAuthor("Sonar Informatics")
    c.setSubject(data["subtitle"])

    x0, x1 = PAD_X, PAGE_W - PAD_X
    y = PAGE_H - PAD_TOP

    # ---- header -----------------------------------------------------------
    draw_mark(c, x0, y - 9 * MM, 9 * MM)

    c.setFillColor(INK)
    c.setFont(BLACK, 15)
    c.drawString(x0 + 11.5 * MM, y - 4.6 * MM, data["brand"])
    c.setFillColor(MUTED)
    c.setFont(REG, 8)
    c.drawString(x0 + 11.5 * MM, y - 8.4 * MM, data["tagline"])

    c.setFillColor(PRIMARY_DARK)
    c.setFont(BOLD, 13)
    c.drawRightString(x1, y - 4.6 * MM, data["title"])
    c.setFillColor(MUTED)
    c.setFont(REG, 7.6)
    c.drawRightString(x1, y - 8.4 * MM, data["subtitle"])

    y -= 13 * MM
    c.setStrokeColor(LINE)
    c.setLineWidth(1.2)
    c.line(x0, y, x1, y)
    y -= 5 * MM

    # ---- intro ------------------------------------------------------------
    intro_lines = wrap(data["intro"], REG, 8.8, x1 - x0 - 12 * MM)
    box_h = len(intro_lines) * 4.2 * MM + 5 * MM
    c.setFillColor(ACCENT_SOFT)
    c.roundRect(x0, y - box_h, x1 - x0, box_h, 2 * MM, stroke=0, fill=1)
    c.setFillColor(PRIMARY)
    cx, cy = x0 + 6 * MM, y - box_h / 2
    c.circle(cx, cy, 3 * MM, stroke=0, fill=1)

    # The tick is drawn, not typed. U+2713 is absent from Lato, and ReportLab
    # renders a missing glyph as .notdef — an empty box in the middle of the
    # first thing anybody looks at. Two strokes need no font at all.
    c.setStrokeColor(HexColor("#ffffff"))
    c.setLineWidth(0.9 * MM)
    c.setLineCap(1)
    c.setLineJoin(1)
    tick = c.beginPath()
    tick.moveTo(cx - 1.35 * MM, cy + 0.1 * MM)
    tick.lineTo(cx - 0.35 * MM, cy - 1.0 * MM)
    tick.lineTo(cx + 1.45 * MM, cy + 1.1 * MM)
    c.drawPath(tick, stroke=1, fill=0)

    ty = y - 4.6 * MM
    c.setFillColor(INK)
    # "Welcome." is bold in the HTML. Rendering one bold word mid-line means
    # re-implementing inline rich text for a single emphasis, so the intro is
    # set in one weight.
    c.setFont(REG, 8.8)
    for ln in intro_lines:
        c.drawString(x0 + 12 * MM, ty, ln)
        ty -= 4.2 * MM
    y -= box_h + 5 * MM

    # ---- bullets ----------------------------------------------------------
    col_w = (x1 - x0 - COL_GAP) / 2
    text_w = col_w - BADGE - 3 * MM
    by_number = {num: (num, title, text) for num, title, text in data["bullets"]}

    def steps_for(index: int) -> list[tuple[str, str, str]]:
        """The steps under one heading, in document order."""
        grouping = data.get("grouping") or []
        if index < len(grouping):
            return [by_number[n] for n in grouping[index] if n in by_number]

        # Only reachable if the HTML stops using `.section-label`, in which case
        # an even split is the best guess available — but say so rather than
        # producing a confidently wrong page.
        print(
            "WARNING: could not read the step grouping; falling back to an even split",
            file=sys.stderr,
        )
        per = max(1, -(-len(data["bullets"]) // max(1, len(data["sections"]))))
        return data["bullets"][index * per: (index + 1) * per]

    def bullet_height(title: str, body: str) -> float:
        t = len(wrap(title, BOLD, 9, text_w)) * 4.3 * MM
        b = len(wrap(body, REG, 8.6, text_w)) * 4.1 * MM
        return max(t + b + 0.6 * MM, BADGE)

    for s, section in enumerate(data["sections"]):
        # section heading, full width
        c.setFillColor(PRIMARY)
        c.circle(x0 + 1.6 * MM, y - 1.4 * MM, 1.6 * MM, stroke=0, fill=1)
        c.setFillColor(PRIMARY_DARK)
        c.setFont(BLACK, 9.4)
        c.drawString(x0 + 5 * MM, y - 2.6 * MM, section.upper())
        label_w = pdfmetrics.stringWidth(section.upper(), BLACK, 9.4)
        c.setStrokeColor(LINE)
        c.setLineWidth(0.8)
        c.line(x0 + 7 * MM + label_w, y - 1.4 * MM, x1, y - 1.4 * MM)
        y -= 6.5 * MM

        pairs = steps_for(s)

        for r in range(0, len(pairs), 2):
            row = pairs[r: r + 2]
            row_h = max(bullet_height(t, b) for _, t, b in row)

            for col, (num, title, body) in enumerate(row):
                bx = x0 + col * (col_w + COL_GAP)
                by = y

                # numbered badge, teal->emerald like the CSS gradient
                c.saveState()
                path = c.beginPath()
                path.circle(bx + BADGE / 2, by - BADGE / 2, BADGE / 2)
                c.clipPath(path, stroke=0, fill=0)
                c.linearGradient(
                    bx, by - BADGE, bx + BADGE, by,
                    (PRIMARY, EMERALD), (0.0, 1.0), extend=True,
                )
                c.restoreState()

                c.setFillColor(HexColor("#ffffff"))
                c.setFont(BLACK, 8.5)
                c.drawCentredString(bx + BADGE / 2, by - BADGE / 2 - 1.05 * MM, num)

                tx = bx + BADGE + 3 * MM
                ty = by - 2.9 * MM
                c.setFillColor(PRIMARY_DARK)
                c.setFont(BOLD, 9)
                for ln in wrap(title, BOLD, 9, text_w):
                    c.drawString(tx, ty, ln)
                    ty -= 4.3 * MM

                ty -= 0.4 * MM
                c.setFillColor(INK)
                c.setFont(REG, 8.6)
                for ln in wrap(body, REG, 8.6, text_w):
                    c.drawString(tx, ty, ln)
                    ty -= 4.1 * MM

            y -= row_h + ROW_GAP

        y -= 1.5 * MM

    # ---- footer -----------------------------------------------------------
    fy = PAD_BOTTOM + 4 * MM
    c.setStrokeColor(LINE)
    c.setLineWidth(0.8)
    c.line(x0, fy + 4 * MM, x1, fy + 4 * MM)
    c.setFillColor(MUTED)
    c.setFont(REG, 7.4)
    left = f"Need help?  {data['contact']}" if data["contact"] else "Need help?"
    c.drawString(x0, fy, left)
    if data["site"]:
        c.setFillColor(PRIMARY)
        c.setFont(BOLD, 7.4)
        c.drawCentredString(PAGE_W / 2, fy, data["site"])
    if data["footer"]:
        c.setFillColor(MUTED)
        c.setFont(REG, 7.4)
        c.drawRightString(x1, fy, data["footer"][0])

    c.showPage()
    c.save()

    # The page is fixed and one-sided. If the layout ever outgrows it, say so
    # loudly rather than shipping a guide with its last steps missing.
    clearance = (y - (fy + 8 * MM)) / MM
    if clearance < 0:
        print(
            f"WARNING: content runs {abs(clearance):.1f}mm into the footer — the page "
            "is full and the last steps will collide with it. Shorten a step before "
            "adding another.",
            file=sys.stderr,
        )
    return clearance


def main() -> None:
    register_fonts()
    data = parse(SRC.read_text(encoding="utf-8"))

    if len(data["bullets"]) != EXPECTED_STEPS:
        print(
            f"WARNING: parsed {len(data['bullets'])} steps, expected {EXPECTED_STEPS}",
            file=sys.stderr,
        )

    clearance = build(data)
    print(
        f"{OUT.relative_to(ROOT)}  ({OUT.stat().st_size / 1024:.0f} KB, "
        f"{len(data['bullets'])} steps, {clearance:.1f}mm spare above the footer)"
    )


if __name__ == "__main__":
    main()

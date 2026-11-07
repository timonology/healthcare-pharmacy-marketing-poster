#!/usr/bin/env python3
"""
Builds docs/USER_GUIDE_FULL.pdf from docs/USER_GUIDE_FULL.md.

Same principle as the QA script's build: <b>the markdown is the source and
nothing here is retyped</b>, because two copies of a customer-facing guide drift
and the PDF is the copy that gets emailed.

Different renderer, though. The QA script is a grid of test cases; this is prose
with bullets, quotes and the occasional table, so it needs list handling,
chapter breaks and a contents page the QA script has no use for.

Usage: python3 scripts/build-user-guide-full-pdf.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import (
    BaseDocTemplate,
    CondPageBreak,
    Frame,
    KeepTogether,
    ListFlowable,
    ListItem,
    PageBreak,
    PageTemplate,
    Paragraph,
    Spacer,
    Table,
    TableStyle,
)

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "docs" / "USER_GUIDE_FULL.md"
OUT = ROOT / "docs" / "USER_GUIDE_FULL.pdf"

INK = colors.HexColor("#0f172a")
MUTED = colors.HexColor("#64748b")
LINE = colors.HexColor("#cbd5e1")
TEAL = colors.HexColor("#0e7490")
DARK = colors.HexColor("#164e63")
ZEBRA = colors.HexColor("#f8fafc")

styles = getSampleStyleSheet()


def st(name, **kw):
    return ParagraphStyle(name, parent=styles["Normal"], **kw)


BODY = st("body", fontSize=9.4, leading=13.4, textColor=INK, spaceAfter=6)
CELL = st("cell", fontSize=8.4, leading=11, textColor=INK)
CELLH = st("cellh", fontSize=8.4, leading=11, textColor=colors.white,
           fontName="Helvetica-Bold")
H1 = st("h1", fontSize=17, leading=21, spaceBefore=2, spaceAfter=9,
        textColor=TEAL, fontName="Helvetica-Bold")
H2 = st("h2", fontSize=11.4, leading=15, spaceBefore=11, spaceAfter=4,
        textColor=DARK, fontName="Helvetica-Bold")
TITLE = st("title", fontSize=26, leading=30, textColor=TEAL,
           fontName="Helvetica-Bold", spaceAfter=6)
SUB = st("sub", fontSize=11, leading=15, textColor=MUTED, spaceAfter=16)
NUMBERED = st("numbered", fontSize=9.6, leading=14, textColor=INK,
              leftIndent=14, spaceAfter=2)
NOTE = st("note", fontSize=8.6, leading=12, textColor=MUTED,
          fontName="Helvetica-Oblique", spaceBefore=8)

# Characters the built-in fonts cannot draw. Outside WinAnsi a glyph renders as
# a solid black box that survives text extraction unchanged — the QA script
# shipped one before this guard existed. See build-qa-script-pdf.py.
PDF_SAFE = {"→": " > ", "⋯": "...", "…": "...", "≥": ">=", "≤": "<=",
            "×": "x", "✓": "Y", "✗": "N", "—": "—", "–": "–"}
_unmapped: set[str] = set()


def safe(text: str) -> str:
    for bad, good in PDF_SAFE.items():
        text = text.replace(bad, good)
    for ch in text:
        if ord(ch) > 127:
            try:
                ch.encode("cp1252")
            except UnicodeEncodeError:
                _unmapped.add(ch)
    return text


def inline(md: str) -> str:
    t = safe(md)
    t = t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    # Links: keep the label, drop the target — a printed page cannot follow one.
    t = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", t)
    t = re.sub(r"`([^`]+)`", r'<font face="Courier" size="8.6">\1</font>', t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", t)
    t = re.sub(r"(?<!\*)\*([^*]+)\*(?!\*)", r"<i>\1</i>", t)
    return t


def cells(line: str) -> list[str]:
    return [c.strip() for c in line.strip().strip("|").split("|")]


def build_table(header, rows):
    ncol = len(header)
    usable = 170 * mm
    if ncol == 2:
        widths = [usable * 0.34, usable * 0.66]
    elif ncol == 3:
        widths = [usable * 0.26, usable * 0.37, usable * 0.37]
    else:
        widths = [usable / ncol] * ncol

    data = [[Paragraph(inline(c), CELLH) for c in header]]
    for r in rows:
        r = (r + [""] * ncol)[:ncol]
        data.append([Paragraph(inline(c), CELL) for c in r])

    t = Table(data, colWidths=widths, repeatRows=1, hAlign="LEFT")
    s = [
        ("BACKGROUND", (0, 0), (-1, 0), TEAL),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("GRID", (0, 0), (-1, -1), 0.4, LINE),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
        ("LEFTPADDING", (0, 0), (-1, -1), 5),
        ("RIGHTPADDING", (0, 0), (-1, -1), 5),
    ]
    for i in range(1, len(data)):
        if i % 2 == 0:
            s.append(("BACKGROUND", (0, i), (-1, i), ZEBRA))
    t.setStyle(TableStyle(s))
    return t


def decorate(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7.4)
    canvas.setFillColor(MUTED)
    if doc.page > 1:
        canvas.drawString(20 * mm, 12 * mm, "Marketing25 — the complete guide")
        canvas.drawRightString(A4[0] - 20 * mm, 12 * mm, str(doc.page))
        canvas.setStrokeColor(LINE)
        canvas.setLineWidth(0.4)
        canvas.line(20 * mm, 15 * mm, A4[0] - 20 * mm, 15 * mm)
    canvas.restoreState()


def main() -> None:
    if not SRC.exists():
        print(f"missing {SRC}", file=sys.stderr)
        raise SystemExit(1)

    md = SRC.read_text(encoding="utf-8")
    lines = md.splitlines()

    doc = BaseDocTemplate(
        str(OUT), pagesize=A4,
        leftMargin=20 * mm, rightMargin=20 * mm,
        topMargin=18 * mm, bottomMargin=20 * mm,
        title="Marketing25 — the complete guide", author="Sonar Informatics",
    )
    doc.addPageTemplates([PageTemplate(
        id="p",
        frames=[Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height)],
        onPage=decorate,
    )])

    story: list = []
    bullets: list[str] = []
    para: list[str] = []
    chapters = 0
    seen_title = False

    def flush_para():
        nonlocal para
        if para:
            story.append(Paragraph(inline(" ".join(para)), BODY))
            para = []

    def flush_bullets():
        nonlocal bullets
        if bullets:
            story.append(ListFlowable(
                [ListItem(Paragraph(inline(b), BODY), leftIndent=14)
                 for b in bullets],
                bulletType="bullet", start="•", leftIndent=12,
                bulletFontSize=7, spaceAfter=6,
            ))
            bullets = []

    i, n = 0, len(lines)
    while i < n:
        ln = lines[i]

        if ln.startswith("# "):
            flush_para(); flush_bullets()
            story.append(Paragraph(inline(ln[2:]), TITLE))
            seen_title = True
            i += 1
            continue

        if ln.startswith("## "):
            flush_para(); flush_bullets()
            heading = ln[3:].strip()
            # Each numbered chapter starts a page; the contents list does not.
            if re.match(r"^\d+\.", heading):
                chapters += 1
                story.append(PageBreak())
            story.append(Paragraph(inline(heading), H1))
            i += 1
            continue

        if ln.startswith("### "):
            flush_para(); flush_bullets()
            story.append(CondPageBreak(28 * mm))
            story.append(Paragraph(inline(ln[4:]), H2))
            i += 1
            continue

        if ln.strip() == "---":
            flush_para(); flush_bullets()
            i += 1
            continue

        if ln.strip().startswith("|") and i + 1 < n and re.match(
            r"^\s*\|[\s:|-]+\|\s*$", lines[i + 1]
        ):
            flush_para(); flush_bullets()
            header = cells(ln)
            i += 2
            rows = []
            while i < n and lines[i].strip().startswith("|"):
                rows.append(cells(lines[i])); i += 1
            t = build_table(header, rows)
            story.append(t if len(rows) > 6 else KeepTogether(t))
            story.append(Spacer(1, 9))
            continue

        m = re.match(r"^\s*[-*]\s+(.*)", ln)
        if m:
            flush_para()
            bullets.append(m.group(1))
            i += 1
            continue

        # A wrapped bullet. Markdown continues a list item by indenting the
        # next line under it, and without this the item was cut at the first
        # line break — "Turn off the device's sleep timer. The page asks the
        # browser to keep the" — while the rest of the sentence came out as a
        # loose paragraph *above* the list, because paragraphs and bullets are
        # flushed from different buffers.
        #
        # Every bullet in this guide that runs past one line was affected, which
        # is most of the important ones.
        if bullets and re.match(r"^\s+\S", ln) and not ln.strip().startswith("|"):
            bullets[-1] += " " + ln.strip()
            i += 1
            continue

        m = re.match(r"^\s*(\d+)\.\s+(.*)", ln)
        if m and not ln.startswith("  "):
            flush_para(); flush_bullets()
            # Markdown bold, not an <b> tag. inline() escapes `<` and `>` before
            # it converts emphasis, so raw markup handed to it comes out as
            # visible "<b>1.</b>" — which is exactly what the contents page did
            # on the first build.
            #
            # Rendered as an indented paragraph rather than a ListFlowable: the
            # number is already in the text, and a bullet in front of it reads
            # as "• 1. Your account".
            story.append(Paragraph(
                inline(f"**{m.group(1)}.**  {m.group(2)}"), NUMBERED))
            i += 1
            continue

        if not ln.strip():
            flush_para(); flush_bullets()
            i += 1
            continue

        if ln.startswith("*") and ln.rstrip().endswith("*") and len(ln) > 60:
            flush_para(); flush_bullets()
            story.append(Paragraph(inline(ln.strip("*").strip()), NOTE))
            i += 1
            continue

        para.append(ln.strip())
        i += 1

    flush_para()
    flush_bullets()

    if not seen_title:
        print("WARNING: no H1 title found", file=sys.stderr)

    doc.build(story)

    if _unmapped:
        listed = ", ".join(f"U+{ord(c):04X} {c!r}" for c in sorted(_unmapped))
        print(
            f"WARNING: {listed} are outside the built-in font and will have drawn "
            f"as black boxes. Add them to PDF_SAFE.",
            file=sys.stderr,
        )

    print(f"{OUT.relative_to(ROOT)}  ({OUT.stat().st_size / 1024:.0f} KB, "
          f"{chapters} chapters)")


if __name__ == "__main__":
    main()

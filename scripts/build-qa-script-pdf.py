#!/usr/bin/env python3
"""
Builds docs/QA_TEST_SCRIPT.pdf from docs/QA_TEST_SCRIPT.md.

<b>The markdown is the source; nothing here is retyped.</b> A QA script that
exists twice drifts, and the drift is silent — the PDF is the copy a tester
prints and works from, so it is the copy that would be wrong.

The one thing the PDF adds is a **Result** column on every case table, because
a printed script needs somewhere to write PASS or FAIL. It is added
structurally, not typed into the markdown, so the two cannot disagree about how
many cases there are.

Usage: python3 scripts/build-qa-script-pdf.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import (
    BaseDocTemplate,
    Frame,
    KeepTogether,
    PageTemplate,
    Paragraph,
    Spacer,
    Table,
    TableStyle,
)

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "docs" / "QA_TEST_SCRIPT.md"
OUT = ROOT / "docs" / "QA_TEST_SCRIPT.pdf"

INK = colors.HexColor("#0f172a")
MUTED = colors.HexColor("#64748b")
LINE = colors.HexColor("#cbd5e1")
HEAD_BG = colors.HexColor("#0e7490")
ZEBRA = colors.HexColor("#f1f5f9")
S1 = colors.HexColor("#b91c1c")

styles = getSampleStyleSheet()


def style(name: str, **kw) -> ParagraphStyle:
    return ParagraphStyle(name, parent=styles["Normal"], **kw)


BODY = style("body", fontSize=8.6, leading=11.4, textColor=INK, alignment=TA_LEFT)
CELL = style("cell", fontSize=7.5, leading=9.6, textColor=INK)
CELL_H = style("cellh", fontSize=7.5, leading=9.6, textColor=colors.white,
               fontName="Helvetica-Bold")
H1 = style("h1", fontSize=15, leading=19, spaceBefore=14, spaceAfter=7,
           textColor=HEAD_BG, fontName="Helvetica-Bold")
H2 = style("h2", fontSize=10.5, leading=14, spaceBefore=10, spaceAfter=4,
           textColor=INK, fontName="Helvetica-Bold")
TITLE = style("title", fontSize=22, leading=26, textColor=HEAD_BG,
              fontName="Helvetica-Bold", spaceAfter=4)


# Characters the built-in fonts cannot draw, and what to draw instead.
#
# ReportLab's standard fonts are WinAnsi-encoded. A character outside that set
# is not dropped and does not raise — it renders as a **solid black box**, which
# survives every automated check because the text layer still extracts the
# original character. The first build of this document shipped `⋯` as a box in
# case 4.4 and the extracted text looked perfect.
#
# Substituted here rather than in the markdown so the source keeps proper
# typography for anyone reading it on GitHub.
PDF_SAFE = {
    "→": " > ",    # → rightwards arrow
    "⋯": "...",    # ⋯ midline horizontal ellipsis
    "…": "...",    # … horizontal ellipsis
    "≥": ">=",
    "≤": "<=",
    "×": "x",
    "✓": "Y",
    "✗": "N",
}

_unmapped: set[str] = set()


def pdf_safe(text: str) -> str:
    for bad, good in PDF_SAFE.items():
        text = text.replace(bad, good)
    # Anything still outside the font's encoding would draw as a box. Collect
    # it and fail loudly at the end rather than shipping a blemish nobody sees.
    for ch in text:
        if ord(ch) > 127:
            try:
                ch.encode("cp1252")
            except UnicodeEncodeError:
                _unmapped.add(ch)
    return text


def inline(md: str) -> str:
    """Markdown emphasis to ReportLab markup, escaping XML first."""
    t = pdf_safe(md)
    t = t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    t = re.sub(r"`([^`]+)`", r'<font face="Courier">\1</font>', t)
    t = re.sub(r"\*\*([^*]+)\*\*", r"<b>\1</b>", t)
    t = re.sub(r"(?<!\*)\*([^*]+)\*(?!\*)", r"<i>\1</i>", t)
    return t


def split_row(line: str) -> list[str]:
    return [c.strip() for c in line.strip().strip("|").split("|")]


def parse(md: str) -> list:
    """Markdown to a flat list of ('h1'|'h2'|'p'|'table'|'rule', payload)."""
    out: list = []
    lines = md.splitlines()
    i, n = 0, len(lines)
    para: list[str] = []

    def flush():
        nonlocal para
        if para:
            out.append(("p", " ".join(para)))
            para = []

    while i < n:
        ln = lines[i]
        if ln.startswith("# "):
            flush(); out.append(("h1", ln[2:].strip())); i += 1; continue
        if ln.startswith("## "):
            flush(); out.append(("h1", ln[3:].strip())); i += 1; continue
        if ln.startswith("### "):
            flush(); out.append(("h2", ln[4:].strip())); i += 1; continue
        if ln.strip() == "---":
            flush(); out.append(("rule", None)); i += 1; continue

        # A table: a pipe row followed by a separator row of dashes.
        if ln.strip().startswith("|") and i + 1 < n and re.match(
            r"^\s*\|[\s:|-]+\|\s*$", lines[i + 1]
        ):
            flush()
            header = split_row(ln)
            i += 2
            rows = []
            while i < n and lines[i].strip().startswith("|"):
                rows.append(split_row(lines[i])); i += 1
            out.append(("table", (header, rows)))
            continue

        if not ln.strip():
            flush(); i += 1; continue

        para.append(ln.strip())
        i += 1

    flush()
    return out


def build_table(header: list[str], rows: list[list[str]]):
    # A case table is one whose first column is the ID. Only those get a place
    # to write a result — the legend and sign-off tables must not.
    is_cases = header and header[0].strip().lower() == "id"

    if is_cases:
        header = header + ["Result"]
        rows = [r + [""] for r in rows]
        widths = [26, 30, 24, 152, 214, 45]
    elif len(header) == 2:
        widths = [150, 341]
    else:
        widths = [491 / max(1, len(header))] * len(header)

    data = [[Paragraph(inline(c), CELL_H) for c in header]]
    for r in rows:
        r = (r + [""] * len(header))[: len(header)]
        data.append([Paragraph(inline(c), CELL) for c in r])

    t = Table(data, colWidths=widths, repeatRows=1, hAlign="LEFT")
    st = [
        ("BACKGROUND", (0, 0), (-1, 0), HEAD_BG),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("GRID", (0, 0), (-1, -1), 0.4, LINE),
        ("TOPPADDING", (0, 0), (-1, -1), 3),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
        ("LEFTPADDING", (0, 0), (-1, -1), 4),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4),
    ]
    for r in range(1, len(data)):
        if r % 2 == 0:
            st.append(("BACKGROUND", (0, r), (-1, r), ZEBRA))
    # S1 is the severity that stops a release. Make it findable by eye.
    if is_cases:
        for r, row in enumerate(rows, start=1):
            if len(row) > 2 and row[2].strip().upper().startswith("S1"):
                st.append(("TEXTCOLOR", (2, r), (2, r), S1))
                st.append(("FONTNAME", (2, r), (2, r), "Helvetica-Bold"))
    t.setStyle(TableStyle(st))
    return t


def decorate(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 7.2)
    canvas.setFillColor(MUTED)
    canvas.drawString(20 * mm, 12 * mm, "Marketing25 — QA test script")
    canvas.drawRightString(A4[0] - 20 * mm, 12 * mm, f"Page {doc.page}")
    canvas.setStrokeColor(LINE)
    canvas.setLineWidth(0.4)
    canvas.line(20 * mm, 15 * mm, A4[0] - 20 * mm, 15 * mm)
    canvas.restoreState()


def main() -> None:
    if not SRC.exists():
        print(f"missing {SRC}", file=sys.stderr)
        raise SystemExit(1)

    blocks = parse(SRC.read_text(encoding="utf-8"))

    doc = BaseDocTemplate(
        str(OUT), pagesize=A4,
        leftMargin=20 * mm, rightMargin=20 * mm,
        topMargin=16 * mm, bottomMargin=20 * mm,
        title="Marketing25 — QA test script", author="Sonar Informatics",
    )
    frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height, id="f")
    doc.addPageTemplates([PageTemplate(id="p", frames=[frame], onPage=decorate)])

    story: list = []
    cases = 0
    first_h1 = True

    for kind, payload in blocks:
        if kind == "h1":
            if first_h1:
                story.append(Paragraph(inline(payload), TITLE))
                first_h1 = False
            else:
                story.append(Paragraph(inline(payload), H1))
        elif kind == "h2":
            story.append(Paragraph(inline(payload), H2))
        elif kind == "p":
            story.append(Paragraph(inline(payload), BODY))
            story.append(Spacer(1, 4))
        elif kind == "rule":
            story.append(Spacer(1, 3))
        elif kind == "table":
            header, rows = payload
            if header and header[0].strip().lower() == "id":
                cases += len(rows)
            t = build_table(header, rows)
            # Keep a small table with the heading above it; let a long one flow.
            story.append(t if len(rows) > 8 else KeepTogether(t))
            story.append(Spacer(1, 8))

    doc.build(story)

    # The count in the sign-off block is written by hand in the markdown. If it
    # disagrees with the number of case rows, say so — a tester reconciling
    # "90 of 100" against a script that has 90 has no way to know which is right.
    claimed = re.search(r"Cases run \| of (\d+)", SRC.read_text(encoding="utf-8"))
    if claimed and int(claimed.group(1)) != cases:
        print(
            f"WARNING: sign-off says {claimed.group(1)} cases, the script has {cases}",
            file=sys.stderr,
        )

    if _unmapped:
        listed = ", ".join(f"U+{ord(c):04X} {c!r}" for c in sorted(_unmapped))
        print(
            f"WARNING: {listed} are outside the built-in font and will have drawn as "
            f"black boxes. Add them to PDF_SAFE.",
            file=sys.stderr,
        )

    print(f"{OUT.relative_to(ROOT)}  ({OUT.stat().st_size / 1024:.0f} KB, {cases} cases)")


if __name__ == "__main__":
    main()

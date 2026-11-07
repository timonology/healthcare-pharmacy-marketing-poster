#!/usr/bin/env python3
"""Snapshot house templates for the marketing site.

The home page shows real house templates — the hero poster and one card per
category — drawn by the same CanvasPreview the gallery uses. It must not
depend on the API being up (it is the most public page we have, and it is
statically rendered), so it carries its own copies:

  apps/web/src/components/landing/house-previews.generated.ts
      the canvases, exactly as seeded, minus the brand references a visitor
      has none of (the booking QR and the pharmacy logo would draw nothing);
  apps/web/public/marketing/house/<template>.webp
      each template photograph, resized for the size it is shown at and
      re-encoded as WebP (alpha kept, so circle crops stay circles).

Source of truth: apps/api/src/Acme.Infrastructure/Persistence/HouseTemplates —
the files the API seeds from. Re-run this after the house set changes:

    python3 scripts/marketing/build-house-previews.py

`house-previews.test.ts` fails if the snapshot drifts from the seed, so a
changed template cannot quietly leave the marketing site showing the old one.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SEED = ROOT / "apps/api/src/Acme.Infrastructure/Persistence/HouseTemplates"
WEB = ROOT / "apps/web"
OUT_TS = WEB / "src/components/landing/house-previews.generated.ts"
OUT_IMG = WEB / "public/marketing/house"
PUBLIC_PREFIX = "/marketing/house"

#: The hero poster, then one per category — the categories the gallery has.
HERO = "sys-house-trusted-circle"
SHOWCASE = [
    "sys-house-flu-vaccination",      # Vaccination
    "sys-house-pharmacy-first",       # Promotion
    "sys-house-know-your-numbers",    # Awareness
    "sys-house-antibiotics",          # Safety (photo-free)
    "sys-house-hay-fever",            # Seasonal
    "sys-house-team-wave",            # General
]

#: Brand references a visitor has none of. Left in, they would request an
#: authenticated route and draw nothing — dropped so the snapshot is honest.
BRAND_SOURCES = {"/api/brand-kit/booking-qr", "/api/brand-kit/logo"}

#: Largest pixel width kept for a photograph, as a fraction of the width it
#: occupies on the 1240px canvas. The hero is shown about 420 CSS px wide —
#: a third of the canvas — so 0.7 covers a 2x screen with room to spare; the
#: showcase cards are about 200 px wide, so 0.4 does the same for them.
HERO_SCALE = 0.7
CARD_SCALE = 0.4

CANVAS_W, CANVAS_H = 1240, 1754


def web_copy(src: Path, slot_width: float, name: str, scale: float) -> str:
    """Resize and re-encode one photo; returns its public path."""
    OUT_IMG.mkdir(parents=True, exist_ok=True)
    img = Image.open(src)
    img.load()
    target = max(160, round(slot_width * scale))
    if img.width > target:
        img = img.resize((target, round(img.height * target / img.width)), Image.Resampling.LANCZOS)
    out = OUT_IMG / f"{name}.webp"
    img.save(out, "WEBP", quality=80, method=6)
    return f"{PUBLIC_PREFIX}/{out.name}"


def snapshot(template: dict, scale: float) -> dict:
    tid = template["id"]
    short = tid.removeprefix("sys-house-")
    shapes = []
    n = 0
    for shape in template["shapes"]:
        if shape["kind"] == "image":
            src = shape.get("blobKey", "")
            if src in BRAND_SOURCES:
                continue
            if src.startswith("/api/assets/templates/"):
                file = src.rsplit("/", 1)[-1]
                n += 1
                shape = {**shape, "blobKey": web_copy(SEED / "assets" / file, shape["width"],
                                                        short if n == 1 else f"{short}-{n}", scale)}
        shapes.append(shape)

    return {
        "id": tid,
        "name": template["name"],
        "category": template["category"],
        "canvas": {
            "schemaVersion": 1,
            "id": tid,
            "ownerId": "system",
            "name": template["name"],
            "width": CANVAS_W,
            "height": CANVAS_H,
            "background": template["background"],
            "viewport": {"pan": {"x": 0, "y": 0}, "zoom": 1},
            "shapes": shapes,
            "createdAtUtc": template["createdAt"] + "T00:00:00Z",
            "updatedAtUtc": template["createdAt"] + "T00:00:00Z",
        },
    }


def compact(value: dict) -> str:
    # One line per template: the wave footers are a hundred points each, and
    # indented they would make this file several times its size for nothing.
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


def main() -> int:
    seed = {t["id"]: t for t in json.loads((SEED / "templates.json").read_text("utf-8"))}
    wanted = [HERO, *SHOWCASE]
    missing = [t for t in wanted if t not in seed]
    if missing:
        print(f"Not in the house set: {missing}", file=sys.stderr)
        return 1

    if OUT_IMG.exists():
        for old in OUT_IMG.glob("*.webp"):
            old.unlink()

    hero = snapshot(seed[HERO], HERO_SCALE)
    showcase = [snapshot(seed[t], CARD_SCALE) for t in SHOWCASE]

    body = (
        "// GENERATED by scripts/marketing/build-house-previews.py — do not edit by hand.\n"
        "// Snapshots of real house templates for the marketing site; see that script.\n"
        "// house-previews.test.ts fails if these drift from the seeded templates.\n\n"
        'import type { CanvasDocument, TemplateCategory } from "@acme/shared-types";\n\n'
        "export interface HousePreview {\n"
        "  id: string;\n"
        "  name: string;\n"
        "  category: TemplateCategory;\n"
        "  canvas: CanvasDocument;\n"
        "}\n\n"
        f"export const HERO_PREVIEW: HousePreview = {compact(hero)};\n\n"
        "export const SHOWCASE_PREVIEWS: HousePreview[] = [\n"
        + "".join(f"  {compact(p)},\n" for p in showcase)
        + "];\n"
    )
    OUT_TS.write_text(body, "utf-8")

    total = sum(p.stat().st_size for p in OUT_IMG.glob("*.webp"))
    print(f"Wrote {OUT_TS.relative_to(ROOT)} and {len(list(OUT_IMG.glob('*.webp')))} images "
          f"({total // 1024} KB) to {OUT_IMG.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

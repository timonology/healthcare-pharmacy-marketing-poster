"""Builds the photographs for the house-style system templates.

Run by a person when a set is added or a photo is changed:

    docker compose run --rm --no-deps -v "$PWD:/repo" -w /repo/apps/ai-service \\
        ai-service python -m app.tools.house_template_assets

Photos already built are left alone (``--force`` rebuilds them): Pexels may
re-encode its files, and rewriting a committed photo would change bytes that
production has already stored, for no visible difference.

It fetches the vetted Pexels photos listed in ``SPECS`` through the Pexels API (the same
``PEXELS_API_KEY`` the video pipeline uses), bakes each one — a circle crop, or
a rectangle with its fades — and writes the results into the API project, where
they are embedded and uploaded to template storage by the seeder. The files are
committed: a fresh environment must not need a Pexels key, or the internet, to
get its built-in templates.

**Why the photo is baked rather than masked by the canvas.** The canvas schema
has no clipping and no gradients, and every renderer (editor, PDF, gallery
preview) draws an image as a plain rectangle. A circle or a fade described in
the canvas would render in one place and not another. Baked into the file, it
is the same pixels everywhere.

- Circles are **transparent PNGs**. A circle cut from a JPEG has square corners
  in some background colour — wrong the moment a pharmacy recolours the page,
  and they hide the ring drawn behind the photo.
- Fades are baked **into the JPEG**, towards the exact colour of what sits
  beside the photo on the poster (named in each spec).

**Why these photos.** Each was checked by eye at full size: no logos on
uniforms, no legible text, no medicine packaging (so no drug brands), and a
plain or clinical background. Changing one means checking the new one the same
way — the IDs below are the record of that check, not a search to be re-run.

Licence: the Pexels License (free for commercial use, modification allowed, no
attribution required). The photographers are credited in CREDITS.md anyway.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image, ImageDraw

#: Where the API embeds template assets from. Relative to the repo root.
ASSET_DIR = Path("apps/api/src/Acme.Infrastructure/Persistence/HouseTemplates/assets")

#: Page colours the fades blend into. Must match `house_templates.py`.
WHITE = "#ffffff"
SKY = "#e8f5fb"
NAVY = "#0b2e59"


@dataclass(frozen=True)
class Fade:
    """A baked edge fade: ``side`` of the photo dissolves into ``color``.

    ``size`` is the fraction of that dimension the fade covers.
    """

    side: str  # left | right | top | bottom
    size: float
    color: str


@dataclass(frozen=True)
class AssetSpec:
    """One photograph as a template uses it.

    ``box`` is the crop in the original photo, as fractions (x0, y0, x1, y1),
    so it does not depend on the resolution Pexels serves. Its aspect must
    match ``out`` — the canvas draws the file into exactly that shape, and a
    mismatch is a stretched face.
    """

    name: str
    file_stem: str  # fixed 32-hex name; the canvases reference it
    pexels_id: int
    photographer: str
    source_size: tuple[int, int]  # the original's pixels, as Pexels reports them
    box: tuple[float, float, float, float]
    out: tuple[int, int]
    circle: bool = False
    #: A rectangle with a rounded top — a half-circle as wide as the photo.
    arch: bool = False
    fades: tuple[Fade, ...] = field(default_factory=tuple)

    @property
    def masked(self) -> bool:
        """Shaped by transparency, so stored as PNG."""
        return self.circle or self.arch

    @property
    def extension(self) -> str:
        return "png" if self.masked else "jpg"

    @property
    def file_name(self) -> str:
        return f"{self.file_stem}.{self.extension}"


SPECS: tuple[AssetSpec, ...] = (
    AssetSpec(
        name="trusted-circle", file_stem="2ee6bf0eb8a1419195fdadecfe1ec251",
        pexels_id=6749773, photographer="Antoni Shkraba", source_size=(4000, 6000),
        box=(0.07, 0.03, 0.87, 0.5633), out=(900, 900), circle=True,
    ),
    AssetSpec(
        name="diagonal-care", file_stem="adcdbb974e8d4dc2af3784d6a02b759c",
        pexels_id=7579831, photographer="cottonbro studio", source_size=(6483, 4322),
        box=(0.05, 0.0, 0.9391, 1.0), out=(1240, 930),
    ),
    AssetSpec(
        name="team-wave", file_stem="1ef5772ef0474419a4cb0862f863f18a",
        pexels_id=14438786, photographer="Tran Nhu Tuan", source_size=(3147, 4721),
        box=(0.0, 0.08, 1.0, 0.6176), out=(1240, 1000),
        # The studio backdrop is a pale grey; the page is white. Fading the
        # edges lets the photo sit on the page instead of as a grey box.
        fades=(Fade("left", 0.20, WHITE), Fade("right", 0.20, WHITE), Fade("top", 0.22, WHITE)),
    ),
    AssetSpec(
        name="advice-checklist", file_stem="59db9911dce14673b17c542d1c9a3f1a",
        pexels_id=5215024, photographer="Antoni Shkraba", source_size=(4000, 6000),
        box=(0.18, 0.0, 0.98, 1.0), out=(640, 1200),
        fades=(Fade("left", 0.35, SKY),),
    ),
    AssetSpec(
        name="book-with-us", file_stem="38756f3e394d4ed3995c6f163fe07b66",
        pexels_id=7578806, photographer="cottonbro studio", source_size=(4268, 6402),
        box=(0.0, 0.05, 1.0, 0.5666), out=(1240, 960),
        # Deep enough that the white headline laid over the foot of the photo
        # sits on at least ~90% navy, not on the pharmacist's white coat.
        fades=(Fade("bottom", 0.38, NAVY),),
    ),
    AssetSpec(
        name="local-pharmacy", file_stem="278b8e7e195b410d80332f157ef67b7a",
        pexels_id=18828741, photographer="Tessy Agbonome", source_size=(6000, 4000),
        box=(0.1733, 0.0, 0.84, 1.0), out=(800, 800), circle=True,
    ),

    # ── second set: awareness, vaccination, travel, weight ──
    AssetSpec(
        # Plain grey cuff (7108334 was rejected: printed markings on its cuff).
        # The wall chart is unreadable at this size.
        name="know-your-numbers", file_stem="f08a7720d92f4d64a8858b552ee4fb3e",
        pexels_id=39192345, photographer="Vitaly Gariev", source_size=(3840, 2160),
        box=(0.0, 0.0, 0.9966, 1.0), out=(1240, 700),
        fades=(Fade("bottom", 0.22, WHITE),),
    ),
    AssetSpec(
        # Lancet and fingertip only: the meter below carries a model label.
        name="diabetes-risk", file_stem="879b335cf2ec4f48b91b1e7cfcc4fb1b",
        pexels_id=6823410, photographer="Artem Podrez", source_size=(2086, 3709),
        box=(0.0, 0.10, 1.0, 0.6624), out=(760, 760), circle=True,
    ),
    AssetSpec(
        name="flu-vaccination", file_stem="2725457bc3714911bea5b9a132fa82a5",
        pexels_id=6285397, photographer="Gustavo Fring", source_size=(3840, 5760),
        box=(0.03, 0.0, 0.9633, 1.0), out=(560, 900), arch=True,
    ),
    AssetSpec(
        name="winter-vaccines", file_stem="84a48379d76046eda50738717ce63e31",
        pexels_id=8830495, photographer="Nataliya Vaitkevich", source_size=(4160, 6240),
        box=(0.0, 0.03, 1.0, 0.6967), out=(800, 800), circle=True,
    ),
    AssetSpec(
        # Plain wall cropped off the top. The suitcase stands on the photo's
        # bottom edge, so the template lays nothing over the photo's foot.
        name="travel-clinic", file_stem="5ea0ff4bf7fe431989b0bdba9ab70251",
        pexels_id=7009459, photographer="Tima Miroshnichenko", source_size=(3784, 2523),
        box=(0.0, 0.108, 1.0, 1.0), out=(1240, 738),
    ),
    AssetSpec(
        # Cropped short of a street sign with text on the right.
        name="weight-support", file_stem="1d21637a07ba417ba6016c9b87458cd1",
        pexels_id=8498153, photographer="Liliana Drew", source_size=(6000, 4000),
        box=(0.04, 0.05, 0.745, 0.727), out=(1000, 640),
    ),

    # ── third set: one or two per category ──
    AssetSpec(
        name="shingles-vaccination", file_stem="e56eff28fa75463191beaee616ad0250",
        pexels_id=4975622, photographer="Gustavo Fring", source_size=(5760, 3840),
        box=(0.32, 0.0, 0.92, 0.9), out=(760, 760), circle=True,
    ),
    AssetSpec(
        name="pneumonia-vaccination", file_stem="348f62847e1c4feba367bc1f6bc3aca1",
        pexels_id=39192354, photographer="Vitaly Gariev", source_size=(3840, 2160),
        box=(0.0, 0.0, 0.9966, 1.0), out=(1240, 700),
        fades=(Fade("bottom", 0.25, NAVY),),
    ),
    AssetSpec(
        # Chosen over the "quit smoking" results, which were all lit cigarettes:
        # a quit poster should not show the thing being quit.
        name="stop-smoking", file_stem="fa9e9520941b45afab6c69a4c29eb14f",
        pexels_id=38412532, photographer="Eyüp Kurt", source_size=(3000, 4000),
        box=(0.15, 0.0, 0.95, 0.9), out=(720, 1080),
        # Left and bottom both dissolve into the page: the sky is a strong
        # blue, and a hard foot reads as a block cut off mid-poster.
        fades=(Fade("left", 0.30, WHITE), Fade("bottom", 0.20, WHITE)),
    ),
    AssetSpec(
        # The clinician, not a patient: a patient photo would imply that real
        # person uses contraception. (7108398 was rejected: her face is out of
        # focus.)
        name="contraception", file_stem="c4704785d85f4cf081d10519444f8d1c",
        pexels_id=8376333, photographer="Tima Miroshnichenko", source_size=(4000, 6000),
        box=(0.075, 0.12, 0.925, 0.6867), out=(700, 700), circle=True,
    ),
    AssetSpec(
        name="pharmacy-first", file_stem="e63084fc783f4cae8c8f3b514e075a59",
        pexels_id=7699307, photographer="MART PRODUCTION", source_size=(3632, 5448),
        box=(0.02, 0.0, 0.9674, 1.0), out=(480, 760), arch=True,
    ),
    AssetSpec(
        # Plain envelopes, no courier branding (4440778 was rejected: a clothing
        # logo and a shipping label).
        name="prescription-delivery", file_stem="560aae8f80b24c0a8042e9d8cb8b023f",
        pexels_id=6868626, photographer="Kindel Media", source_size=(4566, 2894),
        box=(0.0, 0.0, 1.0, 1.0), out=(1240, 786),
    ),
    AssetSpec(
        name="hay-fever", file_stem="dc86782ea5754899a0424cd28548891a",
        pexels_id=24818931, photographer="Christina Demirözoğul", source_size=(3872, 2592),
        box=(0.0, 0.0, 1.0, 1.0), out=(1240, 830),
    ),
    AssetSpec(
        name="repeat-prescriptions", file_stem="7909da6966bf444e995a87d6323a05d7",
        pexels_id=8087568, photographer="Yaroslav Shuraev", source_size=(4480, 6720),
        box=(0.0, 0.1, 1.0, 0.7667), out=(700, 700), circle=True,
    ),
)


# ── baking ────────────────────────────────────────────────────────────────


def crop_aspect_error(spec: AssetSpec) -> float:
    """How far the crop's shape is from the output's, as a fraction."""
    sw, sh = spec.source_size
    x0, y0, x1, y1 = spec.box
    crop = ((x1 - x0) * sw) / ((y1 - y0) * sh)
    target = spec.out[0] / spec.out[1]
    return abs(crop - target) / target


def _rgb(hex_color: str) -> tuple[int, int, int]:
    h = hex_color.lstrip("#")
    return int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)


def _smooth(t: float) -> float:
    """Smoothstep: a fade with no visible edge where it starts or ends."""
    t = min(1.0, max(0.0, t))
    return t * t * (3 - 2 * t)


def apply_fade(img: Image.Image, fade: Fade) -> Image.Image:
    """Blend one edge of ``img`` into a solid colour — fully at the edge."""
    w, h = img.size
    horizontal = fade.side in ("left", "right")
    span = max(1, round((w if horizontal else h) * fade.size))

    # A 1-pixel-thick ramp stretched across the image: alpha is how much of the
    # solid colour shows, 255 at the edge falling to 0 at the end of the fade.
    ramp = Image.new("L", (span, 1) if horizontal else (1, span))
    for i in range(span):
        a = round(255 * (1 - _smooth(i / span)))
        ramp.putpixel((i, 0) if horizontal else (0, i), a)
    if fade.side in ("right", "bottom"):
        ramp = ramp.transpose(
            Image.Transpose.FLIP_LEFT_RIGHT if horizontal else Image.Transpose.FLIP_TOP_BOTTOM)
    ramp = ramp.resize((span, h) if horizontal else (w, span))

    mask = Image.new("L", (w, h), 0)
    x = w - span if fade.side == "right" else 0
    y = h - span if fade.side == "bottom" else 0
    mask.paste(ramp, (x, y))

    solid = Image.new("RGB", (w, h), _rgb(fade.color))
    return Image.composite(solid, img.convert("RGB"), mask)


def circle_mask(size: int, supersample: int = 4) -> Image.Image:
    """An anti-aliased disc: drawn large, then scaled down."""
    big = Image.new("L", (size * supersample, size * supersample), 0)
    ImageDraw.Draw(big).ellipse((0, 0, size * supersample - 1, size * supersample - 1), fill=255)
    return big.resize((size, size), Image.Resampling.LANCZOS)


def arch_mask(size: tuple[int, int], supersample: int = 4) -> Image.Image:
    """A rectangle whose top is a half-circle the full width: anti-aliased."""
    w, h = size[0] * supersample, size[1] * supersample
    big = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(big)
    d.ellipse((0, 0, w - 1, w - 1), fill=255)
    d.rectangle((0, w // 2, w - 1, h - 1), fill=255)
    return big.resize(size, Image.Resampling.LANCZOS)


def bake(original: Image.Image, spec: AssetSpec) -> Image.Image:
    """Crop, size and finish one photo exactly as the template draws it."""
    sw, sh = original.size
    x0, y0, x1, y1 = spec.box
    cropped = original.convert("RGB").crop(
        (round(x0 * sw), round(y0 * sh), round(x1 * sw), round(y1 * sh)))
    sized = cropped.resize(spec.out, Image.Resampling.LANCZOS)

    for fade in spec.fades:
        sized = apply_fade(sized, fade)

    if spec.circle:
        if spec.out[0] != spec.out[1]:
            raise ValueError(f"{spec.name}: a circle needs a square output")
        rgba = sized.convert("RGBA")
        rgba.putalpha(circle_mask(spec.out[0]))
        return rgba
    if spec.arch:
        if spec.out[1] < spec.out[0] / 2:
            raise ValueError(f"{spec.name}: an arch must be at least half as tall as it is wide")
        rgba = sized.convert("RGBA")
        rgba.putalpha(arch_mask(spec.out))
        return rgba
    return sized


def encode(img: Image.Image, spec: AssetSpec) -> bytes:
    buf = io.BytesIO()
    if spec.masked:
        img.save(buf, "PNG", optimize=True)
    else:
        img.save(buf, "JPEG", quality=86, optimize=True, progressive=True)
    return buf.getvalue()


# ── fetching ──────────────────────────────────────────────────────────────


#: Pexels' image service, asked for what the crops were checked against: sRGB
#: (`cs=tinysrgb`), rotated upright, 2600px wide — ample for every output.
RENDITION = "?auto=compress&cs=tinysrgb&fm=jpg&w=2600"


def fetch_original(pexels_id: int, api_key: str) -> tuple[Image.Image, dict]:
    """The photo, through the API — never a scraped URL — as a browser shows it."""
    import requests

    meta = requests.get(
        f"https://api.pexels.com/v1/photos/{pexels_id}",
        headers={"Authorization": api_key}, timeout=30)
    meta.raise_for_status()
    info = meta.json()
    raw = requests.get(info["src"]["original"].split("?")[0] + RENDITION, timeout=120)
    raw.raise_for_status()
    return normalise(Image.open(io.BytesIO(raw.content))), info


def normalise(img: Image.Image) -> Image.Image:
    """Upright and sRGB — the picture the crop boxes were measured on.

    The boxes were chosen looking at the photo in a browser, which applies the
    EXIF rotation and the embedded colour profile. Pillow does neither by
    itself, so an original shot in portrait and stored sideways would be
    cropped across the wrong axis, and a wide-gamut one would print with shifted
    colours. The rendition asked for above is already both; this makes sure.
    """
    from PIL import ImageCms, ImageOps

    img = ImageOps.exif_transpose(img)
    icc = img.info.get("icc_profile")
    if icc:
        try:
            src = ImageCms.ImageCmsProfile(io.BytesIO(icc))
            img = ImageCms.profileToProfile(img, src, ImageCms.createProfile("sRGB"), outputMode="RGB")
        except (ImageCms.PyCMSError, OSError):
            pass  # an unreadable profile: keep the pixels as they are
    return img.convert("RGB")


def same_shape(size: tuple[int, int], expected: tuple[int, int]) -> bool:
    """Whether a photo has the proportions of the one that was vetted."""
    return abs(size[0] / size[1] - expected[0] / expected[1]) <= 0.01 * expected[0] / expected[1]


def credits_markdown(rows: list[dict]) -> str:
    lines = [
        "# House-style template photographs",
        "",
        "Generated by `apps/ai-service/app/tools/house_template_assets.py` — do not edit by hand.",
        "",
        "All from Pexels under the [Pexels License](https://www.pexels.com/license/): free for",
        "commercial use and modification, attribution not required. Credited anyway.",
        "",
        "| File | Used by | Photographer | Source |",
        "|---|---|---|---|",
    ]
    for r in rows:
        lines.append(
            f"| `{r['file']}` | {r['name']} | {r['photographer']} | {r['url']} |")
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--repo", default=str(Path(__file__).resolve().parents[4]),
                        help="repository root (default: inferred)")
    parser.add_argument("--only", nargs="*", help="asset names to build")
    parser.add_argument("--force", action="store_true", help="rebuild photos that already exist")
    args = parser.parse_args(argv)

    key = os.getenv("PEXELS_API_KEY", "").strip()
    if not key:
        print("PEXELS_API_KEY is not set — run this in the ai-service container.", file=sys.stderr)
        return 2

    out_dir = Path(args.repo) / ASSET_DIR
    out_dir.mkdir(parents=True, exist_ok=True)

    for spec in SPECS:
        if crop_aspect_error(spec) > 0.01:
            print(f"{spec.name}: crop and output shapes disagree — fix the spec", file=sys.stderr)
            return 1

    for spec in SPECS:
        if args.only and spec.name not in args.only:
            continue
        target = out_dir / spec.file_name
        if target.exists() and not args.force:
            print(f"{spec.name}: {spec.file_name} already built — skipped (--force to rebuild)")
            continue
        original, _info = fetch_original(spec.pexels_id, key)
        if not same_shape(original.size, spec.source_size):
            # The crop is in fractions, so any size of the same picture frames
            # it the same way. Different proportions mean a different photo, or
            # one on its side — either way the crop would be wrong.
            print(f"{spec.name}: Pexels served {original.size}, the vetted photo is "
                  f"{spec.source_size}. Not written — check the photo.", file=sys.stderr)
            return 1
        data = encode(bake(original, spec), spec)
        target.write_bytes(data)
        print(f"{spec.name}: {spec.file_name} ({len(data) // 1024} KB)")

    # From the full list every time, not from what this run fetched: a skipped
    # or --only run must not drop anybody's credit.
    (out_dir.parent / "CREDITS.md").write_text(credits_markdown(credit_rows()))
    (out_dir.parent / "assets.json").write_text(json.dumps(
        [{"name": s.name, "file": s.file_name, "pexelsId": s.pexels_id} for s in SPECS],
        indent=2) + "\n")
    return 0


def credit_rows() -> list[dict]:
    """Every photo in SPECS, with its photographer and Pexels page."""
    return [{"file": s.file_name, "name": s.name, "photographer": s.photographer,
             "url": f"https://www.pexels.com/photo/{s.pexels_id}/"} for s in SPECS]


if __name__ == "__main__":
    raise SystemExit(main())

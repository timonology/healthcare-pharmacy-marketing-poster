"""Framing a pharmacist's photograph: pan, zoom out, punch in.

The promise the dialog makes is that the framed shot is the render. That rests on
two sides doing identical sums — `framing.py` here and `framing.ts` in the
browser — and on the zoom-1.0 path being exactly what every photo got before
zoom existed. Most of this file is about those two properties.

The tests that run ffmpeg are skipped where it is not installed; everything else
is pure arithmetic and runs anywhere.
"""

import json
import shutil
import subprocess
from pathlib import Path

import pytest

from app.video_agent.framing import (
    ZOOM_MAX,
    ZOOM_MIN,
    coord,
    ffmpeg_args,
    geometry,
    zoom_of,
)

FRAME = (1080, 1920)
REPO = Path(__file__).resolve().parents[3]
needs_ffmpeg = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")


def _legacy_offset(image_w: int, image_h: int, focal: float) -> float:
    """The horizontal crop offset the pre-zoom filter produced, as ffmpeg sums it."""
    scale = max(FRAME[0] / image_w, FRAME[1] / image_h)
    return (image_w * scale - FRAME[0]) * focal


# ── the tests asked for ─────────────────────────────────────────────────

def test_zoom_one_is_exactly_the_old_crop_filter() -> None:
    """Not "close to" the old crop — the same filter, byte for byte.

    Every photo attached before zoom existed, and every photo nobody zooms,
    goes down this path. Rebuilding it from the new geometry would move some of
    them by a pixel; keeping the literal string means none of them move at all.
    """
    old = [
        "-vf",
        "scale=1080:1920:force_original_aspect_ratio=increase,"
        "crop=1080:1920:(in_w-1080)*0.3000:(in_h-1920)*0.5000",
    ]

    assert ffmpeg_args(4032, 3024, *FRAME, 0.3, 0.5, 1.0) == old
    # And a slider left a whisker off 100% still means the untouched crop.
    assert ffmpeg_args(4032, 3024, *FRAME, 0.3, 0.5, 1.004) == old
    assert ffmpeg_args(4032, 3024, *FRAME, 0.3, 0.5, 0.996) == old


def test_zoom_one_geometry_agrees_with_the_old_crop_to_the_pixel() -> None:
    # The preview draws from `geometry`; the render at 1.0 uses the old filter.
    # They are different code, so they are checked against each other.
    for w, h in ((4032, 3024), (1600, 600), (1080, 1080), (641, 479)):
        for focal in (0.0, 0.25, 0.5, 0.75, 1.0):
            g = geometry(w, h, *FRAME, focal, 0.5, 1.0)
            assert abs(-g.x - _legacy_offset(w, h, focal)) <= 1.0, (w, h, focal)


def test_zoom_point_six_shows_more_of_a_wide_image() -> None:
    wide = (1600, 600)

    at_one = geometry(*wide, *FRAME, 0.5, 0.5, 1.0)
    at_point_six = geometry(*wide, *FRAME, 0.5, 0.5, 0.6)

    visible = lambda g: min(FRAME[0], g.width) / g.width  # noqa: E731
    assert visible(at_point_six) > visible(at_one)
    assert visible(at_one) == pytest.approx(0.21, abs=0.01)
    assert visible(at_point_six) == pytest.approx(0.35, abs=0.01)


def test_absent_zoom_and_focal_mean_centre_and_one() -> None:
    """Every row written before zoom existed has neither field."""
    assert zoom_of(None) == 1.0
    assert coord(None) == 0.5
    assert ffmpeg_args(4032, 3024, *FRAME, coord(None), coord(None), zoom_of(None)) == [
        "-vf",
        "scale=1080:1920:force_original_aspect_ratio=increase,"
        "crop=1080:1920:(in_w-1080)*0.5000:(in_h-1920)*0.5000",
    ]


def test_the_preview_reproduces_the_same_golden_table() -> None:
    """The browser's half of "the framed shot is the render".

    `framing.ts` is held to this table by `scripts/check-framing.mjs`; this
    holds `framing.py` to the same one. Two languages, one set of answers.
    """
    fixture = json.loads(
        (REPO / "packages/shared-types/fixtures/framing-geometry.json").read_text())

    for row in fixture["rows"]:
        g = geometry(*row["in"])
        assert [g.width, g.height, g.x, g.y] == row["out"], row["in"]


# ── bounds and bad values ───────────────────────────────────────────────

def test_zoom_is_clamped_to_its_bounds() -> None:
    assert zoom_of(0.1) == ZOOM_MIN
    assert zoom_of(9) == ZOOM_MAX
    assert zoom_of(-1) == ZOOM_MIN


def test_an_unreadable_zoom_is_one_rather_than_an_error() -> None:
    # A framing preference gone wrong is never a reason to drop the picture.
    for value in ("wide", float("nan"), float("inf"), [], {}, True):
        assert zoom_of(value) == 1.0, value


def test_a_focal_point_is_clamped_and_never_raises() -> None:
    assert coord(1.7) == 1.0
    assert coord(-3) == 0.0
    for value in ("left", float("nan"), None, True):
        assert coord(value) == 0.5, value


# ── which way the photo can move ────────────────────────────────────────

def test_landscape_pans_across_but_not_down_until_it_punches_in() -> None:
    landscape = (4032, 3024)

    for zoom in (0.5, 0.75, 1.0):
        g = geometry(*landscape, *FRAME, 0.5, 0.5, zoom)
        assert g.pans_across(FRAME[0]), zoom
        assert not g.pans_down(FRAME[1]), zoom

    # Past 1.0 there is spare height too, so both axes have somewhere to go.
    g = geometry(*landscape, *FRAME, 0.5, 0.5, 1.3)
    assert g.pans_across(FRAME[0]) and g.pans_down(FRAME[1])


def test_a_photo_taller_than_the_frame_pans_down_but_not_across() -> None:
    # "Portrait" here means taller than 9:16. An ordinary 3:4 phone portrait is
    # *wider* than 9:16 and so pans across like a landscape — the rule is always
    # "the axis with slack", never the orientation the camera reported.
    tall = (1000, 2400)

    for zoom in (0.75, 1.0):
        g = geometry(*tall, *FRAME, 0.5, 0.5, zoom)
        assert g.pans_down(FRAME[1]), zoom
        assert not g.pans_across(FRAME[0]), zoom


def test_a_phone_portrait_pans_across_because_it_is_wider_than_the_frame() -> None:
    g = geometry(3024, 4032, *FRAME, 0.5, 0.5, 1.0)

    assert g.pans_across(FRAME[0])
    assert not g.pans_down(FRAME[1])


def test_zoomed_far_enough_out_nothing_pans_at_all() -> None:
    """A drag on an axis with no slack does nothing — including both axes.

    A photo close to 9:16 at half size fits inside the frame completely, with
    blur on every side. There is nothing to move through in either direction,
    so neither drag nor slider may be offered.
    """
    g = geometry(1200, 2400, *FRAME, 0.5, 0.5, 0.5)

    assert not g.pans_across(FRAME[0])
    assert not g.pans_down(FRAME[1])
    assert g.has_bands(*FRAME)


def test_a_photo_smaller_than_the_frame_is_centred_whatever_the_focal_point() -> None:
    # No slack on that axis, so a focal point there has nothing to move.
    left = geometry(1600, 600, *FRAME, 0.0, 0.0, 0.6)
    right = geometry(1600, 600, *FRAME, 1.0, 1.0, 0.6)

    assert left.y == right.y == (FRAME[1] - left.height) // 2


def test_every_size_is_even_because_the_encoder_needs_it() -> None:
    for w, h in ((641, 479), (7, 3), (1, 1), (4032, 3024)):
        for zoom in (0.5, 0.73, 1.0, 1.37, 1.5):
            g = geometry(w, h, *FRAME, 0.5, 0.5, zoom)
            assert g.width % 2 == 0 and g.height % 2 == 0, (w, h, zoom)


def test_below_one_the_filter_fills_the_bands_rather_than_leaving_black() -> None:
    args = ffmpeg_args(1600, 600, *FRAME, 0.5, 0.5, 0.6)

    assert args[0] == "-filter_complex"
    assert "boxblur" in args[1], "the gaps must be a blurred copy of the photo"
    assert "pad=" not in args[1], "padding would leave solid bars"


def test_punching_in_is_a_plain_crop_with_no_blur() -> None:
    args = ffmpeg_args(4032, 3024, *FRAME, 0.5, 0.5, 1.3)

    assert args[0] == "-vf"
    assert "boxblur" not in args[1]


def test_an_unknown_image_size_falls_back_to_the_old_crop() -> None:
    # Pillow could not read the header. Zoom needs the size; the old crop does
    # not — so the photo is still used, just without the zoom.
    args = ffmpeg_args(None, None, *FRAME, 0.5, 0.5, 0.6)

    assert args[0] == "-vf" and "force_original_aspect_ratio=increase" in args[1]


# ── the render, for real ────────────────────────────────────────────────

def _striped(path: Path) -> None:
    """A wide image with a solid red band along its top 40 rows."""
    from PIL import Image, ImageDraw

    im = Image.new("RGB", (1600, 600), (20, 120, 140))
    ImageDraw.Draw(im).rectangle([0, 0, 1600, 39], fill=(220, 20, 20))
    im.save(path)


def _render(src: Path, out: Path, zoom: float) -> None:
    args = ffmpeg_args(1600, 600, *FRAME, 0.5, 0.5, zoom)
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", str(src), *args, "-frames:v", "1", str(out)],
        check=True,
    )


@needs_ffmpeg
def test_the_rendered_photo_sits_exactly_where_the_geometry_puts_it(tmp_path: Path) -> None:
    """The render's half of "the framed shot is the render", measured in pixels.

    The red band is the top of the photo. Where it appears in the output frame
    is where ffmpeg placed the photo, and it has to be the row the geometry —
    and therefore the preview — said.
    """
    from PIL import Image

    src, out = tmp_path / "wide.png", tmp_path / "out.png"
    _striped(src)
    _render(src, out, 0.6)

    g = geometry(1600, 600, *FRAME, 0.5, 0.5, 0.6)
    frame = Image.open(out).convert("RGB")

    # Scanned from a little above the expected edge, not from the top. The
    # background is this same photo, cover-cropped and blurred, so its own copy
    # of the red band is smeared across the top of the frame — the first
    # version of this test found *that* and reported the photo at row 0.
    start = max(0, g.y - 100)
    column = [frame.getpixel((540, y)) for y in range(start, frame.height)]
    first_red = start + next(i for i, (r, gr, b) in enumerate(column) if r > 180 and gr < 80)

    assert frame.size == FRAME
    assert abs(first_red - g.y) <= 1, (first_red, g.y)


@needs_ffmpeg
def test_the_bands_are_the_photo_blurred_not_black(tmp_path: Path) -> None:
    from PIL import Image

    src, out = tmp_path / "wide.png", tmp_path / "out.png"
    _striped(src)
    _render(src, out, 0.6)

    top_band = Image.open(out).convert("RGB").getpixel((540, 5))

    assert sum(top_band) > 60, f"expected the blurred photo, got near-black {top_band}"

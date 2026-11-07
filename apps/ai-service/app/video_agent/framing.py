"""Where a pharmacist's photograph sits in the 9:16 frame.

Three numbers decide it — `focalX`, `focalY` and `zoom` — and this module is the
one place that turns them into pixels. The browser preview does the same sums in
`packages/shared-types/src/framing.ts`, and both are pinned to one table of
answers (`packages/shared-types/fixtures/framing-geometry.json`) so that what the
pharmacist framed is what the render produces, rather than something close to it.

<b>Zoom 1.0 is the crop every picture has always had.</b> The photo is scaled to
*cover* the frame and the spare width or height is cut, positioned by the focal
point. Nothing about that path changes: `ffmpeg_args` hands back the existing
filter string byte for byte, so no video already rendered, and no photo attached
without touching the zoom, comes out differently.

<b>Below 1.0 the photo is scaled down so more of it shows.</b> The gaps that
opens up are filled with a blurred, cover-cropped copy of the same picture — no
black bars and no stretching, which were the two things asked for. A landscape
phone photo keeps 42% of its width at 1.0 and a good deal more as the zoom drops.

<b>Above 1.0 it punches in.</b> Free to offer — it is the same crop with a
larger scale and no blur — but capped at 1.5, because past that a phone photo
is being enlarged beyond what it holds and simply goes soft.

<b>Rounding is explicit, and identical in both languages.</b> Sizes round to the
nearest even number (the encoder's chroma planes need even dimensions), offsets
round half up. Python's own `round` rounds half to even and JavaScript's rounds
half up, so using either built-in would make the preview and the render disagree
by a pixel on exactly the values a slider lands on.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any

#: The bounds and the default. Mirrored in `framing.ts` and in
#: `VideoImage.ZoomMin/ZoomMax` on the .NET side; `VideoImageTests` reads all
#: three copies so they cannot drift apart.
ZOOM_MIN = 0.5
ZOOM_MAX = 1.5
ZOOM_DEFAULT = 1.0
FOCAL_DEFAULT = 0.5

#: Within this of 1.0 counts as 1.0. A slider dragged back to "100%" lands on
#: 0.999 or 1.001 as often as on 1.0, and that must mean the untouched crop — not
#: a new code path whose output differs from the original by a pixel.
ZOOM_SNAP = 0.005


def _even(value: float) -> int:
    return max(2, 2 * math.floor(value / 2 + 0.5))


def _half_up(value: float) -> int:
    return math.floor(value + 0.5)


def coord(value: Any) -> float:
    """One axis of a focal point: 0–1, centre when absent or unreadable."""
    try:
        if value is None or isinstance(value, bool):
            return FOCAL_DEFAULT
        number = float(value)
    except (TypeError, ValueError):
        return FOCAL_DEFAULT
    if math.isnan(number):
        return FOCAL_DEFAULT
    return min(1.0, max(0.0, number))


def zoom_of(value: Any) -> float:
    """A zoom within bounds, 1.0 when absent or unreadable.

    Never raises. A zoom that came back from the database as a string, or as
    NaN, is a framing preference gone wrong — not a reason to drop the picture
    the pharmacist attached. It falls back to the crop they would have had
    before zoom existed.
    """
    try:
        if value is None or isinstance(value, bool):
            return ZOOM_DEFAULT
        number = float(value)
    except (TypeError, ValueError):
        return ZOOM_DEFAULT
    if math.isnan(number) or math.isinf(number):
        return ZOOM_DEFAULT
    number = min(ZOOM_MAX, max(ZOOM_MIN, number))
    return ZOOM_DEFAULT if abs(number - ZOOM_DEFAULT) < ZOOM_SNAP else number


def is_untouched(zoom: float) -> bool:
    return abs(zoom - ZOOM_DEFAULT) < ZOOM_SNAP


@dataclass(frozen=True)
class Geometry:
    """The photo scaled to `width`×`height`, its top-left at (`x`, `y`) in the frame.

    `x` and `y` are negative when the photo is larger than the frame on that
    axis — the part left of zero is what gets cut. Positive means it is smaller
    and centred, with the blurred fill either side.
    """

    width: int
    height: int
    x: int
    y: int

    def pans_across(self, frame_w: int) -> bool:
        """Whether there is spare width to move through."""
        return self.width > frame_w

    def pans_down(self, frame_h: int) -> bool:
        return self.height > frame_h

    def has_bands(self, frame_w: int, frame_h: int) -> bool:
        """Whether any of the frame is not covered by the photo itself."""
        return self.width < frame_w or self.height < frame_h


def geometry(
    image_w: int, image_h: int, frame_w: int, frame_h: int,
    focal_x: float, focal_y: float, zoom: float,
) -> Geometry:
    """Where the photo lands, for any zoom.

    On an axis where the scaled photo is larger than the frame, the focal point
    decides which part is kept — exactly as before zoom existed. On an axis
    where it is smaller, there is nothing to choose: it is centred, and the
    focal point for that axis is ignored.
    """
    scale = max(frame_w / image_w, frame_h / image_h) * zoom
    width, height = _even(image_w * scale), _even(image_h * scale)

    x = (-_half_up((width - frame_w) * focal_x) if width >= frame_w
         else (frame_w - width) // 2)
    y = (-_half_up((height - frame_h) * focal_y) if height >= frame_h
         else (frame_h - height) // 2)
    return Geometry(width, height, x, y)


def ffmpeg_args(
    image_w: int | None, image_h: int | None, frame_w: int, frame_h: int,
    focal_x: float, focal_y: float, zoom: float,
) -> list[str]:
    """The filter arguments that place the photo as `geometry` says.

    Three cases, and the first is the one that matters most:

    * **Zoom 1.0** — the existing filter, unchanged, byte for byte. It needs no
      image size, which is also why it survives a photo Pillow cannot read.
    * **Covering the frame on both axes** (any zoom ≥ 1.0) — scale and crop,
      same shape as before with the geometry's own numbers.
    * **Leaving bands** (zoom below 1.0) — a blurred copy of the photo, cover-
      cropped to the frame, underneath the scaled photo at the geometry offset.
    """
    fx, fy = coord(focal_x), coord(focal_y)
    z = zoom_of(zoom)

    if is_untouched(z) or not image_w or not image_h:
        # Kept literally, not rebuilt from `geometry`, so this path cannot drift
        # by a pixel from every render made before zoom existed.
        return ["-vf", (
            f"scale={frame_w}:{frame_h}:force_original_aspect_ratio=increase,"
            f"crop={frame_w}:{frame_h}:(in_w-{frame_w})*{fx:.4f}:(in_h-{frame_h})*{fy:.4f}"
        )]

    g = geometry(image_w, image_h, frame_w, frame_h, fx, fy, z)

    if not g.has_bands(frame_w, frame_h):
        return ["-vf", f"scale={g.width}:{g.height},crop={frame_w}:{frame_h}:{-g.x}:{-g.y}"]

    # The blur is sized to the frame, not the photo, so the same zoom looks the
    # same on a small photo and a large one. `boxblur` needs its radius under
    # half the smallest plane; at 1080×1920 the chroma planes are 540×960.
    radius = max(4, min(frame_w, frame_h) // 40)
    graph = (
        f"[0:v]split=2[bgsrc][fgsrc];"
        f"[bgsrc]scale={frame_w}:{frame_h}:force_original_aspect_ratio=increase,"
        f"crop={frame_w}:{frame_h},boxblur={radius}:2[bg];"
        f"[fgsrc]scale={g.width}:{g.height}[fg];"
        f"[bg][fg]overlay={g.x}:{g.y}[framed]"
    )
    return ["-filter_complex", graph, "-map", "[framed]"]

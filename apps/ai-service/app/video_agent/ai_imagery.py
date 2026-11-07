"""
AI image generation via Replicate (Flux Schnell by default).

Ported from Yami Social. Kept nearly verbatim — only the import for
`settings` was rewired to Marketing25's `get_settings()`.
"""
from __future__ import annotations

import logging
import os
import time
from pathlib import Path

import requests

from ..config import get_settings

log = logging.getLogger(__name__)


STYLE_PROMPTS: dict[str, str] = {
    "cartoon": (
        "cartoon illustration, vibrant colors, soft cel shading, "
        "friendly characters, animated style, clean composition, "
        "highly detailed, 2D animation aesthetic"
    ),
    "anime": (
        "anime illustration, cel-shaded, vivid colors, detailed eyes, "
        "studio anime style, dramatic lighting, clean line art"
    ),
    "illustration": (
        "flat illustration, vector art style, modern minimalist, "
        "clean shapes, balanced composition, editorial illustration"
    ),
    "photorealistic": (
        "photorealistic, professional photography, sharp focus, "
        "natural lighting, shallow depth of field, 8k detail"
    ),
    "medical": (
        "clean editorial illustration, healthcare aesthetic, "
        "friendly, approachable, soft colors, professional pharmacy setting"
    ),
}


def is_configured() -> bool:
    if not get_settings().replicate_api_token:
        return False
    try:
        import replicate  # noqa: F401
        return True
    except ImportError:
        return False


class BillingError(Exception):
    """Raised when Replicate returns 402 / quota-exhausted."""


def _build_prompt(keyword: str, style: str, niche: str | None = None) -> str:
    style_token = STYLE_PROMPTS.get(style, STYLE_PROMPTS["cartoon"])
    parts = [keyword.strip(), style_token]
    if niche:
        parts.append(f"theme: {niche.strip()}")
    parts.append("centered composition, no text, no watermark")
    return ", ".join(p for p in parts if p)


def generate_one_image(
    out_dir: Path,
    keyword: str,
    style: str,
    *,
    niche: str | None = None,
    aspect_ratio: str = "9:16",
    timeout_s: int = 30,
    file_stem: str | None = None,
) -> tuple[Path | None, float]:
    s = get_settings()
    if not is_configured():
        return (None, 0.0)
    try:
        import replicate
    except ImportError:
        return (None, 0.0)

    os.environ["REPLICATE_API_TOKEN"] = s.replicate_api_token or ""
    prompt = _build_prompt(keyword, style, niche)
    log.info("AI imagery: generating %r (style=%s, aspect=%s)", keyword, style, aspect_ratio)

    start = time.time()
    try:
        result = replicate.run(
            s.replicate_flux_model,
            input={
                "prompt": prompt,
                "aspect_ratio": aspect_ratio,
                "num_outputs": 1,
                "output_format": "webp",
            },
        )
    except Exception as exc:  # noqa: BLE001
        status = getattr(exc, "status", None)
        detail = str(getattr(exc, "detail", "")) or str(exc)
        if status == 402 or "insufficient credit" in detail.lower() or "add a payment method" in detail.lower():
            raise BillingError(detail) from exc
        log.warning("Replicate call failed (%s): %s", keyword, exc)
        return (None, 0.0)

    if isinstance(result, list):
        if not result:
            return (None, 0.0)
        first = result[0]
    else:
        first = result
    if first is None:
        return (None, 0.0)

    image_url = str(first).strip()
    if not image_url.startswith("http"):
        for attr in ("url", "href"):
            v = getattr(first, attr, None)
            if isinstance(v, str) and v.startswith("http"):
                image_url = v
                break
    if not image_url.startswith("http"):
        return (None, 0.0)

    stem = file_stem or f"ai_{int(time.time()*1000)}"
    out_path = out_dir / f"{stem}.webp"
    try:
        with requests.get(image_url, stream=True, timeout=timeout_s) as r:
            r.raise_for_status()
            with open(out_path, "wb") as f:
                for chunk in r.iter_content(1 << 16):
                    f.write(chunk)
    except Exception as exc:  # noqa: BLE001
        log.warning("Image download failed: %s", exc)
        return (None, 0.0)

    elapsed = time.time() - start
    cost = float(s.ai_imagery_cost_per_image_usd)
    log.info("AI imagery: saved %s (%.1fs, $%.4f)", out_path.name, elapsed, cost)
    return (out_path, cost)


#: EXIF orientation tag. Values 2–8 mean the stored pixels are mirrored or
#: rotated relative to how the camera meant them to be seen.
_EXIF_ORIENTATION = 0x0112


def _upright(image_path: Path, out_dir: Path) -> tuple[Path, tuple[int, int] | None]:
    """The picture as a viewer sees it, and its size.

    A phone held upright usually stores a landscape sensor image plus an EXIF tag
    saying "rotate 90°". Browsers apply that tag, so the framing preview shows
    the photo upright and measures it as portrait. ffmpeg — 4.4 and 5.1, which
    is what the worker image ships — ignores it for JPEGs, so the render
    received the sideways pixels: wrong way up, and cropped on the wrong axis
    against a focal point chosen on the right one.

    So the rotation is applied here, once, into a copy beside the clip. A
    picture with no tag (every AI still, most uploads) is returned as it is,
    unopened beyond its header. Never raises: an unreadable file comes back
    untouched with no size, and ffmpeg gets its own chance to decode it.
    """
    try:
        from PIL import Image, ImageOps

        with Image.open(image_path) as im:
            orientation = im.getexif().get(_EXIF_ORIENTATION, 1)
            if orientation in (None, 1) or not 2 <= int(orientation) <= 8:
                return image_path, im.size

            upright = ImageOps.exif_transpose(im)
            # PNG only where there is transparency to keep. Everything else —
            # including CMYK, which print and design tools export and PNG
            # cannot hold — becomes an RGB JPEG at high quality: a lossless PNG
            # of a 12MP phone picture costs seconds to write and is decoded once.
            if upright.mode in ("RGBA", "LA", "P", "PA"):
                fixed = out_dir / f"{image_path.stem}-upright.png"
                upright.save(fixed, "PNG")
            else:
                if upright.mode not in ("RGB", "L"):
                    upright = upright.convert("RGB")
                fixed = out_dir / f"{image_path.stem}-upright.jpg"
                upright.save(fixed, "JPEG", quality=95)
            log.info("Applied EXIF orientation %s to %s", orientation, image_path.name)
            return fixed, upright.size
    except Exception as exc:  # noqa: BLE001
        log.warning("Could not read %s before framing it (%s); using it as stored.",
                    image_path.name, exc)
        return image_path, None


def _ffmpeg_can_decode(image_path: Path) -> bool:
    """Whether ffmpeg can get one frame out of this file, quickly.

    Only asked about a file Pillow could not open — the case `_upright` hands
    to ffmpeg on purpose, since ffmpeg reads a few formats Pillow does not. But
    the clip encode loops the image (`-loop 1`), and on a file that is not a
    picture at all the loop never produces a frame and never ends: it sat until
    the 60-second timeout on every render and every refine of a video with one
    damaged attachment. One frame, no loop, fails in well under a second.
    """
    import subprocess

    try:
        probe = subprocess.run(
            ["ffmpeg", "-nostdin", "-v", "error", "-i", str(image_path),
             "-frames:v", "1", "-f", "null", "-"],
            capture_output=True, text=True, timeout=15, stdin=subprocess.DEVNULL,
        )
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return False
    return probe.returncode == 0


def image_to_video_clip(
    image_path: Path,
    out_dir: Path,
    duration_s: float,
    target_size: tuple[int, int],
    *,
    fps: int = 30,
    focal: tuple[float, float] = (0.5, 0.5),
    zoom: float = 1.0,
) -> Path | None:
    """Convert a still image into a video clip. Static (no Ken Burns) for
    speed — see Yami's notes on why per-frame resize was 30-70s per clip.

    `focal` is which part of the picture survives the crop, 0–1 in each axis,
    centre by default. It matters most for a photograph the pharmacist supplied:
    a landscape phone picture cropped to 9:16 keeps 42% of its width, so
    *which* 42% is the difference between their shopfront and the wall beside
    it.

    `zoom` below 1.0 shows more of the picture and fills the gaps with a
    blurred copy of it; above 1.0 punches in. At 1.0 — the default, and every
    caller that does not pass it — the filter is the original one, unchanged.
    The sums live in `framing.py`, shared with the browser preview.
    """
    import subprocess

    duration_s = max(1.5, float(duration_s))
    tw, th = target_size
    out_path = out_dir / f"{image_path.stem}.mp4"

    # Clamped here as well as in .NET. This function is called directly by the
    # AI-imagery path, which passes no focal point at all, and by tests — a
    # guard at one end of a two-language boundary is not a guard.
    from . import framing

    fx = framing.coord(focal[0] if focal else None)
    fy = framing.coord(focal[1] if focal else None)
    z = framing.zoom_of(zoom)

    # Upright first, then measured. See `_upright`: the browser honours a phone
    # photo's EXIF rotation and ffmpeg does not, so without this the two sides
    # would be framing different pictures. A file Pillow cannot read gives no
    # size, and `ffmpeg_args` falls back to the original crop — the photo is
    # still used, just without the zoom.
    image_path, size = _upright(image_path, out_dir)
    if size is None and not _ffmpeg_can_decode(image_path):
        # Same outcome the timeout used to reach a minute later: no clip, and
        # the caller records the picture as unusable.
        log.warning("%s is not a picture ffmpeg can read either; skipping it.", image_path.name)
        return None

    filter_args = framing.ffmpeg_args(
        size[0] if size else None, size[1] if size else None, tw, th, fx, fy, z)

    cmd = [
        # -nostdin: a worker's ffmpeg must never wait on a terminal.
        "ffmpeg", "-nostdin", "-y",
        "-loglevel", "error",
        "-loop", "1",
        "-i", str(image_path),
        "-t", str(duration_s),
        *filter_args,
        "-r", str(fps),
        "-c:v", "libx264",
        "-preset", "ultrafast",
        "-tune", "stillimage",
        "-pix_fmt", "yuv420p",
        "-an",
        str(out_path),
    ]

    try:
        result = subprocess.run(
            cmd, capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        log.warning("Ken Burns ffmpeg timed out for %s", image_path)
        return None
    except FileNotFoundError:
        log.error("ffmpeg not on PATH — cannot Ken Burns")
        return None

    if result.returncode != 0:
        log.warning("Ken Burns ffmpeg failed (rc=%d): %s", result.returncode, (result.stderr or "")[:400])
        return None
    if not out_path.exists() or out_path.stat().st_size < 1024:
        return None
    return out_path

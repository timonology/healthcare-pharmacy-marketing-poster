"""
Generate a hero photograph for a poster template.

Separate from video_agent/ai_imagery.py on purpose. That module writes files to
disk for the video pipeline, defaults to a 9:16 clip aspect, and its style
presets ("cartoon", "editorial illustration") are tuned for B-roll. A poster
hero has different requirements: it returns bytes for the API to store, needs
poster aspect ratios, and has to leave room for type to sit on top of it.

The prompt engineering below matters more than the model choice. Left to
itself, an image model asked for "a pharmacy" returns a busy, centred,
evenly-lit stock photo — the worst possible base for a poster, because there is
nowhere to put a headline.
"""

from __future__ import annotations

import asyncio
import logging
import os
import time
from typing import Any

import httpx

from app.config import get_settings

log = logging.getLogger(__name__)


class ImageGenerationError(RuntimeError):
    """Generation failed or is not configured."""


# Poster-shaped aspect ratios Flux accepts. A full-bleed hero is usually the
# top half or two-thirds of an A4 page rather than the whole sheet.
ASPECT_RATIOS: dict[str, str] = {
    "banner": "16:9",     # a wide strip across the top
    "hero": "4:3",        # the classic upper-half hero
    "portrait": "3:4",    # a tall side panel
    "full": "9:16",       # full-bleed behind the whole poster
}

DEFAULT_ASPECT = "hero"

# Appended to every prompt.
#
# - "no text" because image models produce garbled lettering, and the poster's
#   own type layer supplies the words.
# - The composition hints reserve space for a headline. Without them the
#   subject lands dead centre and every layout option is ruined.
# - Photographic language ("shallow depth of field", "natural light") steers
#   away from the plasticky 3D-render look Flux defaults to.
_QUALITY_SUFFIX = (
    "professional editorial photography, natural light, shallow depth of field, "
    "clean uncluttered composition with generous negative space for text overlay, "
    "subject positioned off-centre, muted realistic colour grading, "
    "no text, no words, no lettering, no watermark, no logos"
)

# UK community-pharmacy context, so output doesn't look like a US hospital.
_CONTEXT = "UK community pharmacy healthcare setting"


def is_configured() -> bool:
    return bool(get_settings().replicate_api_token)


#: For a photo that will be cropped to a circle portrait. The default suffix
#: pushes the subject off-centre to leave room for a headline — right for a
#: full-width hero, wrong for a circle, where an off-centre face is cut in half.
_PORTRAIT_SUFFIX = (
    "professional editorial portrait photography, natural light, shallow depth of field, "
    "subject centred and filling the middle of the frame, plain uncluttered background, "
    "muted realistic colour grading, no text, no words, no lettering, no watermark, no logos"
)


def build_prompt(description: str, *, context: bool = True, centred: bool = False) -> str:
    parts = [description.strip().rstrip(".")]
    if context:
        parts.append(_CONTEXT)
    parts.append(_PORTRAIT_SUFFIX if centred else _QUALITY_SUFFIX)
    return ", ".join(p for p in parts if p)


async def generate_poster_image(
    description: str,
    aspect: str = DEFAULT_ASPECT,
    timeout_s: int = 120,
    centred: bool = False,
) -> tuple[bytes, str]:
    """
    Generate one image. Returns (bytes, content_type).

    Raises ImageGenerationError rather than returning a sentinel: an admin is
    waiting on this and a silent empty result would surface as a confusing
    "generation failed" three layers up with no cause attached.
    """
    settings = get_settings()
    if not settings.replicate_api_token:
        raise ImageGenerationError(
            "Image generation is not configured. Set REPLICATE_API_TOKEN."
        )

    try:
        import replicate
    except ImportError as exc:  # pragma: no cover - dependency is declared
        raise ImageGenerationError("The replicate package is not installed.") from exc

    prompt = build_prompt(description, centred=centred)
    ratio = ASPECT_RATIOS.get(aspect, ASPECT_RATIOS[DEFAULT_ASPECT])
    log.info("Poster imagery: %r (aspect=%s)", description[:80], ratio)

    started = time.time()

    # The synchronous module-level replicate.run(), pushed onto a worker thread.
    #
    # Not Client.async_run(): that surface varies across releases of the
    # replicate package, and pyproject only pins ">=0.30". The video pipeline
    # has been calling replicate.run() successfully for a while
    # (video_agent/ai_imagery.py), so this reuses the call that is known to work
    # against the version actually installed. asyncio.to_thread keeps the event
    # loop free while it blocks.
    def _run() -> Any:
        os.environ["REPLICATE_API_TOKEN"] = settings.replicate_api_token or ""
        return replicate.run(
            settings.replicate_flux_model,
            input={
                "prompt": prompt,
                "aspect_ratio": ratio,
                "num_outputs": 1,
                # PNG rather than WebP: this is a print asset that gets scaled
                # and composited, and the extra bytes are irrelevant for a
                # one-off authoring step.
                "output_format": "png",
                "output_quality": 95,
            },
        )

    try:
        output: Any = await asyncio.wait_for(
            asyncio.to_thread(_run), timeout=timeout_s
        )
    except asyncio.TimeoutError as exc:
        raise ImageGenerationError(
            f"The image service did not respond within {timeout_s}s."
        ) from exc
    except Exception as exc:
        # Provider text (model version ids, account limits) stays in the log.
        log.error("Poster imagery call failed: %s", exc)
        raise ImageGenerationError("The image service failed. Try again in a minute.") from exc

    url = _first_url(output)
    if not url:
        raise ImageGenerationError("The image service returned no image.")

    try:
        async with httpx.AsyncClient(timeout=timeout_s) as http:
            res = await http.get(url)
            res.raise_for_status()
            data = res.content
    except httpx.HTTPError as exc:
        # The exception text includes the signed delivery URL: log only.
        log.error("Poster imagery download failed: %s", exc)
        raise ImageGenerationError("Could not download the generated image.") from exc

    log.info(
        "Poster imagery: %d KB in %.1fs", len(data) // 1024, time.time() - started
    )
    return data, res.headers.get("content-type", "image/png")


def _first_url(output: Any) -> str | None:
    """
    Pull a URL out of whatever Replicate returned.

    The client's return shape varies by model and client version: a list of
    strings, a single string, or FileOutput objects exposing .url. Handling all
    three here keeps that churn from reaching the caller.
    """
    if output is None:
        return None
    if isinstance(output, str):
        return output
    if isinstance(output, (list, tuple)):
        return _first_url(output[0]) if output else None
    url = getattr(output, "url", None)
    return str(url) if url else None

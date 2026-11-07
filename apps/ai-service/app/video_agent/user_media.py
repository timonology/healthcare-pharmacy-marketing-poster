"""The pharmacy's own pictures, pinned into the visuals stage.

A pharmacist can attach up to four photographs when they queue a video — their
shopfront, their team, a poster they already made. Those become B-roll for
particular beats of the script; every other beat stays stock footage.

**Nothing here calls an image model.** These are pictures the pharmacy already
owns. `visual_style="ai_imagery"` is a separate feature with a per-image cost,
and sending somebody's own photograph to Replicate would bill them for it.

**Nothing here can fail a render.** A picture that will not download or will not
decode gives up its beat to stock footage and records why. The alternative — a
render that dies because one JPEG was truncated — trades a cosmetic problem for
a lost video and a spent allowance.
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

from . import framing

log = logging.getLogger(__name__)

#: Mirrors `Video.VideoImageLimit` in the domain and `MAX_VIDEO_IMAGES` in
#: shared-types. The .NET side refuses more than this, so a longer list here
#: means a row written by something other than the API.
MAX_USER_IMAGES = 4


def plan_pins(segment_count: int, image_count: int) -> dict[int, int]:
    """Which script beat each picture takes, as ``{segment index: image index}``.

    **Spread, not stacked at the front.** Putting all four pictures on the first
    four beats makes a video that opens as a slideshow and then becomes a stock
    reel — the join is obvious and it reads as two different videos. Anchoring
    them across the script keeps the pharmacy's own imagery present throughout.

    The first beat is always pinned when there is anything to pin: the opening
    three seconds decide whether the video is watched at all, and a real
    shopfront there is worth more than any stock clip.

    The last beat is taken next, because that is the call to action — "come and
    see us" over a photograph of the actual us.

    Ordering is by segment so the pictures appear in the order they were
    attached, which is the order the pharmacist arranged them in.
    """
    if segment_count <= 0 or image_count <= 0:
        return {}

    usable = min(image_count, MAX_USER_IMAGES, segment_count)

    if usable >= segment_count:
        # At least as many pictures as beats: every beat gets one, in order.
        #
        # The spreading arithmetic below reaches the same answer here — that is
        # checked exhaustively up to 39 beats — so this branch is a statement of
        # intent rather than a correction. It is kept because it stays true if
        # the spacing below is ever retuned, and because "one each, in order" is
        # easier to verify by eye than the general case.
        #
        # What actually prevents a picture being lost is the *centred* interior
        # spacing below. An earlier version picked interior beats without
        # centring, two picks landed on the same beat, `set()` swallowed one,
        # and a pharmacist who attached four pictures silently got three.
        ordered = list(range(segment_count))
    else:
        ordered = [0]
        if usable >= 2:
            ordered.append(segment_count - 1)

        # Whatever is left goes to interior beats, evenly spaced. `step` is
        # greater than one here by construction — there are always more interior
        # beats than pictures left to place — so these cannot collide.
        remaining = usable - len(ordered)
        if remaining > 0:
            interior = list(range(1, segment_count - 1))
            step = len(interior) / remaining
            for n in range(remaining):
                ordered.append(interior[int(n * step + step / 2)])

        ordered = sorted(set(ordered))

    return {segment: image for image, segment in enumerate(ordered)}


def prepare_user_clips(
    images: list[dict],
    pins: dict[int, int],
    out_dir: Path,
    target_size: tuple[int, int],
    duration_for: Any,
    *,
    download: Any,
    to_clip: Any,
) -> tuple[dict[int, str], list[str]]:
    """Turn the pinned pictures into clips.

    Returns ``({segment index: clip path}, [reasons anything was skipped])``.

    ``download`` and ``to_clip`` are passed in rather than imported so this can
    be tested without Azure or ffmpeg — the failure paths are the whole point of
    the function, and they are the ones a real dependency makes hardest to
    exercise.

    The cropping is not done here: ``to_clip`` is
    ``ai_imagery.image_to_video_clip``, which scales with
    ``force_original_aspect_ratio=increase`` and crops to the target. That is
    the same cover-crop every other still in this pipeline gets, so a portrait
    photograph and a landscape one both fill 9:16 without stretching.
    """
    clips: dict[int, str] = {}
    skipped: list[str] = []

    out_dir.mkdir(parents=True, exist_ok=True)

    for segment_index, image_index in sorted(pins.items()):
        if image_index >= len(images):
            continue

        image = images[image_index] or {}
        key = (image.get("blobKey") or "").strip()
        source = image.get("source") or "upload"
        if not key:
            skipped.append(f"image {image_index + 1}: no blob key on the record")
            continue

        suffix = Path(key).suffix or ".jpg"
        local = out_dir / f"user_{image_index}{suffix}"

        try:
            if not download(key, local):
                # Not an error: the blob may have been removed between queueing
                # and rendering. The beat falls back to stock.
                skipped.append(f"image {image_index + 1} ({source}): not found in storage")
                continue

            # How the pharmacist framed it. Absent means centre and 1.0, which
            # is what every picture got before either could be chosen — so an
            # old row and a new untouched one render identically. Unreadable
            # values fall back the same way rather than costing the picture its
            # beat (see `framing.coord` / `framing.zoom_of`).
            focal = (
                framing.coord(image.get("focalX")),
                framing.coord(image.get("focalY")),
            )
            zoom = framing.zoom_of(image.get("zoom"))
            clip = to_clip(
                local, out_dir, duration_for(segment_index), target_size,
                focal=focal, zoom=zoom)
            if clip is None:
                # ffmpeg refused it — truncated file, or a format that is not
                # what the content type claimed.
                skipped.append(f"image {image_index + 1} ({source}): could not be decoded")
                continue

            clips[segment_index] = str(clip)
            log.info(
                "Pinned %s image %d to segment %d (%s)",
                source, image_index + 1, segment_index, key,
            )
        except Exception as exc:  # noqa: BLE001
            # Deliberately broad. Everything in here is a picture that did not
            # work out, and no picture is worth a render.
            skipped.append(
                f"image {image_index + 1} ({source}): {type(exc).__name__}: {exc}"[:200])
            log.warning("Attached image %d could not be used: %s", image_index + 1, exc)

    if skipped:
        log.warning(
            "%d of %d attached image(s) could not be used; those beats fall back "
            "to stock footage. %s", len(skipped), len(pins), "; ".join(skipped),
        )

    return clips, skipped

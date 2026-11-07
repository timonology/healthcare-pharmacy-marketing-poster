"""A damaged attachment is dropped at once, not after a minute.

Every smoke render and refine spent 60 seconds on the deliberately damaged
upload: Pillow could not read it, so it was handed to ffmpeg to try, and the
looping clip encode on a file that is not a picture never ends — it sat until
the timeout. The picture was dropped either way; the minute was pure waste.
"""

import shutil
import time
from pathlib import Path

import pytest

from app.video_agent.ai_imagery import image_to_video_clip

needs_ffmpeg = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")


@needs_ffmpeg
def test_a_file_that_is_not_a_picture_is_dropped_quickly(tmp_path: Path) -> None:
    damaged = tmp_path / "smoke-damaged.png"
    damaged.write_bytes(b"not a picture" * 64)  # exactly what the smoke run uploads

    started = time.monotonic()
    clip = image_to_video_clip(damaged, tmp_path, 2.0, (108, 192), fps=10)

    assert clip is None
    assert time.monotonic() - started < 10, "the damaged picture must not wait out the encode timeout"


@needs_ffmpeg
def test_a_good_picture_still_becomes_a_clip(tmp_path: Path) -> None:
    from PIL import Image

    good = tmp_path / "shopfront.png"
    Image.new("RGB", (64, 64), "red").save(good)

    clip = image_to_video_clip(good, tmp_path, 2.0, (108, 192), fps=10)

    assert clip is not None and clip.exists()

"""Each tile in the shot picker is a still of that moment, from the finished video."""

import shutil
import subprocess
from pathlib import Path

import pytest

from app.video_agent.worker import _shot_thumbnails, shot_thumb_key

needs_ffmpeg = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")

COLOURS = ["red", "green", "blue"]


def _three_moments(path: Path) -> None:
    """One second of red, then green, then blue."""
    inputs = []
    for colour in COLOURS:
        inputs += ["-f", "lavfi", "-i", f"color=c={colour}:s=320x568:d=1:r=10"]
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", *inputs,
         "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]",
         "-pix_fmt", "yuv420p", str(path)],
        check=True,
    )


def _dominant(path: str) -> str:
    from PIL import Image

    r, g, b = Image.open(path).convert("RGB").resize((1, 1)).getpixel((0, 0))
    return max((("red", r), ("green", g), ("blue", b)), key=lambda c: c[1])[0]


@needs_ffmpeg
def test_each_moment_gets_a_still_of_its_own_shot(tmp_path: Path) -> None:
    video = tmp_path / "final.mp4"
    _three_moments(video)
    timeline = [{"segment": i, "start": float(i), "end": float(i + 1)} for i in range(3)]

    stills = _shot_thumbnails(video, timeline)

    assert sorted(stills) == [0, 1, 2]
    assert [_dominant(stills[i]) for i in range(3)] == COLOURS


@needs_ffmpeg
def test_filler_shots_and_junk_get_no_tile(tmp_path: Path) -> None:
    video = tmp_path / "final.mp4"
    _three_moments(video)
    timeline = [{"segment": None, "start": 0, "end": 1, "filler": True},
                {"segment": 1, "start": "x", "end": 2}, "junk"]

    assert _shot_thumbnails(video, timeline) == {}


def test_stills_live_under_the_video_so_deleting_it_deletes_them() -> None:
    assert shot_thumb_key("owner", "vid", 3) == "videos/owner/vid/shots/03.jpg"

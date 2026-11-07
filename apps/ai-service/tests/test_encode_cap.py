"""The finished video's size is bounded, whatever the footage.

A 19-second re-cut onto detailed footage came out at 38 MB (16 Mbps) because
the encode passed a bitrate *and* a CRF, and x264 honours the CRF and drops the
bitrate. That file streams to pharmacy screens on shop wifi.
"""

import shutil
import subprocess
from pathlib import Path

import pytest

from app.video_agent.nodes import _rate_kbps, encode_params

needs_ffmpeg = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")


def _env(**values):
    return lambda key, default: values.get(key, default)


def test_the_cap_is_video_bitrate_with_two_seconds_of_buffer() -> None:
    params = encode_params(_env(VIDEO_BITRATE="3500k"))
    assert params[params.index("-maxrate") + 1] == "3500k"
    assert params[params.index("-bufsize") + 1] == "7000k"


def test_quality_still_comes_from_crf_and_the_file_still_streams() -> None:
    params = encode_params(_env(FFMPEG_CRF="24"))
    assert params[params.index("-crf") + 1] == "24"
    assert params[params.index("-movflags") + 1] == "+faststart"
    assert "-b:v" not in params


@pytest.mark.parametrize("raw, kbps", [
    ("3500k", 3500), ("3.5M", 3500), ("2m", 2000), ("4000000", 4000), (" 900k ", 900),
    ("", 4000), ("fast", 4000), ("5", 4000),
])
def test_rates_are_read_the_way_ffmpeg_writes_them(raw: str, kbps: int) -> None:
    assert _rate_kbps(raw, 4000) == kbps


@needs_ffmpeg
def test_hard_footage_is_held_to_the_cap(tmp_path: Path) -> None:
    """Noise is the worst case for an encoder — the grass field, exaggerated."""
    seconds = 6
    src = tmp_path / "noise.mkv"
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=540x960:rate=24",
         "-vf", "noise=alls=60:allf=t,format=yuv420p", "-t", str(seconds),
         "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", str(src)],
        check=True,
    )
    out = tmp_path / "out.mp4"
    cap_kbps = 1000
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", str(src), "-c:v", "libx264",
         "-preset", "ultrafast", *encode_params(_env(VIDEO_BITRATE=f"{cap_kbps}k")), str(out)],
        check=True,
    )
    kbps = out.stat().st_size * 8 / seconds / 1000

    # The most VBV allows: the cap for every second, plus one buffer's worth
    # borrowed up front — and 10% for the MP4 container. Uncapped, this same
    # encode is about fifty times the cap.
    bufsize_kbits = 2 * cap_kbps
    ceiling = (cap_kbps * seconds + bufsize_kbits) / seconds * 1.10
    assert kbps <= ceiling, f"{kbps:.0f} kbit/s against a {ceiling:.0f} kbit/s ceiling"

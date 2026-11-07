"""A phone photo's EXIF rotation is applied before ffmpeg sees it.

Browsers honour the tag, so the framing preview shows the picture upright;
ffmpeg does not, so without this the render framed the sideways pixels.
"""

from pathlib import Path

from PIL import Image

from app.video_agent.ai_imagery import _upright


def _orientation(value: int) -> "Image.Exif":
    exif = Image.Exif()
    exif[0x0112] = value
    return exif


def _tagged(path: Path, orientation: int | None) -> None:
    # Stored landscape, like a phone sensor image.
    im = Image.new("RGB", (400, 200), (200, 30, 30))
    exif = Image.Exif()
    if orientation is not None:
        exif[0x0112] = orientation
    im.save(path, exif=exif)


def test_a_rotated_phone_photo_is_stood_upright(tmp_path: Path) -> None:
    src = tmp_path / "phone.jpg"
    _tagged(src, 6)  # "rotate 90° clockwise to view"

    fixed, size = _upright(src, tmp_path)

    assert fixed != src
    assert size == (200, 400), "the browser measures it as portrait, so must the render"
    with Image.open(fixed) as im:
        assert im.size == (200, 400)
        assert im.getexif().get(0x0112, 1) == 1, "no tag left for a newer ffmpeg to apply twice"


def test_a_rotated_cmyk_jpeg_is_stood_upright_too(tmp_path: Path) -> None:
    # Print and design tools export CMYK. PNG cannot hold it, and the first
    # version tried — so the photo came back sideways with no size, and lost
    # its zoom without a word.
    src = tmp_path / "print.jpg"
    Image.new("CMYK", (400, 200)).save(src, exif=_orientation(6))

    fixed, size = _upright(src, tmp_path)

    assert fixed != src and size == (200, 400)


def test_an_untagged_photo_is_used_as_it_is(tmp_path: Path) -> None:
    src = tmp_path / "plain.jpg"
    _tagged(src, None)

    assert _upright(src, tmp_path) == (src, (400, 200))


def test_an_unreadable_file_is_passed_through_without_a_size(tmp_path: Path) -> None:
    # ffmpeg gets its own chance; the photo is not dropped here.
    src = tmp_path / "broken.jpg"
    src.write_bytes(b"not a jpeg")

    assert _upright(src, tmp_path) == (src, None)

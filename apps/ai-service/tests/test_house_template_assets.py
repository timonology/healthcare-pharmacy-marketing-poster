"""The house-template photographs are baked exactly as the canvases draw them."""

from PIL import Image

from app.tools.house_template_assets import (
    SPECS, AssetSpec, Fade, _rgb, bake, crop_aspect_error, encode,
)


def _checker(size: tuple[int, int]) -> Image.Image:
    """A picture with detail everywhere, so a fade has something to cover."""
    img = Image.new("RGB", size)
    for x in range(size[0]):
        for y in range(size[1]):
            img.putpixel((x, y), (200, 40, 40) if (x // 10 + y // 10) % 2 else (40, 40, 200))
    return img


def test_every_crop_has_the_shape_it_is_drawn_at() -> None:
    # The canvas draws the file into exactly `out`; a crop of another shape is
    # a stretched face on a poster.
    for spec in SPECS:
        assert crop_aspect_error(spec) <= 0.01, spec.name


def test_names_and_files_are_unique_and_well_formed() -> None:
    assert len({s.name for s in SPECS}) == len(SPECS)
    assert len({s.file_stem for s in SPECS}) == len(SPECS)
    for s in SPECS:
        # The API serves only a 32-hex name with a known extension.
        assert len(s.file_stem) == 32 and all(c in "0123456789abcdef" for c in s.file_stem)
        assert s.extension == ("png" if s.masked else "jpg")


def test_a_circle_is_transparent_outside_and_solid_inside() -> None:
    spec = AssetSpec(name="t", file_stem="0" * 32, pexels_id=1, photographer="x",
                     source_size=(200, 200), box=(0, 0, 1, 1), out=(100, 100), circle=True)
    out = bake(_checker((200, 200)), spec)

    assert out.mode == "RGBA" and out.size == (100, 100)
    assert out.getpixel((0, 0))[3] == 0, "corners are transparent"
    assert out.getpixel((50, 50))[3] == 255, "the middle is the photo"
    # Anti-aliased: where the curve crosses the diagonal the alpha is partial,
    # not a staircase of 0 and 255.
    diagonal = [out.getpixel((i, i))[3] for i in range(0, 30)]
    assert any(0 < a < 255 for a in diagonal)


def test_a_fade_reaches_the_page_colour_at_the_edge_and_leaves_the_rest() -> None:
    spec = AssetSpec(name="t", file_stem="0" * 32, pexels_id=1, photographer="x",
                     source_size=(100, 100), box=(0, 0, 1, 1), out=(100, 100),
                     fades=(Fade("left", 0.3, "#0b2e59"),))
    src = _checker((100, 100))
    out = bake(src, spec)

    assert out.getpixel((0, 50)) == _rgb("#0b2e59"), "fully the page colour at the edge"
    assert out.getpixel((80, 50)) == src.getpixel((80, 50)), "untouched past the fade"
    # Monotonic: each step in is no closer to the page colour than the last.
    dist = [sum(abs(a - b) for a, b in zip(out.getpixel((x, 5)), _rgb("#0b2e59"))) for x in range(0, 30)]
    assert dist[0] == 0 and dist[-1] > 0


def test_each_side_fades_from_its_own_edge() -> None:
    for side, edge_px, inner_px in [
        ("right", (99, 50), (20, 50)), ("top", (50, 0), (50, 80)), ("bottom", (50, 99), (50, 20)),
    ]:
        spec = AssetSpec(name="t", file_stem="0" * 32, pexels_id=1, photographer="x",
                         source_size=(100, 100), box=(0, 0, 1, 1), out=(100, 100),
                         fades=(Fade(side, 0.3, "#ffffff"),))
        src = _checker((100, 100))
        out = bake(src, spec)
        assert out.getpixel(edge_px) == (255, 255, 255), side
        assert out.getpixel(inner_px) == src.getpixel(inner_px), side


def test_rectangles_are_jpegs_and_circles_are_pngs() -> None:
    for spec in SPECS:
        data = encode(Image.new("RGBA" if spec.masked else "RGB", (8, 8)), spec)
        assert data[:4] == (b"\x89PNG" if spec.masked else b"\xff\xd8\xff\xe0"[:3] + data[3:4])


def test_a_sideways_original_is_stood_up_before_cropping() -> None:
    # A portrait photo stored landscape with an EXIF "rotate 90" tag. The crop
    # boxes were measured on the upright picture a browser shows.
    from app.tools.house_template_assets import normalise
    import io

    stored = Image.new("RGB", (60, 40), (200, 0, 0))
    exif = stored.getexif()
    exif[0x0112] = 6  # Orientation: rotate 90 CW to display
    buf = io.BytesIO()
    stored.save(buf, "JPEG", exif=exif.tobytes())

    upright = normalise(Image.open(io.BytesIO(buf.getvalue())))
    assert upright.size == (40, 60)


def test_a_wide_gamut_original_is_converted_to_srgb() -> None:
    # Adobe RGB is the usual wide-gamut case for stock. The same numbers mean a
    # more saturated green there than in sRGB; left unconverted, the poster
    # prints a duller, shifted colour than the photo that was checked.
    import pytest
    from pathlib import Path
    from app.tools.house_template_assets import normalise

    a98 = Path("/usr/share/color/icc/ghostscript/a98.icc")
    if not a98.exists():
        pytest.skip("no Adobe RGB profile on this machine")

    img = Image.new("RGB", (4, 4), (10, 200, 30))
    img.info["icc_profile"] = a98.read_bytes()
    out = normalise(img)

    assert out.mode == "RGB"
    assert out.getpixel((0, 0)) != (10, 200, 30), "converted into sRGB, not passed through"
    assert out.getpixel((0, 0))[1] > 150, "still green"


def test_the_shape_check_accepts_any_size_of_the_vetted_photo_only() -> None:
    from app.tools.house_template_assets import same_shape

    assert same_shape((2600, 3900), (4000, 6000))      # a smaller rendition
    assert not same_shape((3900, 2600), (4000, 6000))  # on its side
    assert not same_shape((2600, 2600), (4000, 6000))  # another photo


def test_an_arch_is_rounded_on_top_and_square_below() -> None:
    spec = AssetSpec(name="t", file_stem="0" * 32, pexels_id=1, photographer="x",
                     source_size=(100, 160), box=(0, 0, 1, 1), out=(100, 160), arch=True)
    out = bake(_checker((100, 160)), spec)

    assert out.mode == "RGBA" and spec.extension == "png"
    assert out.getpixel((0, 0))[3] == 0, "top corners are cut away"
    assert out.getpixel((99, 0))[3] == 0
    assert out.getpixel((50, 2))[3] == 255, "the top of the curve is photo"
    assert out.getpixel((0, 159))[3] == 255, "bottom corners stay square"
    assert out.getpixel((99, 159))[3] == 255


def test_credits_cover_every_photo_whatever_was_fetched() -> None:
    # Built from SPECS, so a run that skipped existing photos (or used --only)
    # cannot drop anybody's credit.
    from app.tools.house_template_assets import credit_rows, credits_markdown

    text = credits_markdown(credit_rows())
    for s in SPECS:
        assert s.file_name in text and s.photographer in text


def test_existing_photos_are_skipped_unless_forced(tmp_path, monkeypatch) -> None:
    import app.tools.house_template_assets as hta

    out = tmp_path / hta.ASSET_DIR
    out.mkdir(parents=True)
    for s in hta.SPECS:
        (out / s.file_name).write_bytes(b"already built")

    fetched = []
    monkeypatch.setenv("PEXELS_API_KEY", "test")
    monkeypatch.setattr(hta, "fetch_original", lambda pid, key: fetched.append(pid) or (_ for _ in ()).throw(RuntimeError("no network in tests")))

    assert hta.main(["--repo", str(tmp_path)]) == 0
    assert fetched == [], "nothing already built is fetched again"
    assert all((out / s.file_name).read_bytes() == b"already built" for s in hta.SPECS)
    assert (out.parent / "CREDITS.md").exists()

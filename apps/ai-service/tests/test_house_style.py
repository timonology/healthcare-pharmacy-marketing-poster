"""The house-style layout engine the AI designer writes for.

Every archetype, with and without a photograph, must come out as a poster the
built-in house set would pass under: tokens once, one booking QR, ticks drawn
as lines, headlines of 60px or more, fills on the waves and the diagonal, no
translucent rects, nothing off the house palette, and text that wraps the same
way in the editor and the PDF. Photographs are cover-cropped (never stretched)
and baked: circles as transparent PNGs, fades into the JPEG.
"""

from __future__ import annotations

import io
import re

import pytest
from PIL import Image

from app.chains import house_style as hs
from app.tools import house_templates as ht

PLANS = {
    "circle-portrait": dict(
        archetype="circle-portrait", name="Trusted Local Pharmacy", eyebrow="Your local team",
        headline="Trusted care, every visit",
        lede="Friendly, qualified pharmacists who take the time to listen.",
        items=["Expert advice, no appointment", "Prescriptions ready on time",
               "Quick, private health checks"]),
    "diagonal-split": dict(
        archetype="diagonal-split", name="Travel Clinic", eyebrow="Travel health",
        headline="Travel clinic now open", lede="Vaccinations and advice before you go.",
        items=["Vaccinations", "Malaria advice", "Book ahead"]),
    "wave-footer": dict(
        archetype="wave-footer", name="Meet the Team", eyebrow="Meet the team",
        headline="Friendly faces, expert advice", lede="Here to help, every day.",
        items=["Ask us anything", "Private consultation room", "No appointment needed"]),
    "checklist": dict(
        archetype="checklist", name="Just Ask", eyebrow="Ask your pharmacist",
        headline="Not sure? Just ask.",
        items=["Coughs, colds and sore throats", "Minor skin conditions", "Aches and pains",
               "Hay fever and allergies"],
        note="A private consultation room is available."),
}


def _plan(name: str, **overrides) -> hs.Plan:
    plan, problems = hs.Plan.from_json(PLANS[name] | overrides)
    assert plan is not None and problems == [], problems
    return plan


def _jpeg(w: int, h: int, colour=(180, 120, 90)) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (w, h), colour).save(buf, "JPEG")
    return buf.getvalue()


CASES = [(name, photo) for name in PLANS for photo in (True, False)]


@pytest.mark.parametrize("name, photo", CASES)
def test_every_archetype_passes_the_house_checks(name: str, photo: bool) -> None:
    doc, problems = hs.compose(_plan(name), has_photo=photo)
    assert problems == [], problems


@pytest.mark.parametrize("name, photo", CASES)
def test_house_look_and_print_rules(name: str, photo: bool) -> None:
    doc, _ = hs.compose(_plan(name), has_photo=photo)
    shapes = doc["shapes"]
    texts = [s for s in shapes if s["kind"] == "text"]
    all_text = " ".join(t["text"] for t in texts)

    # Contact details as tokens, once each, and exactly one booking QR.
    for token in ("{{pharmacy_name}}", "{{phone}}", "{{address}}"):
        assert all_text.count(token) == 1, token
    qr = [s for s in shapes if s["id"] == "pharmacy-booking-qr"]
    assert len(qr) == 1 and qr[0]["blobKey"] == "/api/brand-kit/booking-qr"

    # A headline of at least 60px, in the face the PDF carries.
    headline = next(s for s in texts if s["id"] == "headline")
    assert headline["fontSize"] >= 60
    assert all(t["fontFamily"] == "Inter, sans-serif" for t in texts)

    # Ticks are lines, never a glyph.
    assert not re.search("[✓✔★•]", all_text)
    if any(s["id"].startswith("item-tick") for s in shapes):
        assert any(s["id"].endswith("-mark") and s["kind"] == "line" and not s["closed"]
                   for s in shapes)

    # No stacked translucent rects faking a fade; the house palette only.
    assert not [s for s in shapes if s["kind"] == "rect" and s["opacity"] < 1]
    colours = {doc["background"]} | {s.get(k) for s in shapes for k in ("fill", "stroke") if s.get(k)}
    assert {c.lower() for c in colours} <= hs.HOUSE_COLOURS

    # The photo, if any, is one image the API will replace with a stored asset.
    photos = [s for s in shapes if s.get("blobKey") == hs.PHOTO_KEY]
    assert len(photos) == (1 if photo else 0)


@pytest.mark.parametrize("name", ["diagonal-split", "wave-footer"])
def test_waves_and_diagonals_are_filled_polygons(name: str) -> None:
    """The outline-only bug: a closed line must carry its fill in every renderer.

    A closed `line` is filled with its stroke colour by the editor, the gallery
    preview and the PDF alike — so the shapes must be closed, and coloured.
    """
    doc, _ = hs.compose(_plan(name), has_photo=True)
    polys = [s for s in doc["shapes"] if s["kind"] == "line" and s.get("closed")]
    assert polys, "no filled polygons"
    for p in polys:
        assert p["stroke"].lower() in hs.HOUSE_COLOURS
        assert len(p["points"]) >= 8


def test_the_headline_is_balanced_not_orphaned() -> None:
    doc, _ = hs.compose(_plan("diagonal-split"), has_photo=True)
    head = next(s for s in doc["shapes"] if s["id"] == "headline")["text"].split("\n")
    assert head == ["Travel clinic", "now open"]


def test_copy_too_long_for_the_layout_is_reported_for_the_model() -> None:
    plan = _plan("diagonal-split", headline="Seasonal flu vaccinations available for everyone today")
    _, problems = hs.compose(plan, has_photo=False)
    assert any("headline does not fit" in p for p in problems)


def test_too_many_items_is_a_plan_problem() -> None:
    plan, problems = hs.Plan.from_json(PLANS["diagonal-split"] | {"items": ["a", "b", "c", "d"]})
    assert any("at most 3 items" in p for p in problems)


@pytest.mark.parametrize("text, why", [
    ("Visit your pharmacy today", "Your Pharmacy"),
    ("20% off all flu jabs", "percentage"),
    ("Rated 5 stars by patients", "review"),
    ("Nurofen for aches and pains", "medicine brand"),
    ("✓ No appointment", "glyph"),
    ("Riverside Pharmacy is here", "names a pharmacy"),
    ("Call 0115 496 0000", "phone number"),
    ("Book at www.example.co.uk", "web address"),
    ("Ask {{pharmacy_name}}", "token"),
])
def test_forbidden_copy_is_rejected(text: str, why: str) -> None:
    _, problems = hs.Plan.from_json(PLANS["checklist"] | {"lede": text})
    assert any(why.lower() in p.lower() for p in problems), problems


def test_generic_pharmacy_wording_is_allowed() -> None:
    _, problems = hs.Plan.from_json(PLANS["circle-portrait"] | {
        "eyebrow": "Your local pharmacy team", "lede": "NHS Pharmacy First, at our community pharmacy."})
    assert problems == []


# ── photographs ──────────────────────────────────────────────────────────────


@pytest.mark.parametrize("name", list(PLANS))
@pytest.mark.parametrize("size", [(1600, 900), (900, 1600), (1000, 1000)])
def test_photos_are_cover_cropped_to_the_slot_never_stretched(name: str, size) -> None:
    baked = hs.bake_photo(_jpeg(*size), name)
    slot = hs.ARCHETYPES[name].slot
    img = Image.open(io.BytesIO(baked.data))
    assert img.size == (slot.width, slot.height) == (baked.width, baked.height)
    # Drawn at exactly its own size, so its ratio on the poster is its true one.
    doc, _ = hs.compose(_plan(name), has_photo=True)
    shape = next(s for s in doc["shapes"] if s.get("blobKey") == hs.PHOTO_KEY)
    assert (shape["width"], shape["height"]) == img.size


def test_cover_crop_keeps_the_subject_not_a_squashed_whole() -> None:
    """A wide photo in a square slot loses its sides; it is never squeezed.

    The outer quarters are red, the middle blue. Cover-cropping keeps only the
    middle; stretching would keep the red edges, squashed, on both sides.
    """
    img = Image.new("RGB", (2000, 1000), (255, 0, 0))
    img.paste((0, 0, 255), (500, 0, 1500, 1000))
    out = hs.cover_crop(img, hs.PhotoSlot(500, 500))
    assert out.size == (500, 500)
    for x in (5, 250, 495):
        r, g, b = out.getpixel((x, 250))
        assert b > 200 and r < 60, (x, (r, g, b))


def test_a_translucent_rect_fails_the_house_check() -> None:
    """Stacked translucent rects faking a fade are the look this replaces."""
    doc, _ = hs.compose(_plan("circle-portrait"), has_photo=True)
    doc["shapes"].append({"id": "scrim", "kind": "rect", "position": {"x": 0, "y": 0},
                          "width": 1240, "height": 600, "fill": hs.NAVY, "opacity": 0.6,
                          "rotation": 0, "scale": {"x": 1, "y": 1}, "draggable": True,
                          "zIndex": 1})
    assert any("translucent rect" in p for p in hs.house_check(doc))


def test_a_colour_off_the_house_palette_fails_the_house_check() -> None:
    doc, _ = hs.compose(_plan("checklist"), has_photo=False)
    doc["shapes"][0]["fill"] = "#ff00aa"
    assert any("house palette" in p for p in hs.house_check(doc))


@pytest.mark.parametrize("name", ["circle-portrait"])
def test_circle_portraits_are_transparent_pngs(name: str) -> None:
    baked = hs.bake_photo(_jpeg(1200, 900), name)
    assert baked.content_type == "image/png"
    img = Image.open(io.BytesIO(baked.data))
    assert img.mode == "RGBA"
    assert img.getpixel((0, 0))[3] == 0                                  # corner: clear
    assert img.getpixel((img.width // 2, img.height // 2))[3] == 255     # centre: photo


def test_fades_are_baked_into_the_jpeg() -> None:
    baked = hs.bake_photo(_jpeg(900, 1600, colour=(20, 20, 20)), "checklist")
    assert baked.content_type == "image/jpeg"
    img = Image.open(io.BytesIO(baked.data)).convert("RGB")
    sky = tuple(int(ht.SKY[i:i + 2], 16) for i in (1, 3, 5))
    left = img.getpixel((0, img.height // 2))
    assert all(abs(a - b) <= 6 for a, b in zip(left, sky)), left   # dissolved into the page
    assert img.getpixel((img.width - 5, img.height // 2))[0] < 60       # photo on the right


def test_a_transparent_upload_sits_on_white_not_black() -> None:
    buf = io.BytesIO()
    Image.new("RGBA", (800, 800), (0, 0, 0, 0)).save(buf, "PNG")
    img = hs.load_image(buf.getvalue())
    assert img.getpixel((10, 10)) == (255, 255, 255)


def test_a_file_that_is_not_an_image_is_refused() -> None:
    with pytest.raises(ValueError):
        hs.load_image(b"%PDF-1.4 not a picture")


def test_an_image_declaring_enormous_dimensions_is_refused_before_decoding(monkeypatch) -> None:
    monkeypatch.setattr(hs, "MAX_SOURCE_PIXELS", 100 * 100)
    with pytest.raises(ValueError):
        hs.load_image(_jpeg(200, 200))


@pytest.mark.parametrize("given, expected", [
    ("checklist-bar", "checklist"),
    ("Checklist bar", "checklist"),
    ("checklist_bar", "checklist"),
    ("circle portrait", "circle-portrait"),
    ("Diagonal  Split", "diagonal-split"),
    ("wave", "wave-footer"),
    ("wave-footer", "wave-footer"),
])
def test_archetype_names_from_the_brief_are_understood(given: str, expected: str) -> None:
    assert hs.canonical_archetype(given) == expected
    plan, _ = hs.Plan.from_json({**PLANS["checklist"], "archetype": given})
    assert plan is not None and plan.archetype == expected


# ── "bold" is a tone, not a layout ──────────────────────────────────────────


def test_there_are_four_house_layouts_and_no_bold_one() -> None:
    assert set(hs.ARCHETYPES) == {"circle-portrait", "diagonal-split", "wave-footer", "checklist"}


@pytest.mark.parametrize("name, photo", CASES)
def test_a_bold_poster_passes_the_house_checks_in_every_layout(name: str, photo: bool) -> None:
    doc, problems = hs.compose(_bold(name), has_photo=photo)
    assert problems == [], problems


@pytest.mark.parametrize("name", list(PLANS))
def test_bold_sets_larger_type_in_the_same_layout(name: str) -> None:
    standard, _ = hs.compose(_plan(name), has_photo=True)
    bold, _ = hs.compose(_bold(name), has_photo=True)

    def size(doc, id_):
        return next(s["fontSize"] for s in doc["shapes"] if s["id"] == id_)

    assert size(bold, "headline") > size(standard, "headline")
    # Same layout: the same shapes, in the same places, on the same page.
    assert bold["background"] == standard["background"]
    assert [s["id"] for s in bold["shapes"] if s["kind"] != "text"] == \
        [s["id"] for s in standard["shapes"] if s["kind"] != "text"]


def test_a_model_naming_the_old_bold_layout_gets_a_house_layout_set_bold() -> None:
    plan, problems = hs.Plan.from_json({**PLANS["circle-portrait"], "archetype": "bold"})
    assert problems == []
    assert plan.archetype in hs.ARCHETYPES and plan.archetype != "bold"
    assert plan.tone == "bold"


@pytest.mark.parametrize("brief, tone", [
    ("walk-in flu clinic, bold", "bold"),
    ("A BOLD poster for flu season", "bold"),
    ("make it bolder", "bold"),
    ("trusted local pharmacy, circle portrait", "standard"),
    ("emboldened by our new service", "standard"),
])
def test_the_tone_comes_from_the_brief(brief: str, tone: str) -> None:
    assert hs.tone_for_brief(brief) == tone


def _bold(name: str) -> hs.Plan:
    plan = _plan(name)
    plan.tone = "bold"
    return plan

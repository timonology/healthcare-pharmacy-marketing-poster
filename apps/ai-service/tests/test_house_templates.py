"""The house-style templates: shipped as generated, and every house rule holds."""

import copy
import json
from pathlib import Path

import pytest

from app.tools import house_templates as ht

REPO = Path(__file__).resolve().parents[3]


def test_the_committed_json_is_what_the_generator_makes() -> None:
    # The API embeds the JSON; this file is the source. A hand edit to one
    # without the other would ship a design nobody can regenerate.
    committed = (REPO / ht.OUT).read_text()
    assert committed == ht.render_json(), (
        "templates.json is stale — run: python -m app.tools.house_templates")


def test_every_house_rule_holds() -> None:
    assert ht.check() == []


def test_the_sets_are_named_and_filed_as_asked() -> None:
    built = {t["name"]: t for t in ht.build()}
    first = ["Trusted circle", "Diagonal care", "Team wave",
             "Advice checklist", "Book with us", "Local pharmacy"]
    assert all(built[n]["category"] == "General" for n in first)
    assert {n: built[n]["category"] for n in [
        "Know your numbers", "Diabetes risk check", "Flu vaccination",
        "Winter vaccinations", "Travel clinic", "Weight management"]} == {
        "Know your numbers": "Awareness", "Diabetes risk check": "Awareness",
        "Flu vaccination": "Vaccination", "Winter vaccinations": "Vaccination",
        "Travel clinic": "Vaccination", "Weight management": "Awareness"}
    third = {
        "Shingles vaccination": "Vaccination", "Pneumonia vaccination": "Vaccination",
        "Stop smoking support": "Awareness", "Contraception advice": "Awareness",
        "Pharmacy First": "Promotion", "Prescription delivery": "Promotion",
        "Antibiotics awareness": "Safety", "Return unused medicines": "Safety",
        "Hay fever help": "Seasonal", "Repeat prescriptions": "General",
    }
    assert {n: built[n]["category"] for n in third} == third
    # Every category the gallery filters by has at least one house template.
    assert {t["category"] for t in built.values()} == {
        "Vaccination", "Awareness", "Promotion", "Safety", "Seasonal", "General"}
    assert len(built) == 22
    assert all(t["id"].startswith("sys-house-") for t in built.values())
    assert len({t["id"] for t in built.values()}) == len(built)
    # A4 at 150 DPI, like every built-in template.
    assert (ht.W, ht.H) == (1240, 1754)


def test_every_photo_is_a_built_asset_drawn_at_its_own_shape() -> None:
    sizes = {f"/api/assets/templates/{s.file_name}": s.out for s in ht.SPECS}
    for t in ht.build():
        photos = [s for s in t["shapes"] if s["kind"] == "image" and s["id"] != ht.BOOKING_QR_ID]
        # Photo-free only where that was the decision, never by accident.
        assert bool(photos) == (t["id"] not in ht.PHOTO_FREE), t["id"]
        for p in photos:
            assert p["blobKey"] in sizes, (t["id"], p["blobKey"])
            w, h = sizes[p["blobKey"]]
            # Same aspect ratio as the file: the renderers stretch to the box.
            assert abs(p["width"] / p["height"] - w / h) < 0.005, (t["id"], p["id"])


def test_polygons_are_closed_lines_filled_by_their_stroke() -> None:
    # The only filled polygon the schema has, and now drawn the same way by
    # the editor, the PDF and the preview.
    for t in ht.build():
        for s in t["shapes"]:
            if s["kind"] == "line" and s.get("closed"):
                assert len(s["points"]) >= 6 and s["stroke"].startswith("#")


def _mutated(fn) -> list[str]:
    templates = copy.deepcopy(ht.build())
    fn(templates[0])
    return ht.check(templates)


def _text(t, id_):
    return next(s for s in t["shapes"] if s["id"] == id_)


@pytest.mark.skipif(
    ht.measuring_font() is None,
    reason="No Arial-metric font (Liberation Sans or Arial) on this machine, so the "
           "editor-wrap check cannot measure. Install fonts-liberation, or set HOUSE_FONT.")
def test_the_editor_wrap_check_catches_wide_type() -> None:
    # Wide capitals pass the PDF's character estimate but overflow the real
    # font: they would wrap in the editor only.
    only_editor = _mutated(lambda t: _text(t, "headline").update(text="W" * 16))
    assert any("wraps in the editor" in p for p in only_editor)
    assert not any("wraps in the PDF" in p for p in only_editor)


def test_the_checks_catch_what_they_are_for() -> None:
    # Guards the guard: each rule fails on the fault it exists to stop.
    # Each wrap rule on its own: narrow letters fit the real font but exceed
    # the PDF's character estimate; wide capitals pass the estimate but
    # overflow the real font. Either would wrap in one renderer only.
    only_pdf = _mutated(lambda t: _text(t, "headline").update(text="i" * 25))
    assert any("wraps in the PDF" in p for p in only_pdf)
    assert not any("wraps in the editor" in p for p in only_pdf)
    # A long pharmacy name wraps onto a second line; the phone must not be there.
    assert any("runs into contact-phone" in p for p in _mutated(
        lambda t: _text(t, "contact-phone")["position"].update(y=_text(t, "contact-name")["position"]["y"] + 40)))
    # A wave over a photo that is too short for it: bare page between them.
    templates = copy.deepcopy(ht.build())
    team = next(t for t in templates if t["id"] == "sys-house-team-wave")
    photo = next(s for s in team["shapes"] if s["kind"] == "image" and s["id"] != ht.BOOKING_QR_ID)
    photo["height"] = 1310 - photo["position"]["y"]
    assert any("bare page shows" in p for p in ht.check(templates))
    assert any("contrast" in p for p in _mutated(
        lambda t: _text(t, "eyebrow").update(fill=ht.TEAL_SOFT)))
    assert any("percentage" in p for p in _mutated(
        lambda t: _text(t, "lede").update(text="20% off")))
    assert any("placeholder" in p for p in _mutated(
        lambda t: _text(t, "lede").update(text="Support at your pharmacy")))
    assert any("brand" in p for p in _mutated(
        lambda t: _text(t, "lede").update(text="Nurofen in stock")))
    assert any("glyph" in p for p in _mutated(
        lambda t: _text(t, "lede").update(text="Checked ✓")))
    assert any("booking QR" in p for p in _mutated(
        lambda t: t["shapes"].remove(next(s for s in t["shapes"] if s["id"] == ht.BOOKING_QR_ID))))
    assert any("logo corner" in p for p in _mutated(
        lambda t: t["shapes"].append({**_text(t, "lede"), "id": "stray", "position": {"x": 1000, "y": 1600},
                                      "width": 100, "zIndex": 999})))


def test_the_json_is_valid_canvas_shapes() -> None:
    data = json.loads(ht.render_json())
    for t in data:
        for s in t["shapes"]:
            assert {"id", "kind", "position", "rotation", "scale", "opacity", "draggable", "zIndex"} <= s.keys()

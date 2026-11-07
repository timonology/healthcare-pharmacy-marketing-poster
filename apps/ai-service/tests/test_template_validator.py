"""
Tests for the generated-template print rules.

Two halves, and both matter:

  * The DEFECT tests pin the failures the validator exists to catch. If one of
    these ever passes validation, a broken poster can reach the shared gallery.

  * The REAL GALLERY test runs the validator over the templates in
    TemplateSeeder.cs. Those are hand-authored and are the quality bar, so
    rejecting them means the rules are too strict — which is not a harmless
    kind of wrong: every false positive costs the generator a repair round
    fighting the house style instead of fixing a real problem.

The thresholds in template_validator.py were calibrated against that corpus.
A 3.0:1 contrast floor rejected 8 of the 24 hand-authored designs.
"""

from __future__ import annotations

import copy
import json
import re
from pathlib import Path
from typing import Any

import pytest

from app.chains.design_system import APPROVED_PHOTOS
from app.chains.template_validator import contrast_ratio, validate_canvas

# tests/ -> ai-service/ -> apps/
SEEDER = (
    Path(__file__).resolve().parents[2]
    / "api/src/Acme.Infrastructure/Persistence/TemplateSeeder.cs"
)


def shape(**kw: Any) -> dict[str, Any]:
    base = {"rotation": 0, "scale": {"x": 1, "y": 1}, "opacity": 1, "draggable": True}
    base.update(kw)
    return base


@pytest.fixture
def good() -> dict[str, Any]:
    """A valid poster, modelled on the seeded 'Vaccination Clinic Open'."""
    return {
        "schemaVersion": 1, "id": "x", "ownerId": "sys",
        "name": "Vaccination Clinic", "width": 1240, "height": 1754,
        "background": "#0f172a",
        "viewport": {"pan": {"x": 0, "y": 0}, "zoom": 1},
        "shapes": [
            shape(id="hero", kind="image", position={"x": 0, "y": 0}, zIndex=1,
                  blobKey=APPROVED_PHOTOS["vaccine"], width=1240, height=920),
            shape(id="scrim", kind="rect", position={"x": 0, "y": 0}, zIndex=2,
                  opacity=0.62, width=1240, height=920, fill="#0f172a"),
            shape(id="tag", kind="rect", position={"x": 90, "y": 140}, zIndex=4,
                  width=260, height=62, fill="#22d3ee", cornerRadius=31),
            shape(id="tag-text", kind="text", position={"x": 90, "y": 157}, zIndex=5,
                  text="OPEN NOW", fontSize=26, fontFamily="Inter, sans-serif",
                  fill="#083344", width=260, align="center"),
            shape(id="headline-1", kind="text", position={"x": 90, "y": 300}, zIndex=5,
                  text="VACCINATION", fontSize=118, fontFamily="Inter, sans-serif",
                  fill="#ffffff", width=1060, align="left"),
        ],
    }


def test_valid_poster_passes(good: dict[str, Any]) -> None:
    assert validate_canvas(good) == []


# --------------------------------------------------------------------------- #
# Defects that must be caught
# --------------------------------------------------------------------------- #

def test_rejects_wrong_page_size(good: dict[str, Any]) -> None:
    good["width"], good["height"] = 1920, 1080
    assert any("1240x1754" in p for p in validate_canvas(good))


def test_rejects_invented_image_url(good: dict[str, Any]) -> None:
    good["shapes"][0]["blobKey"] = "https://images.unsplash.com/photo-9999999999999?w=1400"
    assert any("approved photo list" in p for p in validate_canvas(good))


def test_accepts_approved_photo_at_a_different_width(good: dict[str, Any]) -> None:
    """The same asset is used at several widths across the gallery, so identity
    is the Unsplash photo id, not the whole URL including transform params."""
    good["shapes"][0]["blobKey"] = (
        "https://images.unsplash.com/photo-1612277795421-9bc7706a4a34?w=900&q=80"
    )
    assert validate_canvas(good) == []


def test_rejects_text_running_off_the_page(good: dict[str, Any]) -> None:
    good["shapes"][4]["fontSize"] = 40   # below the decorative threshold
    good["shapes"][4]["position"]["x"] = 1180
    assert any("off the page" in p or "trim edge" in p for p in validate_canvas(good))


def test_rejects_display_text_almost_entirely_off_page(good: dict[str, Any]) -> None:
    """Large type may bleed, but not so far that the word is gone."""
    good["shapes"][4]["position"]["x"] = 1180  # 118px headline, 6% visible
    assert any("on the page" in p for p in validate_canvas(good))


def test_allows_large_decorative_text_to_bleed(good: dict[str, Any]) -> None:
    """A giant year numeral running off the right edge is a real device used by
    the seeded 'New Year Health Check'."""
    good["shapes"].append(shape(
        id="year", kind="text", position={"x": 880, "y": 1200}, zIndex=6,
        text="2026", fontSize=150, fontFamily="Inter, sans-serif",
        fill="#ffffff", width=600, align="left"))
    assert not any("year" in p for p in validate_canvas(good))


def test_rejects_poster_with_no_headline(good: dict[str, Any]) -> None:
    good["shapes"][4]["fontSize"] = 30
    assert any("No headline" in p for p in validate_canvas(good))


def test_rejects_text_buried_under_an_opaque_shape(good: dict[str, Any]) -> None:
    good["shapes"].append(shape(
        id="cover", kind="rect", position={"x": 0, "y": 0}, zIndex=99,
        width=1240, height=1754, fill="#0f172a"))
    assert any("hidden behind" in p for p in validate_canvas(good))


def test_rejects_duplicate_ids(good: dict[str, Any]) -> None:
    good["shapes"][3]["id"] = "headline-1"
    assert any("Duplicate shape id" in p for p in validate_canvas(good))


def test_rejects_non_hex_colour(good: dict[str, Any]) -> None:
    good["shapes"][2]["fill"] = "cyan"
    assert any("hex colour" in p for p in validate_canvas(good))


def test_rejects_missing_zindex(good: dict[str, Any]) -> None:
    good["shapes"][4].pop("zIndex")
    assert any("zIndex is required" in p for p in validate_canvas(good))


def test_rejects_empty_shapes(good: dict[str, Any]) -> None:
    good["shapes"] = []
    assert any("non-empty" in p for p in validate_canvas(good))


def test_rejects_unreadable_text(good: dict[str, Any]) -> None:
    good["shapes"] += [
        shape(id="panel", kind="rect", position={"x": 80, "y": 1200}, zIndex=10,
              width=1080, height=300, fill="#0f172a"),
        shape(id="dim", kind="text", position={"x": 100, "y": 1250}, zIndex=11,
              text="Barely visible", fontSize=70, fontFamily="Inter, sans-serif",
              fill="#1e293b", width=800, align="left"),
    ]
    assert any("poor contrast" in p for p in validate_canvas(good))


def test_contrast_ratio_matches_wcag() -> None:
    # Known values: white on near-black is ~17.9:1, and a colour against
    # itself is exactly 1:1.
    assert contrast_ratio("#ffffff", "#0f172a") == pytest.approx(17.85, abs=0.05)
    assert contrast_ratio("#ffffff", "#ffffff") == pytest.approx(1.0)
    assert contrast_ratio("not-a-colour", "#fff") is None


def test_a_full_bleed_photo_does_not_suppress_contrast_checks(good: dict[str, Any]) -> None:
    """Regression: the background lookup used to bail on the first lower-z image
    it saw anywhere, so the hero photo silently disabled contrast checking for
    every text shape on the poster."""
    good["shapes"] += [
        shape(id="panel", kind="rect", position={"x": 80, "y": 1200}, zIndex=10,
              width=1080, height=300, fill="#ffffff"),
        shape(id="dim", kind="text", position={"x": 100, "y": 1250}, zIndex=11,
              text="Invisible", fontSize=70, fontFamily="Inter, sans-serif",
              fill="#fafafa", width=800, align="left"),
    ]
    problems = validate_canvas(good)
    assert any("dim" in p and "poor contrast" in p for p in problems)


# --------------------------------------------------------------------------- #
# Supplied images (uploaded or AI-generated)
# --------------------------------------------------------------------------- #

SUPPLIED = {
    "url": "/api/assets/templates/" + "a" * 32 + ".jpg",
    "width": 1600.0,
    "height": 1200.0,   # 4:3
    "description": "our pharmacist at the counter",
}


def _with_supplied(doc: dict[str, Any], **overrides: Any) -> dict[str, Any]:
    """Swap the stock hero for the supplied image, 4:3 by default."""
    hero = doc["shapes"][0]
    hero["blobKey"] = SUPPLIED["url"]
    hero["width"], hero["height"] = 1240, 930      # 4:3, full-bleed width
    doc["shapes"][1]["width"], doc["shapes"][1]["height"] = 1240, 930
    hero.update(overrides)
    return doc


def test_supplied_image_is_allowed(good: dict[str, Any]) -> None:
    """It isn't in the curated library, so it only passes when declared."""
    doc = _with_supplied(good)
    assert validate_canvas(doc, supplied_image=SUPPLIED) == []


def test_supplied_image_rejected_when_not_declared(good: dict[str, Any]) -> None:
    doc = _with_supplied(good)
    assert any("approved photo list" in p for p in validate_canvas(doc))


def test_rejects_a_stretched_supplied_image(good: dict[str, Any]) -> None:
    """A 4:3 photo forced into a portrait panel distorts everyone in it."""
    doc = _with_supplied(good, width=1240, height=1600)
    problems = validate_canvas(doc, supplied_image=SUPPLIED)
    assert any("stretched" in p for p in problems)
    # The message has to say what to use instead, or the repair round is a guess.
    assert any("930" in p for p in problems)


def test_allows_minor_aspect_rounding(good: dict[str, Any]) -> None:
    """Models snap to tidy numbers; 1240x920 is 4:2.97 and visually identical."""
    doc = _with_supplied(good, width=1240, height=920)
    assert validate_canvas(doc, supplied_image=SUPPLIED) == []


def test_supplied_image_may_bleed_off_the_page(good: dict[str, Any]) -> None:
    """Scaling both axes to cover more area is the correct fix for coverage."""
    doc = _with_supplied(good, width=1500, height=1125)
    assert validate_canvas(doc, supplied_image=SUPPLIED) == []


def test_rejects_a_design_that_omits_the_supplied_image(good: dict[str, Any]) -> None:
    """The bug this pins: a poster generated *with* a photograph that simply
    doesn't contain it. Every other supplied-image rule sits inside
    `if kind == "image"`, so nothing fired and the omission only surfaced when
    the emailed PDF arrived without a picture."""
    good["shapes"] = [s for s in good["shapes"] if s["kind"] != "image"]
    problems = validate_canvas(good, supplied_image=SUPPLIED)
    assert any("supplied photograph is missing" in p for p in problems)
    assert any("no image at all" in p for p in problems)


def test_rejects_an_image_shape_that_uses_the_wrong_picture(good: dict[str, Any]) -> None:
    """An image is present but it's a stock photo, not the one supplied."""
    problems = validate_canvas(good, supplied_image=SUPPLIED)
    assert any("supplied photograph is missing" in p for p in problems)
    assert any("none use it" in p for p in problems)


def test_rejects_the_supplied_image_used_twice(good: dict[str, Any]) -> None:
    doc = _with_supplied(good)
    duplicate = dict(doc["shapes"][0])
    duplicate["id"] = "hero-2"
    duplicate["zIndex"] = 3
    doc["shapes"].append(duplicate)
    assert any("appears 2 times" in p for p in validate_canvas(doc, supplied_image=SUPPLIED))


def test_no_image_required_when_none_was_supplied(good: dict[str, Any]) -> None:
    """A typographic poster is perfectly valid when no photograph was given."""
    good["shapes"] = [s for s in good["shapes"] if s["kind"] != "image"]
    assert validate_canvas(good) == []


def test_stock_photos_are_not_aspect_checked(good: dict[str, Any]) -> None:
    """Stock images are interchangeable, so cropping them freely is fine.

    Note there is no `supplied_image` here. An earlier version of this test
    passed one while the canvas used a stock photo — which the "supplied
    photograph must actually be used" rule now correctly rejects. The aspect
    exemption is about stock imagery, so the fixture should not claim an image
    was supplied.
    """
    good["shapes"][0]["width"], good["shapes"][0]["height"] = 1240, 400
    assert validate_canvas(good) == []


# --------------------------------------------------------------------------- #
# The real gallery
# --------------------------------------------------------------------------- #

#: Canvas methods the extractor could not read. Filled in by _seeded_templates
#: so the completeness check can name them rather than just report a count.
_skipped: list[str] = []


def _seeded_templates() -> list[tuple[str, dict[str, Any]]]:
    """Extract every canvas from TemplateSeeder.cs.

    Split on method boundaries rather than balancing parentheses — the poster
    copy contains "(" and ")".
    """
    src = SEEDER.read_text(encoding="utf-8")
    consts = dict(re.findall(r'private const string (\w+)\s*=\s*"([^"]+)"', src))
    starts = [
        (m.start(), m.group(1))
        for m in re.finditer(r"private static string (\w+)\(\) => Canvas\(", src)
    ]

    out: list[tuple[str, dict[str, Any]]] = []
    _skipped.clear()
    for i, (pos, _method) in enumerate(starts):
        end = starts[i + 1][0] if i + 1 < len(starts) else len(src)
        body = src[pos:end]
        name = re.search(r'name:\s*"([^"]*)"', body)
        bg = re.search(r'background:\s*"([^"]*)"', body)
        # `\$*`, not `\$?\$?`. A raw string's dollar count sets how many braces
        # open an interpolation hole, so a template carrying literal
        # `{{pharmacy_name}}` copy needs `$$$"""`. Six acquired exactly that when
        # the contact blocks became tokens — and this pattern, which allowed at
        # most two, silently stopped extracting them. The gallery check went on
        # passing over the eighteen it could still see.
        raw = re.search(r'shapes:\s*\$*"""(.*?)"""', body, re.DOTALL)
        if not (name and bg and raw):
            _skipped.append(_method)
            continue
        text = raw.group(1)
        for key, value in consts.items():
            text = text.replace("{{" + key + "}}", value)
        out.append((name.group(1), {
            "schemaVersion": 1, "id": "x", "ownerId": "system",
            "name": name.group(1), "width": 1240, "height": 1754,
            "background": bg.group(1),
            "viewport": {"pan": {"x": 0, "y": 0}, "zoom": 1},
            "shapes": json.loads(text),
        }))
    return out


@pytest.mark.skipif(not SEEDER.exists(), reason="API source not available")
def test_seeded_gallery_is_accepted() -> None:
    """The hand-authored gallery is the quality bar — the rules must not fight it.

    One known exception: 'New Year Health Check' uses a deliberately ghosted
    year numeral at 1.5:1, which is genuinely faint. That is left flagged rather
    than weakening a rule that catches real invisibility.
    """
    templates = _seeded_templates()

    # Every Canvas() method, not "at least twenty".
    #
    # A floor cannot tell "the gallery grew" from "the extractor quietly
    # stopped understanding six of them" — which is what happened, and the six
    # it lost were the most recently edited. A test that silently narrows its
    # own coverage is the failure this whole file exists to avoid.
    declared = len(re.findall(
        r"private static string \w+\(\) => Canvas\(",
        SEEDER.read_text(encoding="utf-8"),
    ))

    assert len(templates) == declared, (
        f"extracted {len(templates)} of {declared} Canvas() methods; "
        f"could not read: {', '.join(_skipped) or 'none'}"
    )

    rejected = {
        name: validate_canvas(doc)
        for name, doc in templates
        if validate_canvas(doc)
    }
    assert set(rejected) <= {"New Year Health Check"}, (
        "validator rejected hand-authored templates: "
        + json.dumps({k: v[:2] for k, v in rejected.items()}, indent=2)
    )


def test_brand_paths_are_accepted(good: dict[str, Any]) -> None:
    """The booking QR and logo are our own routes, resolved per pharmacy at render.

    A house-style design always carries the QR slot; refusing it as an
    "unapproved photo" would fail every generated poster.
    """
    doc = copy.deepcopy(good)
    for path in ("/api/brand-kit/booking-qr", "/api/brand-kit/logo"):
        doc["shapes"].append({"id": path.rsplit("/", 1)[1], "kind": "image", "blobKey": path,
                              "position": {"x": 900, "y": 1500}, "width": 170, "height": 170,
                              "rotation": 0, "scale": {"x": 1, "y": 1}, "opacity": 1,
                              "draggable": True, "zIndex": 99})
    assert not [p for p in validate_canvas(doc) if "blobKey" in p]


def test_any_other_relative_path_is_still_refused(good: dict[str, Any]) -> None:
    good["shapes"][0]["blobKey"] = "/api/brand-kit/../admin/secret"
    assert any("blobKey" in p for p in validate_canvas(good))

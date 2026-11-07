"""The AI designer end to end, with a fake model and fake photo providers.

Only the model and the network are fake: plan parsing, photo choice, baking,
composition, the house checks and the validator all run for real. The two
briefs the house style was asked for — a bold flu clinic and a trusted local
pharmacy in a circle portrait — must come back as house-set posters. "Bold" is
a tone: it sets larger type in one of the four layouts, never a fifth.
"""

from __future__ import annotations

import asyncio
import base64
import io
import json

import pytest
from PIL import Image

from app.chains import house_style as hs
from app.chains import poster_imagery
from app.chains import stock_photos as stock
from app.chains import template_designer as td


class _Reply:
    def __init__(self, content: str):
        self.content = content


class FakeModel:
    """Answers each call with the next plan, and keeps what it was told."""

    def __init__(self, *plans: dict):
        self.replies = [json.dumps(p) for p in plans]
        self.calls: list[list] = []

    async def ainvoke(self, messages):
        self.calls.append(list(messages))
        return _Reply(self.replies[min(len(self.calls) - 1, len(self.replies) - 1)])


FLU = {"archetype": "circle-portrait", "name": "Walk-in Flu Clinic", "eyebrow": "Flu season",
       "headline": "Walk-in flu clinic", "lede": "Protect yourself this winter.",
       "items": ["No appointment needed", "Quick and simple", "Ask if you're eligible"],
       "footer": "Flu can be serious. Protect yourself.",
       "photo_query": "pharmacist giving flu vaccine"}

TRUSTED = {"archetype": "circle-portrait", "name": "Trusted Local Pharmacy",
           "eyebrow": "Your local pharmacy team", "headline": "Trusted care, every visit",
           "lede": "Friendly, qualified pharmacists who take the time to listen.",
           "items": ["Expert advice, no appointment", "Prescriptions ready on time",
                     "Quick, private health checks"],
           "photo_query": "smiling pharmacist portrait"}


def _jpeg(w=1600, h=1067) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (w, h), (150, 110, 90)).save(buf, "JPEG")
    return buf.getvalue()


class Providers:
    """Records every call to the stock and AI providers."""

    def __init__(self, monkeypatch, *, ai: bool = False, results: int = 3):
        self.searches: list[tuple] = []
        self.fetches: list[str] = []
        self.generated: list[tuple] = []
        self.results = results

        async def search(query, orientation=None, per_page=15):
            self.searches.append((query, orientation))
            return [{"id": str(1000 + i), "thumbnailUrl": "https://images.example/t.jpg",
                     "width": 1600, "height": 1067, "photographer": "", "alt": ""}
                    for i in range(self.results)]

        async def fetch(photo_id):
            self.fetches.append(photo_id)
            return _jpeg(), "image/jpeg", {"width": 1600, "height": 1067, "alt": "", "photographer": ""}

        async def generate(description, aspect="hero", timeout_s=120, centred=False):
            self.generated.append((description, aspect))
            self.centred = centred
            return _jpeg(1024, 1024), "image/png"

        monkeypatch.setattr(stock, "search", search)
        monkeypatch.setattr(stock, "fetch", fetch)
        monkeypatch.setattr(poster_imagery, "generate_poster_image", generate)
        monkeypatch.setattr(poster_imagery, "is_configured", lambda: ai)


def run(model, prompt="walk-in flu clinic, bold", **kw):
    """(canvas, repair and photo notes together, assets) — most tests want either."""
    canvas, notes, assets, photo_notes = asyncio.run(
        td.generate_template(prompt, "owner-1", llm=model, **kw))
    return canvas, notes + photo_notes, assets


def _house_poster(canvas: dict) -> None:
    """What every generated poster must be: the house set's rules."""
    assert canvas["width"] == 1240 and canvas["height"] == 1754
    assert hs.house_check(canvas) == []
    texts = " ".join(s["text"] for s in canvas["shapes"] if s["kind"] == "text")
    for token in ("{{pharmacy_name}}", "{{phone}}", "{{address}}"):
        assert texts.count(token) == 1
    assert sum(1 for s in canvas["shapes"] if s["id"] == "pharmacy-booking-qr") == 1
    assert max(s["fontSize"] for s in canvas["shapes"] if s["kind"] == "text") >= 60


def test_flu_brief_comes_back_bold_house_style_with_a_stock_photo(monkeypatch) -> None:
    p = Providers(monkeypatch)
    canvas, notes, assets = run(FakeModel(FLU), photo={"mode": "stock"})

    _house_poster(canvas)
    assert _layout(canvas) == "circle-portrait"
    assert canvas["name"] == "Walk-in Flu Clinic"
    # Searched with the model's query, then fetched BY ID — never by a URL.
    assert p.searches and p.searches[0][0] == "pharmacist giving flu vaccine"
    assert p.fetches == ["1000"]
    assert p.generated == []
    [asset] = assets
    assert asset["key"] == hs.PHOTO_KEY and asset["source"] == "stock"
    assert asset["content_type"] == "image/png"  # a circle portrait
    img = Image.open(io.BytesIO(base64.b64decode(asset["data_base64"])))
    assert img.mode == "RGBA" and img.getpixel((0, 0))[3] == 0


def test_trusted_pharmacy_brief_is_a_circle_portrait_around_the_upload(monkeypatch) -> None:
    p = Providers(monkeypatch)
    upload = base64.b64encode(_jpeg(900, 1400)).decode()
    canvas, _, assets = run(FakeModel(TRUSTED), prompt="trusted local pharmacy, circle portrait",
                            photo={"mode": "upload", "data_base64": upload,
                                   "description": "our pharmacist at the counter"})

    _house_poster(canvas)
    ring = [s for s in canvas["shapes"] if s["id"] == "photo-ring"]
    assert ring and ring[0]["fill"] == hs.TEAL
    photo = [s for s in canvas["shapes"] if s.get("blobKey") == hs.PHOTO_KEY]
    assert len(photo) == 1 and (photo[0]["width"], photo[0]["height"]) == (660, 660)
    assert assets[0]["source"] == "upload"
    # The upload is used, and nothing is fetched or generated alongside it.
    assert p.searches == [] and p.fetches == [] and p.generated == []


def test_the_model_is_told_about_the_upload(monkeypatch) -> None:
    Providers(monkeypatch)
    model = FakeModel(TRUSTED)
    run(model, photo={"mode": "upload", "data_base64": base64.b64encode(_jpeg()).decode(),
                      "description": "two pharmacists smiling"})
    brief = model.calls[0][1].content
    assert "two pharmacists smiling" in brief


def test_no_photo_is_a_geometric_house_design(monkeypatch) -> None:
    p = Providers(monkeypatch)
    canvas, _, assets = run(FakeModel(TRUSTED), photo={"mode": "none"})
    _house_poster(canvas)
    assert assets == []
    assert not [s for s in canvas["shapes"] if s["kind"] == "image" and s["id"] != "pharmacy-booking-qr"]
    assert p.searches == p.fetches == p.generated == []


def test_a_chosen_stock_photo_is_fetched_by_its_id(monkeypatch) -> None:
    p = Providers(monkeypatch)
    _, _, assets = run(FakeModel(FLU), photo={"mode": "stock", "stock_photo_id": "4242"})
    assert p.fetches == ["4242"] and p.searches == []
    assert assets[0]["source"] == "stock"


def test_ai_mode_without_a_configured_model_is_an_error_not_a_stock_photo(monkeypatch) -> None:
    p = Providers(monkeypatch, ai=False)
    with pytest.raises(td.TemplateGenerationError) as err:
        run(FakeModel(FLU), photo={"mode": "ai", "description": "nurse with a vaccine"})
    assert "not set up" in str(err.value)
    assert p.searches == [] and p.fetches == []


def test_ai_mode_generates_one_photo_for_the_slot(monkeypatch) -> None:
    p = Providers(monkeypatch, ai=True)
    _, _, assets = run(FakeModel(FLU), photo={"mode": "ai", "description": "nurse with a vaccine"})
    assert p.generated == [("nurse with a vaccine", "portrait")]
    assert p.centred, "a circle portrait asks for a centred subject"
    assert p.fetches == []
    assert assets[0]["source"] == "ai"


def test_a_clear_request_in_the_brief_uses_ai_when_it_exists(monkeypatch) -> None:
    p = Providers(monkeypatch, ai=True)
    run(FakeModel(FLU), prompt="flu clinic poster, generate a photo of a nurse",
        photo={"mode": "stock"})
    assert len(p.generated) == 1 and p.fetches == []


def test_a_clear_request_without_ai_says_stock_was_used(monkeypatch) -> None:
    p = Providers(monkeypatch, ai=False)
    _, notes, _ = run(FakeModel(FLU), prompt="flu clinic poster, generate a photo of a nurse",
                      photo={"mode": "stock"})
    assert p.fetches and p.generated == []
    assert any("AI images are not set up" in n for n in notes)


def test_no_stock_match_designs_photo_free_and_says_so(monkeypatch) -> None:
    Providers(monkeypatch, results=0)
    canvas, notes, assets = run(FakeModel(FLU), photo={"mode": "stock"})
    _house_poster(canvas)
    assert assets == []
    assert any("photo-free" in n for n in notes)


def test_copy_that_does_not_fit_is_sent_back_and_repaired(monkeypatch) -> None:
    Providers(monkeypatch)
    # Two lines across the diagonal split cannot take this at any house size.
    too_long = FLU | {"archetype": "diagonal-split", "items": [],
                      "headline": "Seasonal flu vaccinations available for everyone today"}
    model = FakeModel(too_long, FLU)
    canvas, notes, _ = run(model, photo={"mode": "none"})
    assert len(model.calls) == 2
    feedback = model.calls[1][-1].content
    assert "headline" in feedback and "does not fit" in feedback
    assert any("needed repair" in n for n in notes)
    _house_poster(canvas)


def test_forbidden_copy_is_sent_back(monkeypatch) -> None:
    Providers(monkeypatch)
    bad = FLU | {"lede": "Visit your pharmacy for 20% off"}
    model = FakeModel(bad, FLU)
    run(model, photo={"mode": "none"})
    feedback = model.calls[1][-1].content
    assert "Your Pharmacy" in feedback and "percentage" in feedback


def test_gives_up_rather_than_returning_a_broken_design(monkeypatch) -> None:
    Providers(monkeypatch)
    bad = FLU | {"headline": "x" * 59}
    with pytest.raises(td.TemplateGenerationError) as err:
        run(FakeModel(bad), photo={"mode": "none"})
    assert err.value.problems


def test_the_callers_archetype_wins(monkeypatch) -> None:
    Providers(monkeypatch)
    canvas, _, _ = run(FakeModel(FLU), archetype="checklist", photo={"mode": "none"})
    assert canvas["background"] == hs.SKY


def test_an_unreadable_upload_is_refused_before_the_model_is_called(monkeypatch) -> None:
    Providers(monkeypatch)
    model = FakeModel(FLU)
    with pytest.raises(td.TemplateGenerationError):
        run(model, photo={"mode": "upload", "data_base64": "not base64!!"})
    assert model.calls == []


def test_asks_for_ai_photo() -> None:
    assert td.asks_for_ai_photo("Generate a photo of a pharmacist")
    assert td.asks_for_ai_photo("with an AI image of a clinic")
    assert not td.asks_for_ai_photo("photo-led flu poster")


def test_photo_notes_are_kept_apart_from_repair_notes(monkeypatch) -> None:
    """The admin console reads repair notes as "this design needed correcting";
    a photo note there would mislabel it. A pharmacy sees only photo notes."""
    Providers(monkeypatch, results=0)
    _, notes, _, photo_notes = asyncio.run(
        td.generate_template("walk-in flu clinic", "owner-1", llm=FakeModel(FLU), photo={"mode": "stock"}))
    assert notes == []
    assert any("photo-free" in n for n in photo_notes)


def test_a_list_left_out_by_the_layout_is_said_out_loud(monkeypatch) -> None:
    Providers(monkeypatch)
    plan = FLU | {"archetype": "wave-footer"}
    _, _, assets, photo_notes = asyncio.run(
        td.generate_template("meet the team", "owner-1", llm=FakeModel(plan), photo={"mode": "stock"}))
    assert assets
    assert any("left out" in n for n in photo_notes)


def test_a_circle_portrait_prompt_centres_the_subject() -> None:
    assert "centred" in poster_imagery.build_prompt("a pharmacist", centred=True)
    assert "off-centre" in poster_imagery.build_prompt("a pharmacist")


# ── "bold" is a tone, not a layout ──────────────────────────────────────────


def _layout(canvas: dict) -> str:
    """Which of the four house layouts a canvas is, by what only it draws."""
    ids = {s["id"] for s in canvas["shapes"]}
    if "diagonal-stripe" in ids:
        return "diagonal-split"
    if "wave-back" in ids:
        return "wave-footer"
    if canvas["background"] == hs.SKY:
        return "checklist"
    if "photo-ring" in ids and canvas["background"] == hs.WHITE:
        return "circle-portrait"
    return "unknown"


def _headline_size(canvas: dict) -> float:
    return next(s["fontSize"] for s in canvas["shapes"] if s["id"] == "headline")


def test_a_bold_flu_clinic_brief_gets_a_house_layout_not_a_bold_one(monkeypatch) -> None:
    """Even when the model still answers with the retired "bold" layout."""
    Providers(monkeypatch)
    legacy = FLU | {"archetype": "bold"}
    canvas, _, _ = run(FakeModel(legacy), prompt="bold flu clinic", photo={"mode": "none"})

    _house_poster(canvas)
    assert _layout(canvas) in hs.ARCHETYPES
    assert canvas["background"] != hs.NAVY
    # The retired layout's white foot panel and call-to-action line.
    assert not {"foot", "foot-title"} & {s["id"] for s in canvas["shapes"]}


def test_bold_in_the_brief_means_larger_type_in_the_same_layout(monkeypatch) -> None:
    Providers(monkeypatch)
    calm, _, _ = run(FakeModel(FLU), prompt="flu clinic", photo={"mode": "none"})
    bold, _, _ = run(FakeModel(FLU), prompt="bold flu clinic", photo={"mode": "none"})

    assert _layout(bold) == _layout(calm) == "circle-portrait"
    assert _headline_size(bold) > _headline_size(calm)


def test_bold_asked_for_as_the_layout_is_a_tone_not_an_error(monkeypatch) -> None:
    Providers(monkeypatch)
    canvas, _, _ = run(FakeModel(FLU | {"archetype": "checklist"}), prompt="flu clinic",
                       archetype="bold", photo={"mode": "none"})
    assert _layout(canvas) == "checklist"


def test_the_prompt_offers_four_layouts_and_calls_bold_a_tone() -> None:
    prompt = td._system_prompt()
    for name in ("circle-portrait", "diagonal-split", "wave-footer", "checklist"):
        assert f"- {name}:" in prompt
    assert "- bold:" not in prompt
    assert '"Bold" is a tone' in prompt


@pytest.mark.parametrize("kw", [
    {"prompt": "hay fever advice, checklist bar"},
    {"prompt": "hay fever advice", "archetype": "checklist bar"},
])
def test_a_checklist_bar_brief_still_gets_the_checklist(monkeypatch, kw) -> None:
    Providers(monkeypatch)
    # The model names the layout the way the brief did.
    plan = FLU | {"archetype": "checklist bar"}
    canvas, _, _ = run(FakeModel(plan), photo={"mode": "none"}, **kw)
    assert _layout(canvas) == "checklist"

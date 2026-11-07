"""
The branded end card, and the editor overrides a refine applies to it.

**Why this file exists, specifically.** Every clip the end card builds is
wrapped in a `try/except` that logs "skipped" and returns nothing, because a
missing end card must never fail a render that is otherwise finished and good.
That is the right behaviour and it has now hidden two of my own programming
errors:

  * `.set_opacity()` on a `ColorClip` built from an RGB triple — moviepy 1.0.3
    multiplies `self.mask`, and such a clip has no mask, so it raised
    `AttributeError` every single time;
  * an `int` passed where the signature wanted a `(top, bottom)` pair, so
    unpacking threw before a clip was ever built.

Both would have shipped. Both produced a log line reading like a deliberate
skip. Neither was visible without *running* the thing.

So these tests run it, against stub clips that reproduce moviepy 1.0.3's real
constraints rather than accepting any call. A stub that says yes to everything
would have passed while both bugs were present, which makes it worse than no
test — its name still appears in the output.
"""

from __future__ import annotations

import pytest


# ── moviepy stand-ins ──────────────────────────────────────────────────
#
# Modelled on moviepy 1.0.3, and strict where 1.0.3 is strict:
#   * ColorClip from an RGB triple has no mask, so set_opacity fails on it;
#   * crossfadein requires a duration;
#   * TextClip requires a string.
# Every one of those rules is a bug this file has already caught.


class _Clip:
    def __init__(self, kind: str, **kw):
        self.kind = kind
        self.kw = kw
        self.mask = None
        self.duration = None
        self.start = None
        self.pos = None

    def _copy(self) -> "_Clip":
        clone = _Clip.__new__(_Clip)
        clone.__dict__.update(self.__dict__)
        return clone

    def set_start(self, value):
        c = self._copy(); c.start = value; return c

    def set_duration(self, value):
        c = self._copy(); c.duration = value; return c

    def set_position(self, value):
        c = self._copy(); c.pos = value; return c

    def set_mask(self, mask):
        c = self._copy(); c.mask = mask; return c

    def set_opacity(self, value):
        if self.mask is None:
            raise AttributeError("'NoneType' object has no attribute 'fl_image'")
        c = self._copy(); return c

    def crossfadein(self, duration):
        if self.duration is None:
            raise ValueError("crossfadein requires a duration")
        c = self._copy()
        if c.mask is None:
            c.mask = _Clip("auto-mask")
        return c


def _color_clip(size=None, color=None, ismask=False, duration=None):
    clip = _Clip("ColorClip", size=size, color=color, ismask=ismask)
    clip.duration = duration
    return clip


def _text_clip(txt, **kw):
    if not isinstance(txt, str):
        raise ValueError("TextClip needs a string")
    clip = _Clip("TextClip", txt=txt, **kw)
    clip.mask = _Clip("text-mask")
    return clip


@pytest.fixture
def nodes(monkeypatch):
    """`video_agent.nodes` with only its clip constructors replaced.

    **Not `sys.modules` stubbing.** The first version of this fixture replaced
    `moviepy.editor` in `sys.modules` and then imported `nodes` — which works
    only if nothing imported `nodes` first. Something does: `test_health.py`
    imports `app.main`, that starts the worker's lifespan, and the worker imports
    `nodes`. Pytest imports every test module during collection, so by the time
    this fixture ran the real `TextClip` was already bound and the tests hit real
    ImageMagick, failing on a font this machine does not have.

    `nodes` binds `TextClip` and `ColorClip` as module attributes at import
    (`from moviepy.editor import ...`), so patching them on the module is
    immune to import order. Which is the property a fixture like this needs.
    """
    import app.video_agent.nodes as module

    monkeypatch.setattr(module, "ColorClip", _color_clip)
    monkeypatch.setattr(module, "TextClip", _text_clip)
    return module


@pytest.fixture
def make_ctx():
    from app.video_agent.context import BrandContext, UserContext
    from app.video_agent.editor_options import EditorOptions

    def build(brand: bool = True, **options):
        pharmacy = (
            BrandContext(
                name="Riverside Pharmacy",
                address="12 Riverside Walk, M1 4AB",
                phone="0161 496 0000",
                booking_url="https://book.riverside.example",
                usable=True,
            )
            if brand
            else None
        )
        return UserContext(
            user_id="owner-1",
            job_id="job-1",
            topic="flu vaccination",
            brand=pharmacy,
            editor_options=EditorOptions(**options) if options else None,
        )

    return build


VERTICAL = (1080, 1920)
CAPTIONS_HIGH = (345, 862)
CAPTIONS_MIDDLE = (864, 1381)


def _texts(clips):
    return [c.kw.get("txt") for c in clips if c.kind == "TextClip"]


def _band(clips):
    return next((c for c in clips if c.kind == "ColorClip" and not c.kw["ismask"]), None)


# ── the card renders at all ────────────────────────────────────────────

def test_a_branded_job_actually_produces_clips(nodes, make_ctx):
    """The test both earlier bugs would have failed.

    Neither was a wrong position or a wrong colour — both meant the function
    returned nothing while reporting a skip. Counting the clips is the whole
    assertion.
    """
    tw, th = VERTICAL
    clips = nodes._brand_end_card(
        make_ctx(), tw, th, 15.0, "DejaVu-Sans", CAPTIONS_HIGH
    )

    assert clips, "the end card produced no clips for a pharmacy that has a name"
    assert "Riverside Pharmacy" in _texts(clips)


def test_the_band_is_translucent_without_set_opacity(nodes, make_ctx):
    """Opacity has to come from an explicit mask.

    moviepy 1.0.3's `set_opacity` multiplies the clip's existing mask, and a
    ColorClip built from an RGB triple has none — the array is three channels,
    so ImageClip never makes one. The stub raises exactly as moviepy does.
    """
    tw, th = VERTICAL
    clips = nodes._brand_end_card(
        make_ctx(), tw, th, 15.0, "DejaVu-Sans", CAPTIONS_HIGH
    )

    band = _band(clips)
    assert band is not None
    assert band.mask is not None, "the band has no mask, so it will not be translucent"


# ── what it shows ──────────────────────────────────────────────────────

def test_the_booking_link_is_preferred_and_shortened(nodes, make_ctx):
    tw, th = VERTICAL
    clips = nodes._brand_end_card(
        make_ctx(), tw, th, 15.0, "DejaVu-Sans", CAPTIONS_HIGH
    )

    assert "book.riverside.example" in _texts(clips), (
        "the scheme should come off — nobody types https://, and it costs "
        "eight characters of a line that has about forty-eight"
    )


@pytest.mark.parametrize(
    "choice,expected",
    [
        ("phone", "0161 496 0000"),
        ("address", "12 Riverside Walk, M1 4AB"),
        ("booking", "book.riverside.example"),
    ],
)
def test_a_refine_can_choose_the_contact_line(nodes, make_ctx, choice, expected):
    tw, th = VERTICAL
    clips = nodes._brand_end_card(
        make_ctx(end_card_detail=choice), tw, th, 15.0, "DejaVu-Sans", CAPTIONS_HIGH
    )

    assert expected in _texts(clips)


def test_detail_none_leaves_the_name_standing_alone(nodes, make_ctx):
    tw, th = VERTICAL
    clips = nodes._brand_end_card(
        make_ctx(end_card_detail="none"), tw, th, 15.0, "DejaVu-Sans", CAPTIONS_HIGH
    )

    assert _texts(clips) == ["Riverside Pharmacy"]


def test_a_refine_can_switch_the_card_off(nodes, make_ctx):
    tw, th = VERTICAL
    clips = nodes._brand_end_card(
        make_ctx(end_card=False), tw, th, 15.0, "DejaVu-Sans", CAPTIONS_HIGH
    )

    assert clips == []


# ── when it stays away ─────────────────────────────────────────────────

def test_no_brand_means_no_card(nodes, make_ctx):
    tw, th = VERTICAL
    assert nodes._brand_end_card(
        make_ctx(brand=False), tw, th, 15.0, "DejaVu-Sans", CAPTIONS_HIGH
    ) == []


def test_a_short_clip_does_not_give_three_seconds_away(nodes, make_ctx):
    tw, th = VERTICAL
    assert nodes._brand_end_card(
        make_ctx(), tw, th, 4.0, "DejaVu-Sans", CAPTIONS_HIGH
    ) == []


def test_a_wrong_argument_type_is_reported_as_a_bug(nodes, make_ctx, caplog):
    """The contract check, and that it is loud.

    An int here used to be swallowed by the general `except` and logged as a
    skip — indistinguishable from a pharmacy that has no name. It is now an
    error-level line with the word BUG in it, still without failing the render.
    """
    tw, th = VERTICAL
    with caplog.at_level("ERROR"):
        clips = nodes._brand_end_card(make_ctx(), tw, th, 15.0, "DejaVu-Sans", 345)

    assert clips == []
    # getMessage(), not .message — the latter only exists as a side effect of a
    # handler having formatted the record, so it is set under pytest's caplog
    # and absent anywhere else. Depending on that makes the assertion a
    # property of the runner rather than of the code.
    assert any("BUG" in record.getMessage() for record in caplog.records)


# ── geometry ───────────────────────────────────────────────────────────

@pytest.mark.parametrize("frame", [(1080, 1920), (1920, 1080), (1080, 1080)])
@pytest.mark.parametrize("position", [None, "top", "middle", "bottom"])
@pytest.mark.parametrize("scale", [0.6, 1.0, 1.6])
def test_the_card_never_lands_on_the_captions(nodes, make_ctx, frame, position, scale):
    """Every combination a refine can produce, not just the default.

    The first version placed the band opposite the caption's *top* edge, which
    worked for captions pinned high or low and put the band straight through
    captions in the middle of the frame. A refine can now ask for exactly that,
    so the case is reachable rather than theoretical.
    """
    tw, th = frame
    options = {}
    if position:
        options["caption_position"] = position
    if scale != 1.0:
        options["caption_scale"] = scale
    ctx = make_ctx(**options)

    caption_top = nodes._caption_band(ctx, tw, th)
    caption_size = max(1, int(th * 0.045 * scale))
    span = (caption_top, caption_top + int(caption_size * 3.75))

    band_height = int(th * 0.22)
    band_top = nodes._end_card_top(th, band_height, span)

    clear = band_top + band_height <= span[0] or band_top >= span[1]
    assert clear, (
        f"end card {band_top}-{band_top + band_height} overlaps captions "
        f"{span[0]}-{span[1]} at {tw}x{th}, position={position}, scale={scale}"
    )

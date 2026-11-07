"""How many words of narration a clip of a given length can hold.

**Why this is a test and not a constant.** The scriptwriter's system prompt
carried "140-160 words for a 60s Short" while the user prompt asked for a
15-second clip. Given a fixed word count and a contradictory duration, the model
wrote for something in between: a measured render produced **26.7 seconds** of
narration for a clip the product calls 15 seconds, on every screen and in the
domain default.

Nothing downstream trims it — the finished video is exactly as long as the
narration — so those extra twelve seconds were paid for in b-roll fetched,
frames composited, seconds encoded and megabytes stored. The encode alone was
91 seconds of a 208-second render, and the only symptom anyone saw was "videos
take a while".

Two facts that must agree, kept in two places, drifting apart. The same shape as
the seven copies of a month-start expression and the three copies of an
instruction limit. So the count is derived from the duration, and this pins the
derivation.
"""

from app.video_agent.nodes import NARRATION_WPM, narration_word_budget


def test_sixty_seconds_reproduces_the_old_hand_written_constant() -> None:
    """The evidence that 145 wpm is the right rate, not a guess.

    The prompt said "140-160 words for a 60s Short" and somebody arrived at that
    by listening to the output. If the derivation could not reproduce it at 60
    seconds, the rate would be wrong and every other length with it.
    """
    low, high = narration_word_budget(60)

    assert high == 160, "the old ceiling was 160 words for 60 seconds"
    assert 120 <= low <= 140


def test_a_fifteen_second_clip_gets_a_fifteen_second_script() -> None:
    """The product's actual duration, and the case that was broken.

    ~36 words, not ~150. At the old budget the narration ran 27 seconds.
    """
    low, high = narration_word_budget(15)

    assert 28 <= low <= 34, low
    assert 36 <= high <= 44, high


def test_the_budget_scales_with_the_clip() -> None:
    """Derived, so a change to clip length carries the script with it.

    Asserted as a ratio rather than exact doubling: the budget is rounded to
    whole words at each end, so 30s and 60s differ from exact proportionality by
    a word or two. Demanding exactness here would be a test pinning the rounding
    rather than the property, and it would fail the next time the rate moves by
    a decimal.
    """
    fifteen = narration_word_budget(15)[1]
    thirty = narration_word_budget(30)[1]
    sixty = narration_word_budget(60)[1]

    assert 1.9 <= thirty / fifteen <= 2.1, (fifteen, thirty)
    assert 1.9 <= sixty / thirty <= 2.1, (thirty, sixty)


def test_the_ceiling_is_close_to_the_nominal_count() -> None:
    """Overshoot is not free and is not trimmed.

    A generous ceiling reads to a model as a target. At 15 seconds the gap
    between nominal and ceiling should be a few words, not tens.
    """
    nominal = round(15 * NARRATION_WPM / 60)
    _, high = narration_word_budget(15)

    assert high - nominal <= 5, (nominal, high)


def test_very_short_clips_still_get_a_usable_script() -> None:
    """A floor, so a 3-second clip does not ask for four words."""
    low, high = narration_word_budget(3)

    assert low >= 10
    assert high >= low


# ── the budget is enforced, not just requested ──────────────────────────

import json  # noqa: E402
from types import SimpleNamespace  # noqa: E402

from app.video_agent import nodes  # noqa: E402
from app.video_agent.context import UserContext  # noqa: E402


def _script(words: int) -> dict:
    text = " ".join(f"w{i}" for i in range(words))
    return {"script": text, "segments": [{"text": text, "start": 0, "end": 15}], "title": "t"}


class _Model:
    def __init__(self, reply):
        self.reply, self.calls = reply, 0

    def invoke(self, _messages):
        self.calls += 1
        if isinstance(self.reply, Exception):
            raise self.reply
        return SimpleNamespace(content=self.reply)


def _with(monkeypatch, reply) -> _Model:
    model = _Model(reply)
    monkeypatch.setattr(nodes, "_llm", lambda *a, **k: model)
    monkeypatch.setattr(nodes, "_meter_llm", lambda _ctx, r: r)
    return model


_CTX = UserContext(job_id="j", user_id="u", topic="weight management")


def test_stage_directions_are_not_counted_as_spoken() -> None:
    assert nodes.spoken_words("Hello there [pause] (smiles) friend.") == 3


def test_a_script_on_budget_is_left_alone(monkeypatch) -> None:
    model = _with(monkeypatch, "{}")
    _, high = narration_word_budget(15)

    assert nodes._within_budget(_CTX, _script(high), high) == _script(high)
    assert model.calls == 0, "no extra model call when the budget is kept"


def test_an_overrun_script_is_tightened_once(monkeypatch) -> None:
    """Production: 22.8s for a 15s clip — ~54 words against a ceiling of 40."""
    _, high = narration_word_budget(15)
    model = _with(monkeypatch, json.dumps(_script(high - 2)))

    result = nodes._within_budget(_CTX, _script(54), high)

    assert nodes.spoken_words(result["script"]) == high - 2
    assert model.calls == 1


def test_a_failed_or_useless_tightening_keeps_the_original(monkeypatch) -> None:
    _, high = narration_word_budget(15)
    for reply in (RuntimeError("provider down"), "not json", json.dumps(_script(60)),
                  json.dumps({"script": "short", "segments": []})):
        _with(monkeypatch, reply)

        assert nodes._within_budget(_CTX, _script(54), high) == _script(54), reply


def test_the_scriptwriter_itself_enforces_the_budget(monkeypatch) -> None:
    # Through the real node: a writer that overruns, then complies when asked.
    _, high = narration_word_budget(15)
    replies = iter([json.dumps(_script(54)), json.dumps(_script(high - 3))])

    class Writer:
        def invoke(self, _messages):
            return SimpleNamespace(content=next(replies))

    monkeypatch.setattr(nodes, "_llm", lambda *a, **k: Writer())
    monkeypatch.setattr(nodes, "_meter_llm", lambda _ctx, r: r)
    ctx = UserContext(job_id="j", user_id="u", topic="weight management", duration_seconds=15)

    out = nodes.make_scriptwriter(ctx)({"topic": "weight management", "facts": []})

    assert nodes.spoken_words(out["script"]) == high - 3


def test_a_tightening_whose_captions_do_not_match_its_words_is_refused(monkeypatch) -> None:
    # The voice speaks `script`; captions come from `segments`. A reply that
    # shortened only the script would caption words nobody says.
    _, high = narration_word_budget(15)
    lopsided = {"script": " ".join(["w"] * (high - 2)),
                "segments": [{"text": " ".join(["w"] * 54), "start": 0, "end": 15}]}
    _with(monkeypatch, json.dumps(lopsided))

    assert nodes._within_budget(_CTX, _script(54), high) == _script(54)


def test_tightening_shortens_segments_rather_than_merging_them() -> None:
    """Merging two segments to fit the word budget removed a shot.

    The tightening pass used to be told to "merge or drop the least important
    sentences", which took a four-shot script to three. A segment is a shot on
    screen; the budget has to be met inside them.
    """
    from app.video_agent.nodes import TIGHTEN_SYS

    assert "merge or drop" not in TIGHTEN_SYS
    assert "same number of segments" in TIGHTEN_SYS


def test_the_writer_is_told_how_many_segments_to_write() -> None:
    from app.video_agent.nodes import SHORTS_SYS

    assert "EXACTLY the number of segments" in SHORTS_SYS

"""On-screen captions: one per beat, and never the same one twice."""

from app.video_agent.nodes import caption_flashes


def _seg(text: str, start: float, end: float) -> dict:
    return {"text": text, "start": start, "end": end}


def test_each_beat_gets_its_first_sentence_upper_cased() -> None:
    flashes = caption_flashes([_seg("Flu season is here. Get protected.", 0, 3)])

    assert flashes == [("FLU SEASON IS HERE", 0.0, 2.5)]


def test_a_caption_already_shown_is_not_shown_again() -> None:
    # The call to action said at the start and again at the close.
    flashes = caption_flashes([
        _seg("Book your flu jab today. It takes minutes.", 0, 3),
        _seg("Protect the people you love.", 3, 6),
        _seg("Book your flu jab today!", 6, 9),
    ])

    assert [text for text, *_ in flashes] == [
        "BOOK YOUR FLU JAB TODAY", "PROTECT THE PEOPLE YOU LOVE"]


def test_repeats_are_matched_ignoring_case_spacing_and_punctuation() -> None:
    flashes = caption_flashes([
        _seg("Book today", 0, 2),
        _seg("  book   TODAY, ", 2, 4),
    ])

    assert len(flashes) == 1


def test_a_beat_with_unreadable_timing_is_skipped_not_fatal() -> None:
    flashes = caption_flashes([
        _seg("First.", 0, 2),
        {"text": "Broken.", "start": "soon", "end": None},
        _seg("Backwards.", 5, 4),
        _seg("Last.", 6, 8),
    ])

    assert [text for text, *_ in flashes] == ["FIRST", "LAST"]


def test_nothing_in_nothing_out() -> None:
    assert caption_flashes(None) == []
    assert caption_flashes([{"text": ""}, "not a dict"]) == []

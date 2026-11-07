"""Re-filling a moment of a finished video, chosen by the pharmacist.

The shot is chosen by tapping it, or by position in a typed request — never
inferred from what the shot is said to show. `parse` validates whatever
arrives, because it crossed two services on the way: a moment this video does
not have, a repeated one, a paragraph where a search belongs.
"""

from app.video_agent.shot_edits import MAX_EDITS, MAX_QUERY_CHARS, ShotEdits, parse


def _reply(edits=None, unsupported=None) -> dict:
    return {"edits": edits or [], "unsupported": unsupported or []}


# ── the request that prompted this ──────────────────────────────────────

def test_the_reported_request_becomes_two_re_fills() -> None:
    """"remove the man image at the end. Add an elder couple walking in a park."

    Two sentences, one operation each, and neither is a deletion — the video is
    as long as its narration, so the man at the end is *replaced* rather than
    cut.
    """
    edits = parse(
        _reply([
            {"index": 5, "query": "elderly couple walking park"},
            {"index": 2, "query": "pharmacist counselling patient"},
        ]),
        segment_count=7,
    )

    assert edits.by_index() == {5: "elderly couple walking park", 2: "pharmacist counselling patient"}
    assert edits.unsupported == []


def test_asking_for_the_words_to_change_is_declared_not_attempted() -> None:
    # Narration is the closed lane. Silently re-cutting the footage instead
    # would report success for a change nobody made.
    edits = parse(_reply(unsupported=["changing what the voice says"]), segment_count=7)

    assert edits.is_empty()
    assert edits.unsupported == ["changing what the voice says"]


def test_part_understood_and_part_not_keeps_both() -> None:
    # The common real request: one thing we can do, one we cannot. Refusing the
    # whole sentence would be wrong; doing half of it silently would be worse.
    edits = parse(
        _reply([{"index": 1, "query": "park bench autumn"}], ["making the video shorter"]),
        segment_count=4,
    )

    assert len(edits.edits) == 1
    assert edits.unsupported == ["making the video shorter"]


# ── replies that name a moment the video does not have ──────────────────

def test_a_moment_beyond_the_end_is_dropped() -> None:
    """The failure that would otherwise re-cut an arbitrary beat.

    A model asked about a seven-moment video will sometimes answer "9". Applying
    that to whichever beat the number happens to hit changes a moment nobody
    mentioned — in a video that was finished and approved.
    """
    assert parse(_reply([{"index": 9, "query": "anything"}]), segment_count=7).is_empty()


def test_a_negative_moment_is_dropped() -> None:
    # -1 indexes the last beat in Python, so this is the one out-of-range value
    # that would silently *work*.
    assert parse(_reply([{"index": -1, "query": "anything"}]), segment_count=7).is_empty()


def test_true_is_not_a_moment() -> None:
    # `True == 1` in Python, so a boolean would quietly edit the second moment.
    assert parse(_reply([{"index": True, "query": "anything"}]), segment_count=7).is_empty()


def test_a_moment_that_is_not_a_number_is_dropped() -> None:
    for index in ("3", 3.5, None, [3], {"index": 3}):
        assert parse(_reply([{"index": index, "query": "x y"}]), segment_count=7).is_empty(), index


# ── replies whose search terms are unusable ─────────────────────────────

def test_an_empty_query_is_dropped_rather_than_searched_for() -> None:
    # Searching for "" returns the stock library's idea of popular, which is
    # unrelated to anything.
    for query in ("", "   ", None, 7):
        assert parse(_reply([{"index": 0, "query": query}]), segment_count=3).is_empty(), query


def test_a_pasted_sentence_is_cut_to_a_searchable_length() -> None:
    long_query = "a really very extremely long description of a scene " * 5

    edits = parse(_reply([{"index": 0, "query": long_query}]), segment_count=3)

    assert len(edits.edits[0].query) <= MAX_QUERY_CHARS


def test_punctuation_is_stripped_from_a_search() -> None:
    edits = parse(_reply([{"index": 0, "query": '"elderly couple", walking!'}]), segment_count=3)

    assert edits.edits[0].query == "elderly couple walking"


def test_hyphens_survive_because_stock_libraries_use_them() -> None:
    edits = parse(_reply([{"index": 0, "query": "walk-in clinic"}]), segment_count=3)

    assert edits.edits[0].query == "walk-in clinic"


# ── replies that ask for too much ───────────────────────────────────────

def test_one_request_cannot_re_cut_the_whole_video() -> None:
    """A runaway reply is a rewrite, and a rewrite is not free.

    Every edit is a stock search and a download. Thirty of them from one
    sentence is a different operation from the one the pharmacist agreed to.
    """
    many = [{"index": i, "query": f"scene {i}"} for i in range(30)]

    edits = parse(_reply(many), segment_count=30)

    assert len(edits.edits) == MAX_EDITS


def test_the_same_moment_named_twice_is_taken_once() -> None:
    edits = parse(
        _reply([
            {"index": 2, "query": "first idea"},
            {"index": 2, "query": "second idea"},
        ]),
        segment_count=5,
    )

    assert len(edits.edits) == 1
    assert edits.edits[0].query == "first idea", "the first answer wins, not the last"


# ── replies that are not replies at all ─────────────────────────────────

def test_nothing_usable_produces_no_edits_rather_than_an_error() -> None:
    for payload in (None, [], "sorry, I cannot help with that", 42, {"edits": "yes"}):
        result = parse(payload, segment_count=5)
        assert isinstance(result, ShotEdits)
        assert result.is_empty(), payload


def test_an_edit_that_is_not_an_object_is_skipped() -> None:
    edits = parse(
        _reply(["remove the man", {"index": 1, "query": "park bench"}]),
        segment_count=4,
    )

    assert edits.by_index() == {1: "park bench"}


def test_unsupported_notes_are_trimmed_and_capped() -> None:
    edits = parse(_reply(unsupported=["x" * 500] + [f"thing {i}" for i in range(20)]),
                  segment_count=3)

    assert len(edits.unsupported) <= MAX_EDITS
    assert all(len(u) <= 200 for u in edits.unsupported)


def test_blank_unsupported_notes_are_dropped() -> None:
    # An empty string would render as a bullet point with nothing after it.
    edits = parse(_reply(unsupported=["", "   ", "making it shorter"]), segment_count=3)

    assert edits.unsupported == ["making it shorter"]


# ── which moments a refine leaves alone ─────────────────────────────────
#
# The selection itself lives inside `_collect_pexels_b_roll`, which needs
# Pexels, a work directory and a UserContext. What is pinned here is the branch
# *order*, read out of the source — the bug it guards against was an ordering
# one, and it is the kind that a passing render hides completely.

def _branch_order() -> list[str]:
    from pathlib import Path
    import re

    src = Path(__file__).resolve().parents[1].joinpath(
        "app", "video_agent", "nodes.py").read_text()
    start = src.index("for i, seg in enumerate(segments):", src.index("def _collect_pexels_b_roll"))
    block = src[start:src.index("candidates = _beat_queries", start)]
    return re.findall(
        r"(if i in user_clips|if override|elif i < len\(previous_aligned\))", block)


def test_a_moment_nobody_mentioned_keeps_the_shot_it_had() -> None:
    """The bug this guards: a refine re-shot the entire video.

    `_pexels_search` takes a random page and shuffles within quality tiers —
    deliberately, so two videos on the same topic do not look identical. The
    consequence on a refine is that re-searching an *unchanged* beat returns
    different footage. Asking to fix the last shot handed back a video in which
    every shot had changed, which is not the change anybody agreed to.
    """
    assert "elif i < len(previous_aligned)" in _branch_order()


def test_a_named_moment_is_re_searched_rather_than_kept() -> None:
    # The override has to be checked *before* the keep-what-we-had branch, or
    # the one moment the pharmacist asked about is the one that never changes.
    order = _branch_order()

    assert order.index("if override") < order.index("elif i < len(previous_aligned)")


def test_a_pinned_photograph_still_wins_over_everything() -> None:
    # A picture the pharmacy supplied is not stock and must not be replaced by
    # a previous stock clip or by a search.
    assert _branch_order()[0] == "if i in user_clips"


def test_a_kept_shot_records_its_reference_so_a_second_edit_can_keep_it_too() -> None:
    """The feature working once and then quietly stopping.

    The worker rewrites `clipSources` from whatever the render reports. A kept
    shot that contributes no record is a reference silently dropped — so the
    *second* shot edit would recover only the beats the first one re-searched
    and re-shoot everything else, with nothing saying why.
    """
    order = _branch_order()
    assert "elif i < len(previous_aligned)" in order

    from pathlib import Path

    block = Path(__file__).resolve().parents[1].joinpath(
        "app", "video_agent", "nodes.py").read_text()
    kept = block[block.index("elif i < len(previous_aligned)"):]
    kept = kept[:kept.index("continue")]

    assert "own_records[i] = previous_sources[i]" in kept, (
        "a kept shot must contribute its source record, or the pool shrinks "
        "on every refine"
    )
    # And those per-moment records are what gets stored — by moment, not by
    # position (see test_beats.py for the round trip).
    assert "beats.record_by_beat(" in block


# ── choosing a shot without guessing ────────────────────────────────────

from app.video_agent.shot_edits import (  # noqa: E402
    TAP_THE_SHOT,
    from_picker,
    from_typed,
    positions,
    query_from,
)


def test_the_request_that_was_refused_now_changes_the_last_shot() -> None:
    """ "Remove the last video shot which shows medicine to something related to
    weight management" was refused: "no moment currently shows medicine". What
    the shot shows is never consulted now — "last" is enough.
    """
    edits = from_typed(
        "remove the last video shot which shows medicine to something related to weight management",
        count=4, topic="Why adults need weight management")

    assert edits.by_index() == {3: "weight management"}


def test_positions_are_read_by_code() -> None:
    assert positions("change the first shot to salad", 4) == [0]
    assert positions("swap the second clip for walking", 4) == [1]
    assert positions("replace the second to last scene with cycling", 4) == [2]
    assert positions("shot 3 should show jogging", 4) == [2]
    assert positions("change #2 to cycling", 4) == [1]
    assert positions("change the first and last shots to salad", 4) == [0, 3]


def test_a_description_is_not_a_position() -> None:
    # "the one with medicine" names no shot; guessing is what this replaced.
    assert positions("the one with medicine", 4) is None
    assert from_typed("replace the one with medicine with a salad", 4, "diet") is None
    assert "Tap the shot" in TAP_THE_SHOT


def test_a_position_past_the_end_is_not_a_shot() -> None:
    assert positions("change shot 9 to salad", 4) is None


def test_the_search_is_what_they_asked_for_without_the_pointing_words() -> None:
    assert query_from("change the last shot to a pharmacist weighing a patient") == \
        "a pharmacist weighing a patient"
    assert query_from("make the last one about exercise") == "exercise"
    assert query_from("something related to weight management") == "weight management"


def test_words_describing_the_current_shot_are_not_the_replacement() -> None:
    # "showing medicine" says what is there now, not what should be.
    assert query_from("the last shot showing medicine") == ""


def test_a_shot_with_nothing_asked_for_searches_the_subject() -> None:
    edits = from_typed("change the last shot", 4, "weight management")

    assert edits.by_index() == {3: "weight management"}


def test_a_tapped_shot_is_changed_whatever_it_is_said_to_show() -> None:
    edits = from_picker([{"index": 3, "show": "a person on a scale"}], 4, "weight")

    assert edits.by_index() == {3: "a person on a scale"}


def test_tapped_shots_are_validated_like_anything_else_from_outside() -> None:
    edits = from_picker(
        [{"index": 9, "show": "x"}, {"index": True, "show": "y"}, "junk",
         {"index": 1, "show": "salad"}, {"index": 1, "show": "again"}],
        4, "diet")

    assert edits.by_index() == {1: "salad"}


def test_a_tapped_shot_with_no_words_gets_the_subject() -> None:
    assert from_picker([{"index": 0, "show": "  "}], 2, "weight management").by_index() == \
        {0: "weight management"}

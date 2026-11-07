"""Every shot in a video is about the thing the video is about.

A pharmacist sent a video about flu jabs that contained footage of a blood
pressure monitor and a pill organiser. Nothing had failed: the scriptwriter left
one beat's `visual_keyword` blank and repeated another, and the code filled both
from a fixed list of generic pharmacy queries.

That list is the fault. It is not wrong footage *for a pharmacy* — it is wrong
footage for this video, which is worse, because it looks deliberate. The
pharmacist cannot tell why their flu video is showing a blood pressure cuff, and
neither could anyone reading the logs: nothing was reported, because from the
pipeline's point of view nothing went wrong.

So the rule is now: every search names this video's subject, and a beat that
cannot be filled reuses footage this video already has. Repetition is a
cosmetic problem; a different condition on screen is a correctness one.
"""

from app.video_agent.nodes import (
    PHARMACY_FALLBACK_QUERIES,
    _beat_queries,
    _topic_phrase,
)


# ── naming the subject ──────────────────────────────────────────────────

def test_the_subject_survives_the_question_around_it() -> None:
    # Real topics are written as questions and headlines, not search terms.
    assert _topic_phrase("Why adult take flu jab") == "adult flu jab"
    assert _topic_phrase("How to spot the signs of a stroke") == "spot signs stroke"


def test_filler_words_are_dropped() -> None:
    # "this week" and "your" say nothing about what should be on screen.
    phrase = _topic_phrase("flu jab walk-in clinic this week")

    assert "week" not in phrase
    assert "flu" in phrase and "jab" in phrase


def test_a_word_is_never_repeated() -> None:
    # "flu" twice would spend one of only three slots saying the same thing.
    assert _topic_phrase("flu jab clinic — flu season advice") == "flu jab clinic"


def test_an_empty_topic_gives_an_empty_phrase() -> None:
    # Not a crash, and not a made-up subject. The caller checks for empty and
    # falls back to the beat's own keyword.
    assert _topic_phrase("") == ""
    assert _topic_phrase("   ") == ""
    assert _topic_phrase(None) == ""  # type: ignore[arg-type]


def test_the_phrase_stays_short_enough_to_search_with() -> None:
    # A whole sentence pasted into Pexels matches nothing at all.
    long_topic = "everything a patient might possibly want to know about the "\
                 "seasonal influenza vaccination programme this autumn"

    assert len(_topic_phrase(long_topic).split()) <= 3


def test_the_phrase_is_not_a_generic_pharmacy_query() -> None:
    """The specific regression: a flu topic must not yield the old fallbacks."""
    phrase = _topic_phrase("Why adult take flu jab")

    for generic in PHARMACY_FALLBACK_QUERIES:
        assert phrase != generic


# ── which searches a beat actually tries ────────────────────────────────

# The real function, imported rather than copied. An earlier version of this
# file reimplemented the ordering so it could be tested without Pexels; that is
# how two definitions of one rule start disagreeing, so the ordering was pulled
# out of the loop instead.
_candidates = _beat_queries


def test_a_good_keyword_is_tried_first_and_unchanged() -> None:
    # The scriptwriter chose it for this beat; second-guessing it would make
    # every video about its topic and nothing else.
    assert _candidates("pharmacist giving injection", "adult flu jab", set())[0] == (
        "pharmacist giving injection"
    )


def test_a_keyword_that_found_nothing_is_retried_with_the_subject() -> None:
    tries = _candidates("rolled up sleeve", "adult flu jab", set())

    assert tries[1] == "rolled up sleeve adult flu jab"


def test_a_repeated_keyword_leads_with_the_subject_rather_than_a_new_one() -> None:
    """The exact case that produced blood-pressure footage on a flu video.

    The old code answered a repeat by changing the *subject*. It now changes
    the search while keeping the subject — and because de-duplication is per
    downloaded file, the same search simply returns a different clip.
    """
    tries = _candidates("pharmacy counter", "adult flu jab", {"pharmacy counter"})

    assert tries[0] == "pharmacy counter adult flu jab"
    assert "pharmacy counter" in tries, "still worth trying before giving up"


def test_a_blank_keyword_falls_back_to_the_subject() -> None:
    assert _candidates("", "adult flu jab", set()) == ["adult flu jab"]


def test_no_candidate_is_ever_a_generic_pharmacy_query() -> None:
    """The invariant the whole change exists to hold."""
    for keyword in ("", "pharmacy counter", "blister pack"):
        for used in (set(), {"pharmacy counter"}):
            for query in _candidates(keyword, "adult flu jab", used):
                assert query not in PHARMACY_FALLBACK_QUERIES


def test_with_no_topic_the_keyword_is_still_tried() -> None:
    # A video whose topic is entirely stopwords should not lose its footage.
    assert _candidates("pharmacist smiling", "", set()) == ["pharmacist smiling"]


def test_nothing_at_all_produces_no_searches_rather_than_a_guess() -> None:
    assert _candidates("", "", set()) == []

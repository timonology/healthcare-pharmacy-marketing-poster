"""Footage is recorded against the moment it belongs to, and read back the same way.

The records used to be paired with moments by list position. A moment showing
the pharmacy's photograph, or one that borrowed a clip after a failed search,
recorded nothing — so every later record was one moment early. A caption
re-cut then dropped the photograph and slid every shot along, and a shot edit
kept the wrong shots.
"""

from app.video_agent import beats


def _rec(pid: str, path: str, role: str = "aligned", **extra) -> dict:
    return {"pexels_id": pid, "path": path, "link": f"https://cdn/{pid}.mp4",
            "query": f"q{pid}", "role": role, **extra}


# ── describing a clip ───────────────────────────────────────────────────

def test_a_pexels_page_says_what_the_clip_shows() -> None:
    assert beats.describe("https://www.pexels.com/video/a-woman-washing-her-hands-3997798/") \
        == "a woman washing her hands"


def test_a_page_with_no_slug_says_nothing() -> None:
    assert beats.describe("https://www.pexels.com/video/3997798/") == ""
    assert beats.describe(None) == ""
    assert beats.describe("not a url") == ""


# ── recording one per moment ────────────────────────────────────────────

def test_every_moment_is_recorded_by_index_including_photos_and_borrowed_clips() -> None:
    a, b, extra = _rec("1", "/a"), _rec("2", "/b"), _rec("9", "/x", role="extra")
    # Moment 0 is a photograph; moment 2's search failed and it borrowed /a.
    recorded = beats.record_by_beat(
        4, ["/user0", "/a", "/a", "/b"], {0: 0}, {1: a, 3: b}, [a, b, extra])

    assert [(r["segment"], r["role"], r.get("pexels_id"), r.get("borrowed", False))
            for r in recorded] == [
        (0, "user", None, False),
        (1, "aligned", "1", False),
        (2, "aligned", "1", True),
        (3, "aligned", "2", False),
    ]


def test_a_moment_with_no_nameable_clip_is_left_out_not_invented() -> None:
    recorded = beats.record_by_beat(2, ["/a", "/mystery"], {}, {0: _rec("1", "/a")}, [])

    assert [r["segment"] for r in recorded] == [0]


# ── reading back ────────────────────────────────────────────────────────

def test_tagged_records_come_back_at_their_own_moment() -> None:
    records = [
        {"segment": 0, "role": "user"},
        _rec("1", "/a", segment=1),
        _rec("1", "/a", segment=2, borrowed=True),
        _rec("2", "/b", segment=3),
        _rec("9", "/x", role="extra"),
    ]

    per_beat, extras = beats.by_beat(records, 4)

    assert per_beat[0] is None, "a photograph moment is rebuilt, not fetched"
    assert [r["pexels_id"] for r in per_beat[1:]] == ["1", "1", "2"]
    assert [r["pexels_id"] for r in extras] == ["9"]


def test_old_untagged_records_step_over_the_photo_moment() -> None:
    # The exact case that dropped photographs from re-cuts: moment 0 was a
    # photo with no record, so the first stock record belongs to moment 1.
    records = [_rec("1", "/a"), _rec("2", "/b"), _rec("9", "/x", role="extra")]

    per_beat, extras = beats.by_beat(records, 3, pinned={0})

    assert per_beat[0] is None
    assert [r["pexels_id"] for r in per_beat[1:]] == ["1", "2"]


def test_round_trip_puts_every_shot_back_where_it_was() -> None:
    a, b = _rec("1", "/a"), _rec("2", "/b")
    recorded = beats.record_by_beat(4, ["/u", "/a", "/a", "/b"], {0: 0}, {1: a, 3: b}, [a, b])

    per_beat, _ = beats.by_beat(recorded, 4, pinned={0})

    assert [r and r["path"] for r in per_beat] == [None, "/a", "/a", "/b"]


def test_records_with_no_role_are_still_fill() -> None:
    per_beat, extras = beats.by_beat([{"link": "x"}], 2)

    assert per_beat == [None, None] and len(extras) == 1


# ── describing moments for the shot-edit model ──────────────────────────


# ── not showing the same footage twice ──────────────────────────────────

def test_a_failed_moment_takes_an_unused_spare_before_a_shot_on_screen() -> None:
    # The old rule was pool[i % len(pool)] with shots on screen first, so
    # moment 1 got "/a" — a duplicate of moment 0.
    filled = beats.fill_gaps(["/a", "", "/b"], stock=["/a", "/b"], spares=["/spare"])

    assert filled == ["/a", "/spare", "/b"]


def test_with_no_spares_a_repeat_is_not_next_to_its_twin() -> None:
    filled = beats.fill_gaps(["/a", "", "/b", "/c"], stock=["/a", "/b", "/c"], spares=[])

    assert filled[1] not in ("/a", "/b"), "a repeat must not sit beside the original"
    assert filled[1] == "/c"


def test_several_gaps_spread_across_clips_rather_than_piling_on_one() -> None:
    filled = beats.fill_gaps(["/a", "", "", "", "/b"], stock=["/a", "/b"], spares=["/x", "/y"])

    assert filled[1:4] == ["/x", "/y", filled[3]]
    assert filled[3] not in ("/y", "/b")


def test_moments_with_clips_are_untouched() -> None:
    assert beats.fill_gaps(["/a", "/b"], ["/a", "/b"], ["/x"]) == ["/a", "/b"]
    assert beats.fill_gaps(["", ""], [], []) == ["", ""]


def test_the_first_use_of_a_clip_is_the_middle_as_before() -> None:
    # Unchanged for every clip shown once, which is almost every clip.
    assert beats.subclip_start(10.0, 3.0, 0) == 3.5


def test_a_reused_clip_is_cut_from_a_different_part() -> None:
    starts = [beats.subclip_start(12.0, 3.0, use) for use in range(4)]

    assert len(set(starts)) == 4, starts
    assert all(0 <= s <= 12.0 - 3.0 for s in starts)
    # Far enough apart that the two cuts do not show the same frames.
    assert abs(starts[1] - starts[0]) >= 3.0


def test_a_clip_with_no_room_for_another_window_still_starts_sensibly() -> None:
    assert beats.subclip_start(3.05, 3.0, 2) == 0.0
    assert 0 <= beats.subclip_start(4.0, 3.0, 1) <= 1.0


# ── which moments really are photographs ────────────────────────────────

def test_a_photo_moment_records_which_picture_it_showed() -> None:
    recorded = beats.record_by_beat(2, ["/u", "/a"], {0: 1}, {1: _rec("1", "/a")}, [])

    assert recorded[0] == {"segment": 0, "role": "user", "image": 1}


def test_recorded_photos_are_what_the_render_showed() -> None:
    records = [{"segment": 0, "role": "user", "image": 0}, _rec("1", "/a", segment=1)]

    assert beats.recorded_photos(records, 2) == {0: 0}


def test_old_untagged_records_cannot_say_so_the_caller_plans() -> None:
    assert beats.recorded_photos([_rec("1", "/a")], 2) is None
    assert beats.recorded_photos(None, 2) is None


# ── topping up short footage ────────────────────────────────────────────

def test_topping_up_uses_an_unseen_clip_before_one_already_shown() -> None:
    # The old rotation started at pool[0] whatever had been shown.
    pool = ["/shown", "/spare"]

    assert beats.pick_topup(pool, {"/shown": 1}, 0) == "/spare"


def test_equally_used_clips_take_turns() -> None:
    pool = ["/a", "/b", "/c"]

    picks = [beats.pick_topup(pool, {}, turn) for turn in range(3)]

    assert picks == ["/a", "/b", "/c"]


def test_a_clip_that_will_not_open_is_not_offered_again() -> None:
    pool = ["/broken", "/a"]
    uses = {"/broken": beats.UNOPENABLE, "/a": 3}

    assert beats.pick_topup(pool, uses, 0) == "/a"


# ── every shot on screen is a moment ────────────────────────────────────

def _planned(*spans: float) -> list[dict]:
    out, t = [], 0.0
    for s in spans:
        out.append({"start": t, "end": t + s})
        t += s
    return out


def test_the_moments_fill_the_whole_narration_so_nothing_is_appended() -> None:
    """The bug: 4 moments planned for ~15s over 19.8s of voice rendered 6 shots.

    The 2 appended top-up shots belonged to no moment, so "change the last shot"
    named a shot the refine had never been shown.
    """
    timeline = beats.fit_timeline(_planned(3, 4, 4, 4), 19.8)

    assert len(timeline) == 4
    assert timeline[0][0] == 0
    assert abs(sum(d for _, d in timeline) - 19.8) < 1e-9
    assert timeline[-1][0] + timeline[-1][1] == 19.8


def test_moments_keep_their_planned_proportions() -> None:
    timeline = beats.fit_timeline(_planned(2, 4), 12.0)

    assert [round(d, 6) for _, d in timeline] == [4.0, 8.0]


def test_moments_are_back_to_back() -> None:
    timeline = beats.fit_timeline(_planned(3, 5, 2, 6), 20.0)

    for (s0, d0), (s1, _) in zip(timeline, timeline[1:]):
        assert abs(s0 + d0 - s1) < 1e-9


def test_a_tiny_or_unreadable_moment_still_gets_a_real_shot() -> None:
    segments = [{"start": 0, "end": 0.1}, {"start": "?", "end": None}, {"start": 1, "end": 9}]

    timeline = beats.fit_timeline(segments, 10.0)

    assert all(d >= beats.MIN_SHOT_SECONDS - 1e-9 for _, d in timeline)
    assert abs(sum(d for _, d in timeline) - 10.0) < 1e-9


def test_captions_move_with_the_shots() -> None:
    timed = beats.timed_segments([{"text": "a", "start": 0, "end": 2},
                                  {"text": "b", "start": 2, "end": 4}], 8.0)

    assert [(s["text"], s["start"], s["end"]) for s in timed] == [("a", 0.0, 4.0), ("b", 4.0, 8.0)]


def test_nothing_to_lay_out() -> None:
    assert beats.fit_timeline([], 10.0) == []
    assert beats.fit_timeline(_planned(3), 0) == []


# ── the model is told where each shot is, not just what it might show ───


# ── searching for a shot the pharmacist chose ───────────────────────────

def test_a_chosen_shot_searches_their_words_then_with_the_subject_only() -> None:
    # No subject-only search, no generic list: if both miss, a clip already in
    # this video is reused (see test_refine_scenarios).
    assert beats.replacement_queries("person on a scale", "weight management") == [
        "person on a scale", "person on a scale weight management"]


def test_no_empty_searches() -> None:
    assert beats.replacement_queries("salad", "") == ["salad"]
    assert beats.replacement_queries("", "diet") == []


# ── how many shots ──────────────────────────────────────────────────────────
#
# Reported: AI videos used to have four shots and now often have three —
# especially with a photo attached, which then leaves two stock shots. The
# count was whatever the scriptwriter wrote; it is now decided from the length.


def test_a_fifteen_second_video_has_four_shots() -> None:
    assert beats.shot_count(15) == 4


def test_the_shot_count_follows_the_length_within_limits() -> None:
    assert beats.shot_count(3) == beats.MIN_SHOTS
    assert beats.shot_count(30) > beats.shot_count(15)
    assert beats.shot_count(600) == beats.MAX_SHOTS
    assert beats.shot_count(None) == beats.MIN_SHOTS  # type: ignore[arg-type]


def _three_moments() -> list[dict]:
    return [
        {"start": 0.0, "end": 4.0, "text": "Feeling run down this winter?", "visual_keyword": "tired woman"},
        {"start": 4.0, "end": 10.0,
         "text": "A flu jab protects you and the people you care about. It takes five minutes.",
         "visual_keyword": "flu vaccine"},
        {"start": 10.0, "end": 14.0, "text": "Walk in at Riverside today.", "visual_keyword": "pharmacy counter"},
    ]


def test_three_moments_become_four_without_changing_a_word() -> None:
    before = _three_moments()
    after = beats.ensure_shot_count(before, 4)

    assert len(after) == 4
    # The narration is cut, never rewritten: the voice and captions still agree.
    assert " ".join(s["text"] for s in after) == " ".join(s["text"] for s in before)


def test_the_longest_moment_is_split_at_its_sentence_end() -> None:
    after = beats.ensure_shot_count(_three_moments(), 4)

    assert after[1]["text"] == "A flu jab protects you and the people you care about."
    assert after[2]["text"] == "It takes five minutes."


def test_each_half_searches_for_different_footage() -> None:
    after = beats.ensure_shot_count(_three_moments(), 4)
    keywords = [s["visual_keyword"] for s in after]

    assert len(set(keywords)) == len(keywords), keywords
    assert after[2]["visual_keyword"].startswith("flu vaccine")


def test_split_timings_stay_inside_the_original_moment() -> None:
    after = beats.ensure_shot_count(_three_moments(), 4)

    assert after[1]["start"] == 4.0 and after[2]["end"] == 10.0
    assert 4.0 < after[1]["end"] == after[2]["start"] < 10.0


def test_enough_moments_are_left_alone() -> None:
    four = _three_moments() + [{"start": 14, "end": 15, "text": "Ask us.", "visual_keyword": "smile"}]
    assert beats.ensure_shot_count(four, 4) == four


def test_moments_too_short_to_split_are_not_shredded() -> None:
    tiny = [{"start": 0, "end": 2, "text": "Ask us today.", "visual_keyword": "smile"}]
    assert beats.ensure_shot_count(tiny, 4) == tiny


def test_nothing_in_nothing_out() -> None:
    assert beats.ensure_shot_count([], 4) == []


def test_a_sentence_ending_in_a_quote_is_still_a_sentence_end() -> None:
    quoted = [{"start": 0, "end": 6, "visual_keyword": "pharmacist",
               "text": 'Our pharmacist says "ask us anything." We are here until seven tonight.'}]
    after = beats.ensure_shot_count(quoted, 2)

    assert after[0]["text"] == 'Our pharmacist says "ask us anything."'

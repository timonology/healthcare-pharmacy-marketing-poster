"""Refines run through the real visuals node, with stock search and photos faked.

Each test is a situation a pharmacist actually hit, end to end through the code
that decides which footage lands on which moment.
"""

import json
from pathlib import Path

import pytest

from app.video_agent import nodes, rehydrate
from app.video_agent.context import UserContext


def _segments(n: int) -> list[dict]:
    return [{"text": f"moment {i}", "start": i * 3.0, "end": i * 3.0 + 3,
             "visual_keyword": f"kw{i}"} for i in range(n)]


@pytest.fixture
def stock(monkeypatch):
    """A fake Pexels: every search returns the same ranked results, and the
    first one not already taken is used — as the real one does."""
    searched: list[str] = []

    def download(out_dir, query, orientation, prefer_vertical, already, sources=None, role="aligned"):
        searched.append(query)
        pid = next(str(i) for i in range(100, 1000) if str(i) not in already)
        already.add(pid)
        path = str(Path(out_dir) / f"{pid}.mp4")
        if sources is not None:
            sources.append({"path": path, "pexels_id": pid, "link": f"https://cdn/{pid}",
                            "query": query, "shows": query, "role": role})
        return path

    monkeypatch.setattr(nodes, "_download_one_clip", download)
    monkeypatch.setattr(nodes, "_pinned_user_clips", lambda ctx, segs, out, size: (
        {m: f"/photo{i}.mp4" for m, i in nodes._photo_pins(ctx, segs).items()}, []))
    return searched


def _ctx(images: int, **extra) -> UserContext:
    return UserContext(job_id="scenario", user_id="u", topic="flu jab",
                       user_images=[{"blobKey": f"k{i}"} for i in range(images)], **extra)


def _run(ctx: UserContext, state: dict) -> dict:
    return nodes._collect_pexels_b_roll(ctx, {"metadata": {"segments": _segments(4)}, **state},
                                        lambda *a: None)


def test_first_render_records_photos_and_stock_by_moment(stock) -> None:
    out = _run(_ctx(2), {})

    roles = [(r.get("segment"), r["role"]) for r in out["clip_sources"] if r["role"] != "extra"]
    assert roles == [(0, "user"), (1, "aligned"), (2, "aligned"), (3, "user")]
    assert out["aligned_clips"][0] == "/photo0.mp4" and out["aligned_clips"][3] == "/photo1.mp4"


def test_changing_the_last_moment_replaces_the_photo_there_and_keeps_the_rest(stock) -> None:
    # "Change the last video to pharmacy related flu" on a video whose last
    # moment the system believes is a photo. It used to be refused.
    first = _run(_ctx(2), {})
    doc = {"images": [{"blobKey": "k0"}, {"blobKey": "k1"}], "clipSources": first["clip_sources"]}

    pins = rehydrate.pinned_beats(doc, 4)
    pins.pop(3, None)  # what the worker does for a shot edit on moment 3
    stock.clear()
    ctx = _ctx(2, shot_edits={3: "pharmacist flu jab"}, user_pins=pins)
    previous = [r.get("path", "") if r.get("role") == "aligned" else ""
                for r in first["clip_sources"] if r.get("role") != "extra"]
    sources = [r if r.get("role") == "aligned" else None
               for r in first["clip_sources"] if r.get("role") != "extra"]

    out = _run(ctx, {"previous_aligned": previous, "previous_sources": sources,
                     "clip_sources": first["clip_sources"]})

    assert out["aligned_clips"][0] == "/photo0.mp4", "the untouched photo stays"
    assert out["aligned_clips"][1:3] == first["aligned_clips"][1:3], "unchanged shots are kept"
    assert out["aligned_clips"][3] not in ("/photo1.mp4", *first["aligned_clips"])
    assert stock[0] == "pharmacist flu jab"
    last = [r for r in out["clip_sources"] if r.get("segment") == 3][0]
    assert last["role"] == "aligned", "recorded as footage, so the next refine keeps it that way"


def test_a_replaced_photo_is_not_put_back_by_the_next_refine(stock) -> None:
    first = _run(_ctx(2), {})
    doc = {"images": [{"blobKey": "k0"}, {"blobKey": "k1"}], "clipSources": first["clip_sources"]}
    pins = rehydrate.pinned_beats(doc, 4)
    pins.pop(3)
    second = _run(_ctx(2, shot_edits={3: "pharmacist"}, user_pins=pins), {})

    doc["clipSources"] = second["clip_sources"]

    assert rehydrate.pinned_beats(doc, 4) == {0: 0}


def test_a_refine_never_brings_back_a_clip_the_video_already_has(stock) -> None:
    first = _run(_ctx(0), {})
    used = {r["pexels_id"] for r in first["clip_sources"]}

    out = _run(_ctx(0, shot_edits={2: "pharmacist"}, user_pins={}),
               {"clip_sources": first["clip_sources"]})

    new = [r["pexels_id"] for r in out["clip_sources"] if r.get("segment") == 2]
    assert new and new[0] not in used


def test_a_photo_that_failed_once_is_tried_again_next_time(stock, monkeypatch) -> None:
    # Photo 1 would not download this render; the moment showed stock instead.
    monkeypatch.setattr(nodes, "_pinned_user_clips", lambda ctx, segs, out, size: (
        {m: f"/photo{i}.mp4" for m, i in nodes._photo_pins(ctx, segs).items() if i == 0},
        ["image 2 (upload): not found in storage"]))

    out = _run(_ctx(2), {})
    doc = {"images": [{"blobKey": "k0"}, {"blobKey": "k1"}], "clipSources": out["clip_sources"]}

    assert out["aligned_clips"][3] != "/photo1.mp4", "stock stood in this time"
    assert rehydrate.pinned_beats(doc, 4) == {0: 0, 3: 1}, "the photo is still meant to be there"


# ── a shot the pharmacist chose, when stock has nothing ─────────────────

def test_a_chosen_shot_searches_their_words_then_with_the_subject(stock) -> None:
    first = _run(_ctx(0), {})
    stock.clear()

    _run(_ctx(0, shot_edits={3: "person on a scale"}, user_pins={}),
         {"clip_sources": first["clip_sources"],
          "previous_aligned": first["aligned_clips"][:3] + [""],
          "previous_sources": [r for r in first["clip_sources"] if r.get("role") == "aligned"][:3] + [None]})

    assert stock[0] == "person on a scale"
    assert "flu jab" not in stock[:1], "the subject alone is never searched for a chosen shot"


def test_when_stock_misses_a_chosen_shot_reuses_a_clip_already_in_the_video(stock, monkeypatch) -> None:
    first = _run(_ctx(0), {})
    in_video = set(first["aligned_clips"])
    real = nodes._download_one_clip

    def nothing_for_the_request(out_dir, query, *a, **k):
        return None if "scale" in query else real(out_dir, query, *a, **k)

    monkeypatch.setattr(nodes, "_download_one_clip", nothing_for_the_request)

    out = _run(_ctx(0, shot_edits={3: "person on a scale"}, user_pins={}),
               {"clip_sources": first["clip_sources"],
                "previous_aligned": first["aligned_clips"][:3] + [""],
                "previous_sources": [r for r in first["clip_sources"]
                                     if r.get("role") == "aligned"][:3] + [None]})

    assert out["aligned_clips"][3] in in_video, "a clip this video already had"
    assert out["aligned_clips"][3] != out["aligned_clips"][2], "and not the one right before it"


def test_a_refine_with_no_footage_at_all_fails_rather_than_use_generic_stock(monkeypatch) -> None:
    # The generic pharmacy list is for a first render with nothing else. On a
    # change to an approved video it would put unrelated footage in; failing
    # leaves the video as it was (the worker keeps it Ready and says so).
    #
    # Generic searches *succeed* here, so the test can tell "refused to use
    # them" from "tried them and found nothing" — the first version could not.
    generic = set(nodes.PHARMACY_FALLBACK_QUERIES)
    searched: list[str] = []

    def only_generic_stock_exists(out_dir, query, *a, **k):
        searched.append(query)
        return str(Path(out_dir) / "generic.mp4") if query in generic else None

    monkeypatch.setattr(nodes, "_download_one_clip", only_generic_stock_exists)
    monkeypatch.setattr(nodes, "_pinned_user_clips", lambda *a, **k: ({}, []))

    raised = False
    try:
        _run(_ctx(0, shot_edits={0: "anything"}, user_pins={}),
             {"clip_sources": [{"segment": 0, "role": "aligned", "pexels_id": "1"}]})
    except RuntimeError:
        raised = True

    assert raised
    assert not generic.intersection(searched), "the generic list must not even be searched"


def test_a_first_render_may_still_use_generic_stock_as_a_last_resort(monkeypatch) -> None:
    generic = set(nodes.PHARMACY_FALLBACK_QUERIES)
    monkeypatch.setattr(nodes, "_download_one_clip",
                        lambda out_dir, query, *a, **k: str(Path(out_dir) / "g.mp4") if query in generic else None)
    monkeypatch.setattr(nodes, "_pinned_user_clips", lambda *a, **k: ({}, []))

    out = _run(_ctx(0), {})

    assert all(out["aligned_clips"]), "a first render with nothing else still gets a video"

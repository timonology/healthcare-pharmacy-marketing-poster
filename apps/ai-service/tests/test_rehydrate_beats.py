"""A refine lays footage back against the moment it came from.

Covers the two failures a pharmacist reported as "nothing changed":
a caption re-cut that dropped their photograph and slid every shot one moment
early, and a shot edit that kept the wrong shots.
"""

import json
from pathlib import Path

import pytest

from app.video_agent import rehydrate


def _rec(pid: str, **extra) -> dict:
    return {"pexels_id": pid, "link": f"https://cdn/{pid}.mp4", "query": f"q{pid}",
            "role": "aligned", **extra}


def _doc(records: list[dict], segments: int, images: int = 0) -> dict:
    return {
        "_id": "v1",
        "clipSources": records,
        "images": [{"blobKey": f"videos/o/attachments/{i}.jpg"} for i in range(images)],
        "script": "words",
        "metadataJson": json.dumps({"segments": [
            {"text": f"moment {i}", "start": i * 3, "end": i * 3 + 3} for i in range(segments)
        ]}),
        "audioBlobKey": "a.mp3",
    }


@pytest.fixture
def downloads(monkeypatch):
    """Every link 'downloads' to a file named after its Pexels id."""
    fetched: list[str] = []

    def fake(url: str, target: Path) -> bool:
        fetched.append(url)
        return "dead" not in url

    monkeypatch.setattr(rehydrate, "_download", fake)
    return fetched


def test_tagged_footage_comes_back_at_its_own_moment(tmp_path, downloads) -> None:
    doc = _doc([
        {"segment": 0, "role": "user"},
        _rec("1", segment=1),
        _rec("1", segment=2, borrowed=True),
        _rec("2", segment=3),
        _rec("9", role="extra"),
    ], segments=4, images=1)

    paths, sources, extras = rehydrate.footage(doc, tmp_path, 4)

    assert paths[0] == "" and sources[0] is None
    assert paths[1] == paths[2], "a borrowed clip is the same file"
    assert [s and s["pexels_id"] for s in sources] == [None, "1", "1", "2"]
    assert len(extras) == 1
    assert downloads.count("https://cdn/1.mp4") == 1, "fetched once, not per moment"


def test_old_records_step_over_the_photograph(tmp_path, downloads) -> None:
    # Written before moments were tagged: the photo at moment 0 recorded
    # nothing, so record 0 belongs to moment 1. Pairing by position was the bug.
    doc = _doc([_rec("1"), _rec("2")], segments=3, images=1)

    _paths, sources, _extras = rehydrate.footage(doc, tmp_path, 3)

    assert [s and s["pexels_id"] for s in sources] == [None, "1", "2"]


def test_a_caption_recut_keeps_the_photograph_and_every_shot_in_place(
        tmp_path, downloads, monkeypatch) -> None:
    doc = _doc([_rec("1"), _rec("2")], segments=3, images=1)
    monkeypatch.setattr(rehydrate, "narration", lambda *_a: str(tmp_path / "n.mp3"))
    monkeypatch.setattr(rehydrate, "stills", lambda *_a: [])
    monkeypatch.setattr(rehydrate, "user_clips",
                        lambda _doc, _segs, _out: {0: "/photo0.mp4"})

    state, missing = rehydrate.initial_state(doc, "editor", tmp_path)

    assert missing == []
    aligned = state["aligned_clips"]
    assert aligned[0] == "/photo0.mp4", "the photograph must survive a caption change"
    assert len(aligned) == 3 and all(aligned)
    # Fetched in moment order, so moment 1's clip is the first file and moment
    # 2's the second. Paired by position, moment 1 got moment 2's shot.
    assert Path(aligned[1]).name == "000.mp4" and Path(aligned[2]).name == "001.mp4", (
        "each stock shot stays on its own moment")


def test_a_moment_whose_clip_is_gone_borrows_stock_not_the_photo(
        tmp_path, downloads, monkeypatch) -> None:
    doc = _doc([_rec("1", segment=1), _rec("dead", segment=2)], segments=3, images=1)
    monkeypatch.setattr(rehydrate, "narration", lambda *_a: str(tmp_path / "n.mp3"))
    monkeypatch.setattr(rehydrate, "stills", lambda *_a: [])
    monkeypatch.setattr(rehydrate, "user_clips",
                        lambda _doc, _segs, _out: {0: "/photo0.mp4"})

    state, _missing = rehydrate.initial_state(doc, "editor", tmp_path)

    aligned = state["aligned_clips"]
    assert aligned[2] == aligned[1] != "/photo0.mp4"


# ── which moments are photographs ──────────────────────────────────────

def test_a_picture_on_the_list_that_was_never_shown_is_not_a_photo_moment() -> None:
    """The refusal a pharmacist hit: "moment 0 shows the pharmacy's own photograph".

    .NET adds pictures to the list when an add-pictures change is queued. If
    that change never rendered, planning from the list called the first and
    last moments photographs in a video that showed stock there. After a render
    that recorded its moments, the record is the truth.
    """
    doc = _doc([_rec("1", segment=0), _rec("2", segment=1), _rec("3", segment=2)],
               segments=3, images=2)

    assert rehydrate.pinned_beats(doc, 3) == {}


def test_a_replaced_photo_stays_replaced() -> None:
    doc = _doc([{"segment": 0, "role": "user", "image": 0}, _rec("2", segment=1),
                _rec("3", segment=2)], segments=3, images=2)

    # Planning would put picture 1 on the last moment; the record says it
    # was replaced with footage there, and that choice is kept.
    assert rehydrate.pinned_beats(doc, 3) == {0: 0}


def test_adding_a_picture_places_it_alongside_the_ones_shown() -> None:
    doc = _doc([{"segment": 0, "role": "user", "image": 0}, _rec("2", segment=1),
                _rec("3", segment=2)], segments=3, images=2)
    doc["refine"] = {"pinsOwnPictures": True, "addedPictures": 1}

    pins = rehydrate.pinned_beats(doc, 3)

    assert sorted(pins.values()) == [0, 1] and set(pins) == {0, 2}


def test_adding_a_picture_does_not_bring_back_a_replaced_one() -> None:
    # Picture 1 was replaced with footage; picture 2 is being added now.
    doc = _doc([{"segment": 0, "role": "user", "image": 0}, _rec("2", segment=1),
                _rec("3", segment=2), _rec("4", segment=3)], segments=4, images=3)
    doc["refine"] = {"pinsOwnPictures": True, "addedPictures": 1}

    assert sorted(rehydrate.pinned_beats(doc, 4).values()) == [0, 2]


def test_old_records_fall_back_to_the_plan() -> None:
    doc = _doc([_rec("1"), _rec("2")], segments=4, images=2)

    assert set(rehydrate.pinned_beats(doc, 4)) == {0, 3}


def test_adding_pictures_without_a_count_still_places_the_new_ones() -> None:
    # An add-pictures change queued by an API build that predates the count.
    doc = _doc([{"segment": 0, "role": "user", "image": 0}, _rec("2", segment=1),
                _rec("3", segment=2)], segments=3, images=2)
    doc["refine"] = {"pinsOwnPictures": True}

    assert sorted(rehydrate.pinned_beats(doc, 3).values()) == [0, 1]


def test_a_clip_that_will_not_download_is_still_described(tmp_path, downloads) -> None:
    doc = _doc([_rec("dead", segment=0, shows="a pharmacist")], segments=1)

    paths, sources, _extras = rehydrate.footage(doc, tmp_path, 1)

    assert paths == [""], "nothing to keep"
    assert sources[0]["shows"] == "a pharmacist", "but what it showed is still known"

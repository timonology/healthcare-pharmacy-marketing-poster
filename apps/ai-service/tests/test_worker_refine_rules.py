"""Small worker rules around refines, tested without Mongo."""

from app.video_agent.worker import _cannot, _undo_added_pictures


def _job(images: int, added, pins: bool = True) -> dict:
    return {
        "images": [{"blobKey": f"k{i}"} for i in range(images)],
        "refine": {"pinsOwnPictures": pins, "addedPictures": added, "confirmed": True},
    }


def test_pictures_a_change_added_come_off_when_it_does_not_happen() -> None:
    assert _undo_added_pictures(_job(3, 2)) == {"images": [{"blobKey": "k0"}]}


def test_only_an_add_pictures_change_undoes_anything() -> None:
    assert _undo_added_pictures(_job(3, 2, pins=False)) == {}


def test_a_count_it_cannot_trust_undoes_nothing() -> None:
    for added in (None, 0, -1, True, "2", 5):
        assert _undo_added_pictures(_job(3, added)) == {}, added


def test_the_parts_it_cannot_do_read_as_a_sentence() -> None:
    assert _cannot(["removing duplicate captions."]) == " Not possible here: removing duplicate captions."
    assert _cannot(["  "]) == ""


def test_a_failed_change_says_the_video_is_untouched_and_how_to_retry() -> None:
    from app.video_agent.worker import _refine_failure

    note = _refine_failure(RuntimeError("boom"))

    assert "as it was" in note and "ask for the change again" in note
    assert "allowance" not in note, "a refine never uses the allowance"
    assert _refine_failure(TimeoutError()).startswith("It took longer")


# ── what a refine leaves behind when it does not happen ─────────────────

from app.video_agent.worker import (  # noqa: E402
    STATUS_FAILED,
    STATUS_READY,
    _failure_outcome,
    _refusal_outcome,
)


def _finished(**refine) -> dict:
    return {"blobKey": "videos/o/v/final.mp4",
            "images": [{"blobKey": "k0"}, {"blobKey": "k1"}],
            "refine": {"confirmed": True, **refine}}


def test_a_crashed_change_leaves_the_finished_video_ready() -> None:
    # It used to be marked Failed — a working video taken away, with Retry
    # offered on a change rather than the video.
    outcome = _failure_outcome(_finished(), RuntimeError("ffmpeg died"))

    assert outcome["status"] == STATUS_READY
    assert "as it was" in outcome["note"]


def test_a_crashed_first_render_is_still_failed() -> None:
    outcome = _failure_outcome({"refine": None}, RuntimeError("x"))

    assert outcome["status"] == STATUS_FAILED
    assert "images" not in outcome


def test_a_crashed_add_pictures_change_takes_its_pictures_back_off() -> None:
    outcome = _failure_outcome(
        _finished(pinsOwnPictures=True, addedPictures=1), RuntimeError("x"))

    assert outcome["images"] == [{"blobKey": "k0"}]


def test_a_refused_change_leaves_the_video_ready_and_says_why() -> None:
    outcome = _refusal_outcome(_finished(pinsOwnPictures=True, addedPictures=1), "No.")

    assert outcome == {"status": STATUS_READY, "note": "No.", "images": [{"blobKey": "k0"}]}


def test_a_refusal_on_a_failed_video_keeps_it_failed_and_its_pictures() -> None:
    # No file behind it: Ready would show a play button attached to nothing,
    # and Retry will run the same change — pictures included — again.
    job = _finished(pinsOwnPictures=True, addedPictures=1)
    job["blobKey"] = None

    assert _refusal_outcome(job, "No.") == {"status": STATUS_FAILED, "note": "No."}

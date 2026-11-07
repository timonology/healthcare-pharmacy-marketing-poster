"""The pharmacy's own pictures, pinned into a video.

A pharmacist can attach up to four photographs when they queue a video. Two
things about that have to hold, and only one of them is about the happy path:

* the pictures are **used** — pinned to beats, in the order attached, spread
  across the script rather than stacked at the front;
* a picture that will not download or will not decode **gives up its beat and
  nothing else**. The render carries on with stock footage.

The second is the one worth testing hardest. A render costs a research call, a
script call, narration and ninety seconds of ffmpeg, and it spends a video from
the pharmacy's monthly allowance whether or not it succeeds — so a truncated
JPEG taking the whole thing down would turn a cosmetic problem into a lost video
somebody paid for.
"""

from pathlib import Path

from app.video_agent.user_media import MAX_USER_IMAGES, plan_pins, prepare_user_clips


# ── which beat each picture takes ───────────────────────────────────────

def test_nothing_to_pin_is_not_an_error() -> None:
    assert plan_pins(6, 0) == {}
    assert plan_pins(0, 3) == {}
    assert plan_pins(0, 0) == {}


def test_the_opening_beat_is_always_pinned() -> None:
    """The first three seconds decide whether the video is watched at all.

    A real shopfront there is worth more than any stock clip, so the opening is
    taken before anything else.
    """
    for images in range(1, MAX_USER_IMAGES + 1):
        assert 0 in plan_pins(6, images), images


def test_the_closing_beat_is_taken_second() -> None:
    """That is the call to action — "come and see us" over the actual us."""
    pins = plan_pins(6, 2)

    assert sorted(pins) == [0, 5]


def test_pictures_are_spread_rather_than_stacked_at_the_front() -> None:
    # Four pictures on the first four beats makes a video that opens as a
    # slideshow and then becomes a stock reel; the join is obvious.
    pins = sorted(plan_pins(8, 4))

    assert pins[0] == 0
    assert pins[-1] == 7
    assert pins[1] > 1, "the second picture should not sit immediately after the first"


def test_every_attached_picture_is_used() -> None:
    """A picture that was attached and paid attention to must appear.

    This is the shape of a bug the feature already had: two interior beats
    collided, `set()` quietly swallowed one, and a pharmacist who attached four
    photographs got three with nothing saying so. The test is written against
    the invariant rather than that one arithmetic, and it was checked by
    swapping in three different collision-prone spacings — it fails on all
    three. (Removing the `usable >= segment_count` shortcut alone does *not*
    fail it, because the general arithmetic handles that case identically; the
    shortcut is intent, not the fix.)
    """
    for segments in range(1, 13):
        for images in range(0, MAX_USER_IMAGES + 3):
            pins = plan_pins(segments, images)
            expected = min(images, MAX_USER_IMAGES, segments)
            assert len(pins) == expected, (segments, images, pins)


def test_pins_are_real_beats_and_no_picture_is_used_twice() -> None:
    for segments in range(1, 13):
        for images in range(0, MAX_USER_IMAGES + 3):
            pins = plan_pins(segments, images)
            assert all(0 <= beat < segments for beat in pins), (segments, images)
            assert len(set(pins.values())) == len(pins), (segments, images)


def test_pictures_appear_in_the_order_they_were_attached() -> None:
    # The order in the dialog is the order the pharmacist arranged them in.
    pins = plan_pins(8, 3)
    beats = sorted(pins)

    assert [pins[b] for b in beats] == [0, 1, 2]


# ── turning them into clips ─────────────────────────────────────────────

def _image(n: int, source: str = "upload") -> dict:
    return {"blobKey": f"videos/o/attachments/{n}.jpg", "source": source}


def _ok_download(_key: str, path: Path) -> bool:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"pretend jpeg")
    return True


#: Every focal point the fake clip-maker was handed, so the tests can assert on
#: what the render would actually have been told to keep.
_focals: list[tuple[float, float]] = []
#: Every zoom it was handed, likewise.
_zooms: list[float] = []


def _ok_clip(image_path: Path, out_dir: Path, _duration: float, _size,
             *, focal: tuple[float, float] = (0.5, 0.5), zoom: float = 1.0) -> Path:
    _focals.append(focal)
    _zooms.append(zoom)
    return out_dir / f"{image_path.stem}.mp4"


def _three_seconds(_index: int) -> float:
    return 3.0


def test_pinned_pictures_become_clips(tmp_path: Path) -> None:
    clips, skipped = prepare_user_clips(
        [_image(1), _image(2)], {0: 0, 5: 1}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert sorted(clips) == [0, 5]
    assert skipped == []


def test_a_missing_blob_gives_up_its_beat_and_nothing_else(tmp_path: Path) -> None:
    """The blob may have been deleted between queueing and rendering."""
    def missing(_key: str, _path: Path) -> bool:
        return False

    clips, skipped = prepare_user_clips(
        [_image(1)], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=missing, to_clip=_ok_clip,
    )

    assert clips == {}, "the beat falls back to stock"
    assert len(skipped) == 1
    assert "not found" in skipped[0]


def test_a_file_ffmpeg_refuses_gives_up_its_beat_and_nothing_else(tmp_path: Path) -> None:
    """A truncated file, or a PNG that is really something else."""
    def undecodable(*_args, **_kwargs):
        return None

    clips, skipped = prepare_user_clips(
        [_image(1)], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=undecodable,
    )

    assert clips == {}
    assert "could not be decoded" in skipped[0]


def test_an_exception_anywhere_is_contained(tmp_path: Path) -> None:
    """The assertion the whole feature rests on.

    Storage can raise anything — a transport error, an auth failure, a bug in
    the SDK. None of it is worth a render that has already paid for research,
    a script and narration.
    """
    def explodes(*_args, **_kwargs):
        raise RuntimeError("the container is on fire")

    clips, skipped = prepare_user_clips(
        [_image(1)], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=explodes, to_clip=_ok_clip,
    )

    assert clips == {}
    assert "RuntimeError" in skipped[0], "the technical reason is kept for the diagnostic"
    assert "on fire" in skipped[0]


def test_one_bad_picture_does_not_take_the_good_ones_with_it(tmp_path: Path) -> None:
    def second_one_fails(key: str, path: Path) -> bool:
        if key.endswith("2.jpg"):
            raise OSError("truncated")
        return _ok_download(key, path)

    clips, skipped = prepare_user_clips(
        [_image(1), _image(2), _image(3)], {0: 0, 3: 1, 6: 2},
        tmp_path, (1080, 1920), _three_seconds,
        download=second_one_fails, to_clip=_ok_clip,
    )

    assert sorted(clips) == [0, 6], "the readable pictures are still used"
    assert len(skipped) == 1


def test_a_record_with_no_blob_key_is_skipped_not_crashed(tmp_path: Path) -> None:
    clips, skipped = prepare_user_clips(
        [{"source": "upload"}], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert clips == {}
    assert "no blob key" in skipped[0]


# ── which part of the picture is kept ───────────────────────────────────

def test_the_stored_focal_point_reaches_the_crop(tmp_path: Path) -> None:
    """The whole point of letting somebody frame a photograph.

    A landscape phone picture cropped to 9:16 keeps 42% of its width. Which 42%
    is the difference between the shopfront and the wall next to it, so this
    number has to survive .NET, Mongo and the worker to mean anything.
    """
    _focals.clear()
    image = {**_image(1), "focalX": 0.2, "focalY": 0.8}

    prepare_user_clips(
        [image], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _focals == [(0.2, 0.8)]


def test_a_picture_with_no_focal_point_is_centred(tmp_path: Path) -> None:
    # Every picture attached before the crop could be chosen. Centre is what
    # they rendered as, so centre is what they must keep rendering as.
    _focals.clear()

    prepare_user_clips(
        [_image(1)], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _focals == [(0.5, 0.5)]


def test_a_focal_point_outside_the_picture_is_pulled_back_in(tmp_path: Path) -> None:
    # Arrives from a drag in a browser, so a hair outside the box is a rounding
    # artefact of somebody's own gesture. Clamped, never refused.
    _focals.clear()
    image = {**_image(1), "focalX": 1.4, "focalY": -0.3}

    prepare_user_clips(
        [image], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _focals == [(1.0, 0.0)]


def test_an_unreadable_focal_point_falls_back_to_centre(tmp_path: Path) -> None:
    # A string where a number was expected, from a hand-written row or an older
    # writer. Centre is a worse crop than they asked for; a failed render is
    # worse than that.
    _focals.clear()
    image = {**_image(1), "focalX": "left", "focalY": None}

    clips, skipped = prepare_user_clips(
        [image], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _focals == [(0.5, 0.5)]
    assert skipped == [], "a bad focal point must not cost the picture its beat"
    assert clips != {}


def test_the_stored_zoom_reaches_the_clip_maker(tmp_path: Path) -> None:
    _zooms.clear()
    image = {**_image(1), "zoom": 0.6}

    prepare_user_clips(
        [image], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _zooms == [0.6]


def test_a_picture_with_no_zoom_gets_the_original_crop(tmp_path: Path) -> None:
    # Every row written before zoom existed. Not migrated — absent reads as 1.0.
    _zooms.clear()

    prepare_user_clips(
        [_image(1)], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _zooms == [1.0]


def test_a_zoom_out_of_range_is_clamped(tmp_path: Path) -> None:
    _zooms.clear()

    prepare_user_clips(
        [{**_image(1), "zoom": 0.1}, {**_image(2), "zoom": 7}],
        {0: 0, 1: 1}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _zooms == [0.5, 1.5]


def test_an_unreadable_zoom_keeps_the_picture_at_one(tmp_path: Path) -> None:
    # "Unreadable zoom must not skip the picture — treat as 1.0."
    _zooms.clear()
    image = {**_image(1), "zoom": "wide"}

    clips, skipped = prepare_user_clips(
        [image], {0: 0}, tmp_path, (1080, 1920), _three_seconds,
        download=_ok_download, to_clip=_ok_clip,
    )

    assert _zooms == [1.0]
    assert skipped == [] and clips != {}

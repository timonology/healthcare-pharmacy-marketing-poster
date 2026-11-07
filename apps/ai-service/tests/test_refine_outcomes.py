"""What a pharmacist is told when a requested change cannot be made."""

from app.video_agent import editor_options


def test_a_duplicate_caption_request_is_understood() -> None:
    options = editor_options.parse({"remove_repeated_captions": True})

    assert options.remove_repeated_captions is True
    assert not options.is_empty(), "a re-cut can answer this, so it must not be refused"


def test_only_a_literal_true_counts() -> None:
    assert editor_options.parse({"remove_repeated_captions": "yes"}).is_empty()

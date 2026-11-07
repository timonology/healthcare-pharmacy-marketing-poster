"""Which narrator a render uses — the rule that broke production.

Every render on Railway failed at the voice stage while the same code rendered
fine on a laptop. Three things had to line up for that to be hard to see, and
each one is a case below:

* the provider was chosen from the *voice id*, so setting ``ELEVENLABS_API_KEY``
  in production changed nothing at all;
* the fallback to Edge-TTS was silent, so a paid provider being unavailable was
  indistinguishable from a free one being chosen deliberately;
* Edge-TTS cannot be reached from a datacentre, so the fallback led somewhere
  that could not work in the only environment that mattered.

The rule is now: a configured key means ElevenLabs, with no fallback.
"""

from app.video_agent import tts_policy


def test_a_configured_key_wins_over_an_edge_voice_name() -> None:
    """The exact production bug.

    The video asks for an Edge voice — that is the default the .NET side sends —
    and the key is set. Old behaviour: Edge-TTS, which 403s from Railway. New
    behaviour: ElevenLabs.
    """
    choice = tts_policy.choose("en-US-JennyNeural", key_configured=True)

    assert choice.provider == "elevenlabs"
    assert choice.voice is None  # resolve the account default


def test_a_configured_key_honours_an_explicit_elevenlabs_voice() -> None:
    choice = tts_policy.choose("11labs:abc123", key_configured=True)

    assert choice.provider == "elevenlabs"
    assert choice.voice == "abc123"  # prefix stripped


def test_no_key_means_edge_with_the_requested_voice() -> None:
    choice = tts_policy.choose("en-GB-SoniaNeural", key_configured=False)

    assert choice.provider == "edge"
    assert choice.voice == "en-GB-SoniaNeural"


def test_no_key_and_a_paid_voice_falls_back_and_says_so() -> None:
    """A misconfiguration, not a preference — so the reason has to name it.

    Edge can still narrate, and a video in the wrong voice beats no video. But
    somebody asked for a voice they are not going to get, and the only place
    that can surface is the reason string in the log.
    """
    choice = tts_policy.choose("11labs:abc123", key_configured=False)

    assert choice.provider == "edge"
    assert choice.voice is None
    assert "ELEVENLABS_API_KEY" in choice.reason


def test_no_voice_at_all_is_handled_either_way() -> None:
    assert tts_policy.choose(None, key_configured=True).provider == "elevenlabs"
    assert tts_policy.choose(None, key_configured=False).provider == "edge"


def test_edge_is_never_chosen_when_a_key_is_configured() -> None:
    """The property, stated once over every input that has ever been sent.

    This is the assertion that would have failed before the change, for the
    first two cases, which is the entire point of writing it as a sweep rather
    than as three more examples.
    """
    for voice in (None, "", "en-US-JennyNeural", "en-GB-SoniaNeural", "11labs:xyz", "nonsense"):
        assert tts_policy.choose(voice, key_configured=True).provider == "elevenlabs", voice


def test_every_choice_explains_itself() -> None:
    """A reason nobody wrote is a log line nobody can use."""
    for key_configured in (True, False):
        for voice in (None, "en-US-JennyNeural", "11labs:xyz"):
            reason = tts_policy.choose(voice, key_configured=key_configured).reason
            assert reason and len(reason) > 20, (voice, key_configured)

"""Which text-to-speech provider a render uses, and why.

**The rule, in one line: if an ElevenLabs key is configured, ElevenLabs does the
narration. Full stop.**

That is a change from what this pipeline used to do, and the reason is a
production failure. The old rule picked the provider from the *voice id* —
ElevenLabs only when the id carried the ``11labs:`` prefix — and fell through to
Edge-TTS whenever ElevenLabs was not chosen or did not work. On a laptop that is
invisible: Edge-TTS is free and works from a home connection. On Railway every
render died at the voice stage, because Edge-TTS is an unofficial route into
Microsoft's consumer speech service and that service does not serve datacentre
address ranges.

Three separate faults combined to make it hard to see:

* setting ``ELEVENLABS_API_KEY`` in production changed nothing, because the
  branch was decided by the voice id and the default voice was an Edge voice;
* the fallback was silent, so a paid provider being unavailable looked exactly
  like a free provider being chosen on purpose;
* the fallback led somewhere that could not work in that environment, which is
  the worst kind of fallback — it converts a clear provider error into a
  confusing one further down.

So: configured key means ElevenLabs, always, with no fallback. If ElevenLabs
fails, the render fails carrying *ElevenLabs' own reason*, which is a sentence
somebody can act on ("invalid api key", "quota exceeded") instead of a
websocket error from a service we never meant to use in production.

Edge-TTS remains the local path. It is free, it needs no account, and a
developer without an ElevenLabs key should still be able to render a video.
"""
from __future__ import annotations

from typing import Literal, NamedTuple

VOICE_ID_PREFIX = "11labs:"

Provider = Literal["elevenlabs", "edge"]


class TtsChoice(NamedTuple):
    provider: Provider

    #: For ElevenLabs, the raw voice id, or None meaning "resolve the account's
    #: default". For Edge, the voice name to try first.
    voice: str | None

    #: Why, in a few words. Logged, so that the answer to "which provider ran?"
    #: is in the render log rather than inferred from behaviour.
    reason: str


def choose(voice_id: str | None, *, key_configured: bool) -> TtsChoice:
    """Pick the narration provider.

    :param voice_id: What the video asked for — an Edge voice name like
        ``en-US-JennyNeural``, or ``11labs:{uuid}``, or nothing.
    :param key_configured: Whether ``ELEVENLABS_API_KEY`` is set.
    """
    if key_configured:
        if voice_id and voice_id.startswith(VOICE_ID_PREFIX):
            return TtsChoice(
                "elevenlabs",
                voice_id[len(VOICE_ID_PREFIX):],
                "ElevenLabs key configured and this video asked for an ElevenLabs voice",
            )

        # An Edge voice name was asked for and is deliberately ignored. The two
        # services have unrelated voice catalogues, so there is nothing to
        # translate; what the request really meant was "narrate this".
        return TtsChoice(
            "elevenlabs",
            None,
            "ElevenLabs key configured — using it rather than Edge-TTS, which does not "
            "work from a datacentre",
        )

    if voice_id and voice_id.startswith(VOICE_ID_PREFIX):
        # Asked for a paid voice with no key. Edge can still narrate it, and a
        # video with the wrong voice beats no video — but this is a
        # misconfiguration and the reason says so, so it shows up in the log.
        return TtsChoice(
            "edge",
            None,
            "video asked for an ElevenLabs voice but ELEVENLABS_API_KEY is not set — "
            "falling back to Edge-TTS",
        )

    return TtsChoice("edge", voice_id, "no ElevenLabs key configured — using Edge-TTS")

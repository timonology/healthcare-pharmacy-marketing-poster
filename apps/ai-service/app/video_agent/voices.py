"""The voices a pharmacist may pick from, and what they cost.

**Why the catalogue lives here and not in the web app.** It used to be two
hard-coded arrays in ``videos/page.tsx``, and the browser decided from a voice
id whether the render would cost anything. That worked while the voice id
decided the provider. It stopped working the moment the *key* decided it: the
dialog went on offering "Jenny · Warm US female (default)" on a deployment where
Jenny could never be used, and went on estimating $0.005 for a render that would
actually cost about $0.05 — a choice the product would ignore, priced at a tenth
of what it charged.

Only this service knows whether ``ELEVENLABS_API_KEY`` is set. So this service
answers the whole question — which provider, which voices, which default, what
it costs — and the browser renders the answer without deciding anything. The
alternative, teaching the web and the .NET API to read the key, would be the
same fact in three places, which is how the original bug got in.

The ``11labs:`` marker appears nowhere in this file for the same reason: it is
``tts_policy``'s, and one definition is the whole point.
"""
from __future__ import annotations

from typing import Literal, TypedDict

from .tts_policy import VOICE_ID_PREFIX


class Voice(TypedDict):
    id: str
    label: str


#: Rachel. The default when nothing is configured.
#:
#: A curated id rather than "whatever the account lists first": the first voice
#: on an account changes when somebody adds a cloned voice, and a pharmacy's
#: narration silently changing character between two renders is the kind of
#: difference nobody reports as a bug but everybody notices.
DEFAULT_ELEVEN_VOICE = "21m00Tcm4TlvDq8ikWAM"

#: ElevenLabs' public premade voices — available on every account.
ELEVEN_VOICES: list[Voice] = [
    {"id": f"{VOICE_ID_PREFIX}{DEFAULT_ELEVEN_VOICE}", "label": "Rachel · Editorial calm"},
    {"id": f"{VOICE_ID_PREFIX}AZnzlk1XvdvUeBnXmlld", "label": "Domi · Bright energetic"},
    {"id": f"{VOICE_ID_PREFIX}EXAVITQu4vr4xnSDxMaL", "label": "Bella · Soft narrator"},
    {"id": f"{VOICE_ID_PREFIX}pNInz6obpgDQGcFmaJgB", "label": "Adam · Deep clinical"},
]

#: Edge-TTS voices. Offered only where Edge-TTS actually works, which is a
#: developer machine — see tts_policy.
EDGE_VOICES: list[Voice] = [
    {"id": "en-US-JennyNeural", "label": "Jenny · Warm US female"},
    {"id": "en-US-AriaNeural", "label": "Aria · Friendly US female"},
    {"id": "en-US-GuyNeural", "label": "Guy · Confident US male"},
    {"id": "en-GB-SoniaNeural", "label": "Sonia · Clear UK female"},
    {"id": "en-GB-RyanNeural", "label": "Ryan · Warm UK male"},
]

#: Roughly what narration adds to one 15-second video.
#:
#: ElevenLabs Turbo v2.5 is about $0.15 per 1,000 characters and a 15s script is
#: about 350, so ~$0.05 with room for a longer script. Edge-TTS is free. This is
#: the figure the pharmacist is shown *before* queueing, so it is deliberately
#: not less than the real cost.
ELEVENLABS_USD_PER_VIDEO = 0.05


class Narration(TypedDict):
    provider: Literal["elevenlabs", "edge"]
    voices: list[Voice]
    defaultVoiceId: str
    estimateUsdPerVideo: float


def catalogue(*, key_configured: bool, configured_default: str | None = None) -> Narration:
    """What this deployment can actually narrate with.

    The lists do not overlap, deliberately. Offering an Edge voice where
    ElevenLabs will be used regardless means the pharmacist picks a voice and
    gets a different one — the product ignoring a choice it invited.
    """
    if key_configured:
        default = (configured_default or "").strip() or DEFAULT_ELEVEN_VOICE
        if not default.startswith(VOICE_ID_PREFIX):
            default = f"{VOICE_ID_PREFIX}{default}"

        voices = list(ELEVEN_VOICES)
        # A configured voice that is not one of the curated four still has to be
        # selectable, or the dialog would open with nothing selected and send a
        # voice the operator never offered.
        if all(v["id"] != default for v in voices):
            voices.insert(0, {"id": default, "label": "Configured default"})

        return {
            "provider": "elevenlabs",
            "voices": voices,
            "defaultVoiceId": default,
            "estimateUsdPerVideo": ELEVENLABS_USD_PER_VIDEO,
        }

    return {
        "provider": "edge",
        "voices": list(EDGE_VOICES),
        "defaultVoiceId": EDGE_VOICES[0]["id"],
        "estimateUsdPerVideo": 0.0,
    }

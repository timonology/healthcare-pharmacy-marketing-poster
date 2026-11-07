"""ElevenLabs text-to-speech.

**This no longer falls back.** It used to return ``False`` on any failure and
let the caller try Edge-TTS instead, which meant an expired key, an exhausted
quota or a wrong voice id all looked identical to "ElevenLabs was not selected"
— and in production the fallback led to a service that cannot be reached from a
datacentre at all. Every failure now raises :class:`ElevenLabsError` carrying
what ElevenLabs actually said, so the render fails with a reason somebody can
act on. See ``tts_policy`` for the decision this sits behind.
"""
from __future__ import annotations

import logging
from typing import Any

import httpx

from ..config import get_settings

log = logging.getLogger(__name__)

#: Imported rather than repeated. Two copies of "11labs:" is two places to
#: change when the marker changes, and one of them gets missed — this codebase
#: has spent the week paying for exactly that (seven copies of a month-start
#: expression, three of an instruction limit, two collection names).
#: ``tts_policy`` owns it because it decides the routing; this module only
#: strips it.
from .tts_policy import VOICE_ID_PREFIX

_DEFAULT_MODEL = "eleven_turbo_v2_5"
_BASE = "https://api.elevenlabs.io/v1"


class ElevenLabsError(RuntimeError):
    """ElevenLabs could not narrate this, and here is its reason."""


#
# `is_elevenlabs()` used to live here and has been deleted rather than left
# unused. It answered "does this voice id have the 11labs prefix?", which *was*
# the rule for choosing this provider — and that rule is what broke production,
# because it made a configured API key irrelevant. A dead helper that encodes a
# superseded rule is worse than no helper: the next person to need "should we
# use ElevenLabs?" finds it, it looks authoritative, and it reintroduces the
# bug. `tts_policy.choose()` is the answer to that question now.


def extract_voice_id(prefixed: str) -> str:
    return prefixed[len(VOICE_ID_PREFIX):] if prefixed.startswith(VOICE_ID_PREFIX) else prefixed


def _describe(response: httpx.Response) -> str:
    """ElevenLabs' own words, short enough to store.

    Its errors are JSON like ``{"detail": {"status": "quota_exceeded",
    "message": "..."}}``, and the message is the useful part. Falls back to raw
    text, because an error shape we have not seen is still better read than
    replaced with a guess.
    """
    try:
        body = response.json()
        detail = body.get("detail", body)
        if isinstance(detail, dict):
            message = detail.get("message") or detail.get("status") or str(detail)
        else:
            message = str(detail)
    except Exception:  # noqa: BLE001
        message = (response.text or "").strip()

    return f"ElevenLabs returned {response.status_code}: {message}"[:300]


def resolve_default_voice() -> str:
    """The voice to narrate with when the video did not name an ElevenLabs one.

    ``ELEVENLABS_DEFAULT_VOICE_ID`` if set, otherwise the curated default from
    :mod:`voices` — the same one the dialog offers first, so what a pharmacist
    sees selected and what a caller gets by default are one decision rather than
    two that can disagree.

    An earlier version asked the account for its voice list and took the first.
    That was one fewer constant and two worse properties: the "first" voice
    changes the moment somebody adds a cloned voice, so narration could change
    character between renders with nothing in the config having moved; and it
    put a network call on the path of every render that did not name a voice.
    """
    configured = (get_settings().elevenlabs_default_voice_id or "").strip()
    if configured:
        return extract_voice_id(configured)

    from .voices import DEFAULT_ELEVEN_VOICE

    return DEFAULT_ELEVEN_VOICE


def synthesize(
    text: str,
    voice_id: str,
    out_path: str,
    ledger: Any | None = None,
) -> None:
    """Synthesise narration to ``out_path``. Raises :class:`ElevenLabsError`.

    ElevenLabs bills per character of the text sent, so the meter goes here and
    not at the call site: the caller has the script, and the script is not what
    is charged for — stage directions and bracketed notes are stripped before
    this is reached, and billing follows what went over the wire.

    Recorded only on a successful response. A refused or failed request is not
    billed, and counting it would overstate the one number the refine charge
    policy is actually waiting on.
    """
    key = get_settings().elevenlabs_api_key
    if not key:
        raise ElevenLabsError("ELEVENLABS_API_KEY is not set.")

    raw_voice = extract_voice_id(voice_id)
    payload = {
        "text": text,
        "model_id": _DEFAULT_MODEL,
        "voice_settings": {
            "stability": 0.5,
            "similarity_boost": 0.75,
            "style": 0.0,
            "use_speaker_boost": True,
        },
    }
    headers = {
        "xi-api-key": key,
        "Content-Type": "application/json",
        "Accept": "audio/mpeg",
    }

    try:
        with httpx.Client(timeout=120) as client:
            r = client.post(
                f"{_BASE}/text-to-speech/{raw_voice}", json=payload, headers=headers
            )
    except Exception as exc:  # noqa: BLE001
        raise ElevenLabsError(f"could not reach ElevenLabs: {exc}") from exc

    if r.status_code != 200:
        reason = _describe(r)
        log.error("ElevenLabs synthesis failed: %s", reason)
        raise ElevenLabsError(reason)

    with open(out_path, "wb") as f:
        f.write(r.content)

    if ledger is not None:
        from . import usage

        s = get_settings()
        ledger.record(
            usage.ELEVENLABS,
            usage.CHARACTERS,
            usage.count_characters(text),
            s.elevenlabs_usd_per_character,
        )

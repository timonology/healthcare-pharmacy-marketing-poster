"""Decide which pipeline stage a refine instruction actually needs.

The render pipeline is linear — researcher → scriptwriter → voice → visuals →
editor — and a refine restarts at the earliest stage its change touches, reusing
what the previous run produced. Reading down that list is the whole feature: a
caption fix re-runs the editor alone, in seconds, calling nobody; a rewrite
re-runs almost everything and spends real money at three providers.

So this module answers one question: **what is the earliest stage that could
possibly satisfy what the pharmacist asked for?**

Two asymmetries shape every decision here.

*Guessing too early is expensive.* Routing "make the captions bigger" to the
scriptwriter rewrites the script, re-records the narration and bills three
providers for a change ffmpeg could have made on its own.

*Guessing too late is a silent no-op.* Routing "make it friendlier" to the
editor re-cuts the same footage with the same words, reports success, and hands
back a video that is identical in every way the pharmacist cared about. That is
the failure mode this codebase has had most of, and it is worse than the
expensive one because nothing anywhere reports it.

Neither is a risk this module carries alone. It returns a stage and a plain
sentence; the .NET side shows both to the pharmacist and does nothing until they
agree. Classification is a suggestion presented for confirmation, never an
instruction that spends money by itself.
"""
from __future__ import annotations

import json
import logging
import re
from typing import Any

# One definition, in the pipeline module that owns it. These five names were
# written out again here, which is how a classifier starts naming a stage the
# graph cannot enter.
from ..video_agent.stages import FREE_STAGES, MAX_INSTRUCTION_CHARS, STAGES

log = logging.getLogger(__name__)

#: Returned when the instruction does not describe a change to the video.
UNCLEAR = "unclear"



SYSTEM = """\
You route a pharmacist's change request to the earliest stage of a video \
pipeline that could satisfy it.

The pipeline runs in this order. Each stage can only change what it owns.

1. researcher — looks the subject up. Owns the facts the video is based on.
   Needed only if the request disputes or wants different subject matter.
2. scriptwriter — writes the spoken words. Owns wording, tone, the hook, the
   call to action, what is claimed, how long the narration is.
3. voice — reads the script aloud. Owns the speaking voice, accent, pace and
   delivery. It cannot change the words.
4. visuals — chooses the footage and imagery. Owns what is on screen behind the
   narration. It cannot change the words or the voice.
5. editor — cuts it together. Owns on-screen captions and text overlays, their
   size, position, colour and timing, the closing card with the pharmacy's name
   and booking link, fades, and the order and duration of existing shots. It
   cannot change the words, the voice, or which footage was downloaded.

Choose the LAST stage in that list that can fully satisfy the request — that is
the cheapest one that actually works.

If the request needs two things, choose the earlier stage, because everything
after it re-runs anyway.

If the request is not about changing the video — a question, a greeting, praise,
something you cannot interpret — return "unclear". Do not guess a stage.

Reply with JSON only:
{"stage": "<one of: researcher, scriptwriter, voice, visuals, editor, unclear>",
 "reason": "<one short sentence, addressed to the pharmacist, saying what will \
change. No stage names, no jargon.>"}
"""

#: Worked examples. Each is a judgement that was genuinely ambiguous, kept so
#: the boundaries are visible rather than implied by prose.
EXAMPLES: tuple[tuple[str, str], ...] = (
    ("make the subtitles bigger", "editor"),
    ("the text at the bottom is hard to read", "editor"),
    # The end card's contact line is chosen from the pharmacy's own details, so
    # swapping which one shows is genuinely an edit. Asking for a number the
    # pharmacy has not saved is not, and lands in `unsupported`.
    ("show our phone number on the end card instead of the link", "editor"),
    ("drop the closing card", "editor"),
    ("it feels too rushed at the start", "editor"),
    ("use a male voice", "voice"),
    ("she talks too fast", "voice"),
    ("say 'flu jab' instead of 'influenza vaccination'", "scriptwriter"),
    ("make it friendlier", "scriptwriter"),
    ("don't claim it prevents all flu", "scriptwriter"),
    ("mention that we're open on Saturdays", "scriptwriter"),
    ("show a pharmacy, not a hospital", "visuals"),
    ("the footage looks american", "visuals"),
    # How pharmacists actually ask for a shot change: by naming what they can
    # see and where it is, often several at once, and often with the word
    # "remove" — which means "put something else there", because the video is
    # exactly as long as its narration.
    ("remove the man at the end and show an elderly couple walking in a park", "visuals"),
    ("the shot with the white capsules doesn't fit, use something else", "visuals"),
    ("swap the second clip for something with a real pharmacy counter", "visuals"),
    # Pharmacists call a repeated *shot* "a duplicate video" or even "a
    # duplicate caption". Repeated footage is a re-fill, free, like any other.
    ("remove the duplicate video", "visuals"),
    ("the same clip plays twice", "visuals"),
    ("this is out of date, check the latest guidance", "researcher"),
    ("thanks, this is great", UNCLEAR),
    ("what does this cost?", UNCLEAR),
)


def _examples_block() -> str:
    return "\n".join(f'"{text}" -> {stage}' for text, stage in EXAMPLES)


def _extract_json(text: str) -> dict[str, Any]:
    """The first JSON object in a model reply.

    Models wrap JSON in prose or fences often enough that asking nicely is not
    a strategy. A reply we cannot parse becomes `unclear`, which the caller
    surfaces as a question rather than an action.
    """
    if not isinstance(text, str):
        return {}
    fenced = re.search(r"```(?:json)?\s*(.+?)```", text, re.S)
    if fenced:
        text = fenced.group(1)
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        return {}
    try:
        parsed = json.loads(text[start : end + 1])
    except (ValueError, TypeError):
        return {}
    return parsed if isinstance(parsed, dict) else {}


def classify(instruction: str, model: str | None = None) -> dict[str, Any]:
    """Route one instruction. Never raises.

    Returns ``{"stage": <name or "unclear">, "reason": <sentence>, "free": bool}``.

    Every failure — no instruction, no model configured, a provider outage, an
    unparseable reply, a stage name that is not a stage — comes back as
    ``unclear``. That is the one answer that cannot do damage: the .NET side
    turns it into "we could not tell what you wanted to change", which asks the
    pharmacist to rephrase. Falling back to a real stage would either spend
    money on a guess or perform a re-cut that changes nothing.
    """
    text = (instruction or "").strip()
    if len(text) < 3:
        return _unclear("There was nothing to act on in that request.")

    # A long instruction is usually several requests at once, and routing the
    # whole thing to one stage would satisfy part of it and silently drop the
    # rest. Truncating would do the same. Better to say so.
    if len(text) > MAX_INSTRUCTION_CHARS:
        return _unclear(
            "That is a lot to change at once. Ask for one change at a time so "
            "we can tell you what each one will do."
        )

    try:
        # Imported here, like shot_edits and editor_options, so this module
        # loads without the provider SDK. This line once sat *inside* the
        # module docstring, where it was text: every classification raised
        # NameError, was caught below, and reached the pharmacist as "we could
        # not work out what to change" for any sentence at all.
        # `tests/test_no_undefined_names.py` now fails on that shape of fault.
        from app.llm import anthropic_chat, text_of

        from ..config import get_settings

        settings = get_settings()
        chosen = (model or settings.anthropic_model or "").strip()
        if not chosen:
            log.warning("Refine classification skipped: no Anthropic model configured.")
            return _unclear(
                "We could not work out what to change just now. Please try again."
            )

        # No temperature: newer Claude models refuse it with a 400, which would
        # reach the pharmacist as "we could not work out what to change just
        # now" — a transport fault wearing the costume of a comprehension
        # failure, for the second time in this feature. See app/llm.py.
        llm = anthropic_chat(
            chosen, max_tokens=300, timeout=30,
            api_key=settings.anthropic_api_key,
        )
        reply = llm.invoke(
            [
                {"role": "system", "content": SYSTEM + "\nExamples:\n" + _examples_block()},
                {"role": "user", "content": text},
            ]
        )
        parsed = _extract_json(text_of(reply))
    except Exception as exc:  # noqa: BLE001
        # Including the provider being down. A refine that cannot be classified
        # is a refine that does not happen, which is the correct outcome — the
        # video the pharmacist already has is untouched.
        log.warning("Refine classification failed (%s). Returning unclear.", exc)
        return _unclear(
            "We could not work out what to change just now. Please try again."
        )

    stage = str(parsed.get("stage") or "").strip().lower()
    reason = str(parsed.get("reason") or "").strip()

    if stage not in STAGES:
        if stage != UNCLEAR:
            log.info("Refine classifier returned %r, which is not a stage.", stage)
        return _unclear(
            reason
            or "We could not tell which part of the video you wanted changed. "
            "Try naming it — the wording, the voice, the footage, or the "
            "captions and timing."
        )

    return {
        "stage": stage,
        "reason": reason or f"This will change the {stage}.",
        "free": stage in FREE_STAGES,
    }


def _unclear(reason: str) -> dict[str, Any]:
    return {"stage": UNCLEAR, "reason": reason, "free": True}

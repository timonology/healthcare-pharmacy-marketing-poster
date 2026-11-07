"""What an editor-stage refine is actually allowed to change.

Routing an instruction to the editor is only half the job. The editor node's
inputs — the caption text and timings from the script's segments, the footage
from the visuals stage, the end card from the pharmacy's brand — are all decided
before it runs. Re-running it with those same inputs produces a byte-identical
video, reports success, and leaves the pharmacist looking at exactly what they
asked to have changed.

That is the worst outcome in this codebase's history: software that reports
success and hands back the wrong thing. So an editor refine has to *carry
something* into the node, and this module is that something — a small, typed,
bounded set of overrides.

**Small and typed on purpose.** The alternative is handing the instruction to
the editor and letting a model improvise, which cannot be validated, cannot be
bounded, and fails in ways nobody can predict from the code. Everything here is
a number in a range or one of a fixed set of words, so an instruction that asks
for something impossible is *visibly* impossible rather than quietly ignored.
"""
from __future__ import annotations

import json
import logging
import re
from dataclasses import asdict, dataclass, field
from typing import Any

log = logging.getLogger(__name__)

CAPTION_POSITIONS = ("top", "middle", "bottom")
END_CARD_DETAILS = ("booking", "phone", "address", "none")

#: How far caption size may be pushed. Below 0.6 it is unreadable on a phone;
#: above 1.6 a three-line caption covers the footage it is captioning. A request
#: outside this is clamped and said out loud, rather than honoured into
#: something unusable or refused outright.
CAPTION_SCALE_MIN = 0.6
CAPTION_SCALE_MAX = 1.6

FADE_MIN = 0.0
FADE_MAX = 1.5


@dataclass
class EditorOptions:
    """Overrides applied to one editor pass. All optional; None means "as before"."""

    caption_scale: float | None = None
    caption_position: str | None = None

    #: True/False force the branded end card on or off. None leaves it to the
    #: usual rule (shown when the pharmacy has a name and the clip is long
    #: enough).
    end_card: bool | None = None

    #: Which line sits under the pharmacy name. "none" leaves the name alone.
    end_card_detail: str | None = None

    fade_seconds: float | None = None

    #: The pharmacist reported a caption shown twice. Repeats are dropped on
    #: every cut now (`nodes.caption_flashes`), so this changes no setting — it
    #: is what makes the request something a re-cut can answer, and lets the
    #: worker check the video actually has a repeat before promising a fix.
    remove_repeated_captions: bool | None = None

    #: Anything the instruction asked for that this set cannot express. Carried
    #: so the caller can tell the pharmacist plainly instead of pretending.
    unsupported: list[str] = field(default_factory=list)

    def is_empty(self) -> bool:
        """True when nothing would actually change."""
        return all(
            getattr(self, f) is None
            for f in ("caption_scale", "caption_position", "end_card",
                      "end_card_detail", "fade_seconds", "remove_repeated_captions")
        )

    def to_dict(self) -> dict[str, Any]:
        return {k: v for k, v in asdict(self).items() if v not in (None, [])}


SYSTEM = """\
You convert a pharmacist's request into settings for a video's final edit.

You may set only these. Anything else is not yours to change.

- caption_scale: number 0.6-1.6. 1.0 is current size. Bigger number, bigger text.
- caption_position: "top", "middle" or "bottom" — where subtitles sit.
- end_card: true or false — whether the closing card with the pharmacy's name
  and contact line is shown.
- end_card_detail: "booking", "phone", "address" or "none" — which single line
  appears under the pharmacy name on that card.
- fade_seconds: number 0-1.5 — how long shots fade in and out.
- remove_repeated_captions: true — when the request says a caption is
  duplicated, repeated or shown twice.

Set ONLY what the request actually asks for. Leave everything else out.

If part of the request is about something not in that list — the spoken words,
the voice, which footage was used, music, length — put a short plain description
of that part in "unsupported". Never invent a setting to cover it.

Reply with JSON only, for example:
{"caption_scale": 1.3, "unsupported": []}
{"end_card_detail": "phone", "unsupported": []}
{"unsupported": ["adding background music"]}
"""


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def _number(raw: Any) -> float | None:
    if isinstance(raw, bool) or raw is None:
        return None
    try:
        return float(raw)
    except (TypeError, ValueError):
        return None


def parse(payload: dict[str, Any]) -> EditorOptions:
    """Validate a model's reply into bounded options. Never raises.

    Every field is checked independently, so one nonsensical value costs that
    setting and not the whole refine. A value out of range is clamped rather
    than dropped: somebody who asks for enormous captions wants bigger captions,
    and the largest readable size is a better answer than no change at all.
    """
    options = EditorOptions()

    scale = _number(payload.get("caption_scale"))
    if scale is not None:
        clamped = _clamp(scale, CAPTION_SCALE_MIN, CAPTION_SCALE_MAX)
        if clamped != scale:
            log.info("Caption scale %.2f clamped to %.2f.", scale, clamped)
        options.caption_scale = clamped

    position = payload.get("caption_position")
    if isinstance(position, str) and position.strip().lower() in CAPTION_POSITIONS:
        options.caption_position = position.strip().lower()

    end_card = payload.get("end_card")
    if isinstance(end_card, bool):
        options.end_card = end_card

    detail = payload.get("end_card_detail")
    if isinstance(detail, str) and detail.strip().lower() in END_CARD_DETAILS:
        options.end_card_detail = detail.strip().lower()

    if payload.get("remove_repeated_captions") is True:
        options.remove_repeated_captions = True

    fade = _number(payload.get("fade_seconds"))
    if fade is not None:
        options.fade_seconds = _clamp(fade, FADE_MIN, FADE_MAX)

    raw_unsupported = payload.get("unsupported")
    if isinstance(raw_unsupported, list):
        options.unsupported = [
            str(item).strip()[:200] for item in raw_unsupported
            if isinstance(item, (str, int, float)) and str(item).strip()
        ][:5]

    return options


def from_document(raw: Any) -> EditorOptions:
    """Rebuild options stored on the video document. Tolerates anything."""
    return parse(raw) if isinstance(raw, dict) else EditorOptions()


def _extract_json(text: str) -> dict[str, Any]:
    if not isinstance(text, str):
        return {}
    fenced = re.search(r"```(?:json)?\s*(.+?)```", text, re.S)
    if fenced:
        text = fenced.group(1)
    start, end = text.find("{"), text.rfind("}")
    if start < 0 or end <= start:
        return {}
    try:
        parsed = json.loads(text[start : end + 1])
    except (ValueError, TypeError):
        return {}
    return parsed if isinstance(parsed, dict) else {}


def interpret(
    instruction: str,
    model: str | None = None,
    ledger: Any | None = None,
) -> EditorOptions:
    """Turn a request into editor settings. Never raises.

    `ledger` records what the interpretation itself cost. It is a small model
    call, but an editor refine is advertised as free and there are two of these
    on the path — this one and the classification before it. "Free" that is
    asserted rather than measured is the sort of claim the whole usage ledger
    exists to stop us making.

    A failure returns empty options, and empty options mean the caller must not
    run the refine — re-cutting with no changes would burn a slot to hand back
    an identical file. `EditorOptions.is_empty` is how the caller tells.
    """
    text = (instruction or "").strip()
    if not text:
        return EditorOptions()

    try:
        from langchain_anthropic import ChatAnthropic

        from app.llm import anthropic_chat, text_of
        from ..config import get_settings

        settings = get_settings()
        chosen = (model or settings.anthropic_model or "").strip()
        if not chosen:
            log.warning("Editor options skipped: no Anthropic model configured.")
            return EditorOptions()

        # No temperature: newer Claude models refuse it with a 400. See
        # app/llm.py — determinism here comes from the strict parser below,
        # never from a temperature of zero.
        llm = anthropic_chat(
            chosen, max_tokens=400, timeout=30,
            api_key=settings.anthropic_api_key,
        )
        reply = llm.invoke(
            [{"role": "system", "content": SYSTEM}, {"role": "user", "content": text}]
        )

        if ledger is not None:
            from . import usage as _usage

            inp, out = _usage.tokens_from(reply)
            ledger.record(_usage.ANTHROPIC, _usage.INPUT_TOKENS, inp,
                          settings.anthropic_usd_per_input_token)
            ledger.record(_usage.ANTHROPIC, _usage.OUTPUT_TOKENS, out,
                          settings.anthropic_usd_per_output_token)

        return parse(_extract_json(text_of(reply)))
    except Exception as exc:  # noqa: BLE001
        log.warning("Could not interpret editor instruction (%s).", exc)
        return EditorOptions()

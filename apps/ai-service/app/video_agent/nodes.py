"""
LangGraph node implementations. Each is a closure over UserContext so the
graph stays a pure function of (state, ctx).

Ported from the Yami Social project's agent/nodes.py — same six-stage
model (researcher → scriptwriter → voice → visuals → editor) but the
prompts are pharmacy-marketing-tuned and the module reads config through
Marketing25's Settings singleton rather than Yami's `settings` import.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import random
import re
import shutil
import tempfile
import time
from pathlib import Path
from typing import Any, Callable

import requests
from langchain_anthropic import ChatAnthropic
from langchain_community.tools.tavily_search import TavilySearchResults

from ..config import get_settings
from app.llm import anthropic_chat, text_of
from . import beats
from .context import UserContext

log = logging.getLogger(__name__)

# ─── Pillow / MoviePy compatibility (Pillow 10 removed ANTIALIAS) ───────
from PIL import Image as _PILImage  # noqa: E402

if not hasattr(_PILImage, "ANTIALIAS"):
    try:
        _PILImage.ANTIALIAS = _PILImage.Resampling.LANCZOS  # type: ignore[attr-defined]
    except AttributeError:
        _PILImage.ANTIALIAS = _PILImage.LANCZOS  # type: ignore[attr-defined]

from moviepy.config import change_settings  # noqa: E402

_magick = os.getenv("IMAGEMAGICK_BINARY") or shutil.which("magick") or shutil.which("convert")
if _magick:
    change_settings({"IMAGEMAGICK_BINARY": _magick})

from moviepy.editor import (  # noqa: E402
    AudioFileClip,
    ColorClip,
    CompositeAudioClip,
    CompositeVideoClip,
    TextClip,
    VideoFileClip,
    concatenate_videoclips,
)
from moviepy.video.fx.all import crop, fadein, fadeout, loop, resize  # noqa: E402


ProgressCallback = Callable[[str, int, dict[str, Any] | None], None]


def _noop_progress(stage: str, pct: int, extra: dict[str, Any] | None = None) -> None:
    pass


class NonRetriablePipelineError(RuntimeError):
    """Raised when a node has already tried every fallback — a retry
    would only re-burn upstream Claude credits without any chance of
    succeeding. The worker loop skips retries when it sees this type."""


# ─── LLM + helpers ─────────────────────────────────────────────────────
def _llm(ctx: UserContext, max_tokens: int = 4096) -> ChatAnthropic:
    """The model this job runs on.

    No ``temperature`` — see ``app/llm.py``. This passed 0.8 until
    ``claude-sonnet-5`` started refusing it with a 400, which killed every
    render at the researcher, the first model call in the graph.
    """
    return anthropic_chat(ctx.model, max_tokens=max_tokens)


def _meter_llm(ctx: UserContext, reply: Any) -> Any:
    """Record what one model call cost, then hand the reply straight back.

    Written to wrap an invoke — `resp = _meter_llm(ctx, _llm(ctx).invoke(...))`
    — so metering a call site is one edit and cannot change what it returns.
    Silent when the job carries no ledger, and when the provider did not report
    usage: a zero would read as a free call rather than an unmeasured one.
    """
    try:
        if ctx.usage is not None:
            from . import usage as _usage

            s = get_settings()
            inp, out = _usage.tokens_from(reply)
            ctx.usage.record(_usage.ANTHROPIC, _usage.INPUT_TOKENS, inp,
                             s.anthropic_usd_per_input_token)
            ctx.usage.record(_usage.ANTHROPIC, _usage.OUTPUT_TOKENS, out,
                             s.anthropic_usd_per_output_token)
    except Exception as exc:  # noqa: BLE001
        log.warning("Could not meter a model call: %s", exc)
    return reply


def _extract_json(text: str) -> Any:
    fence = re.search(r"```(?:json)?\s*(.*?)```", text, re.DOTALL | re.IGNORECASE)
    if fence:
        try:
            return json.loads(fence.group(1).strip())
        except json.JSONDecodeError:
            pass
    for o, c in (("{", "}"), ("[", "]")):
        s, e = text.find(o), text.rfind(c)
        if s != -1 and e > s:
            try:
                return json.loads(text[s : e + 1])
            except json.JSONDecodeError:
                continue
    return json.loads(text)


def _workdir(ctx: UserContext) -> Path:
    p = Path(tempfile.gettempdir()) / "marketing25" / ctx.job_id
    p.mkdir(parents=True, exist_ok=True)
    return p


# ═══════════════════════════════════════════════════════════════════════
# Researcher
# ═══════════════════════════════════════════════════════════════════════
RESEARCHER_SYSTEM = """You are the Head of Research for a community pharmacy's short-form video channel.

The channel's NICHE is community pharmacy marketing. The TONE is provided in the \
user message. Distill raw research into SHARP, SPECIFIC, TRUSTWORTHY facts that \
help pharmacies communicate to patients. Facts should feel:
- Actionable (something a patient can do today)
- Evidence-based (cite a reputable source)
- Locally relevant (independent pharmacies, community care)

Rules:
- Every fact must be concrete (names, numbers, brands, dates, symptoms, dosages).
- No generic filler.
- No specific medical advice that would require a prescriber — keep it educational.
- Cite source URL.
- Score 0..1 by how well the fact fits pharmacy marketing + the tone.

Return STRICT JSON:
{"facts":[{"fact":"string","source":"string","url":"string","score":0.0}]}
"""


def make_researcher(ctx: UserContext, progress: ProgressCallback = _noop_progress):
    def node(state: dict) -> dict:
        progress("researching", 5, None)
        s = get_settings()
        topic, retries = state.get("topic", ctx.topic), state.get("research_retries", 0)
        queries = [
            f"{topic} pharmacist tips",
            f"{topic} patient education",
            f"{topic} community pharmacy 2025",
            f"trending {topic} health news 2025",
        ]
        if retries:
            queries = [q + f" evidence variant {retries}" for q in queries]

        tool = TavilySearchResults(
            api_key=s.tavily_api_key,
            max_results=5,
            search_depth="advanced",
        )
        raw: list[dict] = []
        searches = 0
        for q in queries:
            try:
                raw.extend(tool.invoke({"query": q}))
                # Counted only when it answered. A refused or failed search is
                # not billed, and counting it would overstate the one number
                # the charge policy is waiting on.
                searches += 1
            except Exception as exc:  # noqa: BLE001
                log.warning("Tavily failed (%s): %s", q, exc)

        if ctx.usage is not None:
            from . import usage as _usage
            ctx.usage.record(_usage.TAVILY, _usage.SEARCHES, searches,
                             s.tavily_usd_per_search)

        # Tavily's results are not reliably dictionaries.
        #
        # `TavilySearchResults` returns a list of {title, url, content} maps in
        # most versions and a list of plain strings in others — and when it
        # returns strings, `r.get("url")` raises AttributeError inside the
        # researcher, which is the first node in the graph. Every render dies
        # before writing a word of script, and the traceback names the line
        # rather than the cause.
        #
        # Normalised to dictionaries here rather than guarded at each of the
        # three uses below, so the shape is settled once. A bare string has no
        # URL to dedupe on, so it keeps its position and is carried as content.
        seen, deduped = set(), []
        for r in raw:
            if isinstance(r, str):
                text = r.strip()
                if text:
                    deduped.append({"title": "", "url": "", "content": text})
                continue
            if not isinstance(r, dict):
                log.warning(
                    "Ignoring a search result of unexpected type %s", type(r).__name__)
                continue

            url = r.get("url")
            if not url:
                deduped.append(r)          # usable content, nothing to dedupe on
            elif url not in seen:
                seen.add(url)
                deduped.append(r)
        compressed = "\n\n".join(
            f"[{i+1}] {r.get('title','')}\nURL: {r.get('url','')}\n{(r.get('content','') or '')[:600]}"
            for i, r in enumerate(deduped[:20])
        )

        prompt = (
            f"NICHE: {ctx.niche}\nTONE: {ctx.style_tone}\nTOPIC: {topic}\n\n"
            f"RESEARCH:\n{compressed}\n\n"
            f"Distill 7 punchy, patient-friendly facts. JSON only."
        )
        resp = _meter_llm(ctx, _llm(ctx).invoke([
            {"role": "system", "content": RESEARCHER_SYSTEM},
            {"role": "user", "content": prompt},
        ]))
        try:
            facts = _extract_json(text_of(resp)).get("facts", [])
        except Exception:
            facts = []
        progress("researching", 15, {"facts": len(facts)})
        return {**state, "facts": facts, "research_retries": retries + 1}

    return node


def research_quality_score(state: dict) -> float:
    facts = state.get("facts", [])
    if not facts:
        return 0.0
    return sum(float(f.get("score", 0)) for f in facts) / len(facts)


# ═══════════════════════════════════════════════════════════════════════
# Scriptwriter
# ═══════════════════════════════════════════════════════════════════════
#: Words of narration per minute, measured for Edge-TTS at rate="-5%".
NARRATION_WPM = 145


def narration_word_budget(duration_seconds: int) -> tuple[int, int]:
    """How many words of narration fit in a clip of this length.

    Derived, never tabulated. The two facts — "a Short is 60 seconds" and "this
    clip is 15 seconds" — used to live in two different prompts, and the model
    resolved the contradiction by writing for something in between. Deriving one
    from the other means a change to clip length cannot leave a word count
    behind.

    The ceiling is only 10% over the nominal count because overshoot is not
    trimmed anywhere downstream: the video is exactly as long as the narration,
    so every extra word is b-roll fetched, frames composited, seconds encoded
    and megabytes stored.
    """
    words = max(12, round(duration_seconds * NARRATION_WPM / 60))
    return round(words * 0.85), round(words * 1.1)


def spoken_words(script: str) -> int:
    """Words the voice will actually say: stage directions in [] and () are not spoken."""
    return len(_clean_script(script or "").split())


def over_budget(script: str, ceiling: int) -> bool:
    """True when a script would run past its clip length.

    A little slack (5%) over the ceiling so a one-word overshoot does not cost a
    second model call; the ceiling already sits 10% above the nominal count.
    """
    return spoken_words(script) > ceiling * 1.05


TIGHTEN_SYS = """You shorten a pharmacy video script that is too long for its clip.

You are given the script as JSON and a hard word ceiling for the spoken
narration. Return the same JSON shape with the narration cut to the ceiling or
under:
- Keep the hook (first line) and the call to action (last line).
- Keep EXACTLY the same number of segments. Each segment is a shot on screen:
  shorten the text inside each one, never merge two segments or drop one.
- Keep every fact true to the original; do not add claims.
- "script" must be exactly the segment texts joined in order.
- Keep "title", "description" and "tags" unless a cut makes them wrong.

JSON only."""


SHORTS_SYS = """You are the head writer for a community pharmacy's short-form video channel.

The user message tells you the NICHE and TONE. Every video's job is to be:
- Trustworthy (source-backed, no scaremongering, no diagnosis)
- Practical (a patient watching can do something with it today)
- Warm (a pharmacist you already know, not a corporate ad)

Constraints:
- First 3 seconds = a specific, patient-relevant hook (not a fear cliché).
- Write to the WORD BUDGET given in the user message. It is derived from the
  clip length and is not negotiable: the finished video is exactly as long as
  the narration, so an over-long script does not get trimmed — it makes a
  longer, more expensive video than the one that was ordered.
- End with a soft CTA — "ask your pharmacist", "visit us", "book a flu jab" —
  matching the tone.
- Write EXACTLY the number of segments given in the user message. Each
  segment is one shot on screen, so fewer segments is a slower, emptier video,
  not a tighter one. Keep segments roughly equal in length.

VISUAL KEYWORD RULES (very important — this drives our stock-footage search):
- Each segment gets ONE visual_keyword.
- Keywords must be CONCRETE, VISUAL, and DIFFERENT from each other.
  A video with 6 segments MUST have 6 DIFFERENT keywords — no repeats.
- Tie each keyword to what THIS specific segment is talking about — a
  segment about elderly patients should evoke elderly imagery; a segment
  about children should evoke children; a segment about medication
  should evoke pills or blister packs, etc.
- Pull from a wide vocabulary — inhaler, pill organiser, hand sanitiser
  dispenser, doctor's referral note, thermometer, waiting room chair,
  patient consultation room, pharmacist white coat, blood pressure cuff,
  travel vaccine leaflet, elderly patient reading label, mother holding
  child, syringe close up, medicine label macro, prescription bag,
  glucose meter, allergy tablets, first aid kit, sanitiser bottle, etc.
  Prefer specific over generic every time.
- Forbidden as keywords: "video", "scene", "shot", "person", "pharmacy"
  alone (too generic), and any keyword you've already used in an
  earlier segment of THIS script.

Never give dosing instructions. Never say "you should take". Frame as
"your pharmacist can help you decide" instead.

Output STRICT JSON:
{
 "title": "string (<=70 chars)",
 "hook": "string (<=18 words)",
 "script": "string with [0:00] markers",
 "segments": [{"start":0.0,"end":3.0,"text":"...","visual_keyword":"..."}],
 "description": "string with #hashtags",
 "tags": ["string"],
 "cta": "string"
}
"""


# Used ONLY in "custom" mode. The narration text comes from the user —
# we still need title/description/tags/segments for downstream nodes.
CUSTOM_META_SYS = """You are extracting metadata for a user-supplied pharmacy marketing script.

You will receive: NICHE, TONE, TOPIC, and the FULL SCRIPT the creator
already wrote. DO NOT rewrite or "improve" the script. Produce:
  - title (<=70 chars), tied to the script's actual content
  - hook (<=18 words) — the first attention-grabbing line from the script
  - segments — split the script into the number of beats given (SEGMENTS),
    in order, without changing a word. Each beat gets a
    concrete pharmacy-adjacent `visual_keyword` that drives the stock-
    footage search. Generic words like "video", "scene", "shot" are
    forbidden.
  - description with #hashtags matched to community pharmacy
  - tags (3–15 short tags, community pharmacy relevant)
  - cta — pull a short call-to-action from the script's ending, or
    invent one if the script has none ("Ask your pharmacist today").

Output STRICT JSON:
{
 "title": "string", "hook": "string",
 "segments": [{"start":0.0,"end":3.0,"text":"...","visual_keyword":"..."}],
 "description": "string", "tags": ["string"], "cta": "string"
}
"""


def _within_budget(ctx: UserContext, parsed: dict, ceiling: int) -> dict:
    """The script, tightened once if the writer ran past its word ceiling.

    <b>Enforced, not requested.</b> The prompt states the budget, and the
    writer still overran it: production renders came back at 22.8s for a 15s
    clip — about 54 spoken words against a ceiling of 40. A model does not count
    words reliably, and nothing downstream trims a long script: the video is as
    long as its narration, so every extra word is b-roll, compositing, encoding,
    storage and render time.

    One tightening call, only when the budget is broken. If that fails or comes
    back longer, the shorter of the two is kept — a long video is a cost
    problem, a failed render is a lost video.
    """
    script = str(parsed.get("script") or "")
    if not script or not over_budget(script, ceiling):
        return parsed

    before = spoken_words(script)
    try:
        resp = _meter_llm(ctx, _llm(ctx).invoke([
            {"role": "system", "content": TIGHTEN_SYS},
            {"role": "user", "content": (
                f"WORD CEILING: {ceiling} spoken words.\n"
                f"CURRENT: {before} words.\n\n"
                f"{json.dumps(parsed, ensure_ascii=False)}"
            )},
        ]))
        tightened = _extract_json(text_of(resp))
    except Exception as exc:  # noqa: BLE001
        log.warning("Script over budget (%d > %d words); tightening failed: %s",
                    before, ceiling, exc)
        return parsed

    after = spoken_words(str(tightened.get("script") or ""))
    segments = tightened.get("segments")
    if not after or after >= before or not isinstance(segments, list) or not segments:
        log.warning("Script over budget (%d > %d words); tightening did not help (%d).",
                    before, ceiling, after)
        return parsed

    # The voice speaks `script`; the captions and shot timings come from
    # `segments`. A reply that shortened one and not the other would put
    # captions on screen for words nobody says, so both must agree.
    captioned = sum(spoken_words(str(seg.get("text") or ""))
                    for seg in segments if isinstance(seg, dict))
    if abs(captioned - after) > max(3, after * 0.15):
        log.warning(
            "Script tightening refused: the script has %d words but its segments "
            "carry %d. Keeping the original (%d words).", after, captioned, before)
        return parsed

    log.info("Script tightened from %d to %d words (ceiling %d).", before, after, ceiling)
    return {**parsed, **tightened}


def make_scriptwriter(ctx: UserContext, progress: ProgressCallback = _noop_progress):
    def node(state: dict) -> dict:
        progress("scripting", 25, None)
        mode = (ctx.script_source or "ai").lower()
        # One shot per segment: the count is decided here, from the length,
        # not left to the writer. See beats.shot_count.
        wanted = beats.shot_count(ctx.duration_seconds)

        # ─── custom: user supplied the full narration ─────────────────
        if mode == "custom" and ctx.custom_script:
            user_prompt = (
                f"NICHE: {ctx.niche}\nTONE: {ctx.style_tone}\n"
                f"TOPIC: {state.get('topic')}\n\n"
                f"SEGMENTS: {wanted}\n\n"
                f"FULL SCRIPT (verbatim — do not rewrite):\n"
                f"{ctx.custom_script}\n\n"
                f"Extract metadata only. JSON only."
            )
            resp = _meter_llm(ctx, _llm(ctx).invoke([
                {"role": "system", "content": CUSTOM_META_SYS},
                {"role": "user", "content": user_prompt},
            ]))
            parsed = _extract_json(text_of(resp))
            tags = list(dict.fromkeys(parsed.get("tags") or []))[:30]
            progress("scripting", 35, {"title": parsed.get("title"), "mode": "custom"})
            custom_segments = beats.ensure_shot_count(parsed.get("segments") or [], wanted)
            return {
                **state,
                "script": ctx.custom_script,
                "title": parsed.get("title"),
                "description": parsed.get("description"),
                "tags": tags,
                "metadata": {
                    "hook": parsed.get("hook"),
                    "cta": parsed.get("cta"),
                    "segments": custom_segments,
                    "mode": ctx.video_mode,
                    "script_source": "custom",
                },
            }

        # ─── ai / guided: AI writes from topic (+ optional guidance) ──
        facts = state.get("facts", [])
        fact_lines = "\n".join(
            f"- {f.get('fact','')} (src={f.get('source','')})" for f in facts
        )
        # Named so the closing line can be "come and see us at X" rather than a
        # generic "visit your local pharmacy" — and, more to the point, so the
        # narration and the end card agree. A video that says one name while the
        # card underneath shows another is worse than an unbranded one.
        #
        # A hint, not an instruction to recite it. The pharmacy is already on
        # screen in the last three seconds; saying it twice in fifteen seconds
        # is an advert nobody watches to the end.
        brand_block = ""
        if ctx.brand is not None and ctx.brand.name:
            brand_block = (
                f"\n\nPHARMACY: {ctx.brand.name}"
                "\nYou may name the pharmacy once in the closing call to action. "
                "Do not invent any other detail about it — no address, no opening "
                "hours, no services it has not been said to offer."
            )

        guidance_block = ""
        if mode == "guided" and ctx.description:
            guidance_block = (
                "\n\n=== CREATOR GUIDANCE (MUST FOLLOW) ===\n"
                f"{ctx.description}\n"
                "=== END CREATOR GUIDANCE ===\n"
            )
        # The word budget, derived from the length that was actually ordered.
        #
        # <b>This is why renders took twice as long as they should.</b> The
        # system prompt used to carry "140-160 words for a 60s Short" as a fixed
        # constraint while the user prompt asked for a 15s clip. Given a hard
        # word count and a contradictory duration, the model split the
        # difference: a measured render produced 26.7s of narration for a clip
        # the product calls 15 seconds, on every screen and in the domain
        # default.
        #
        # Nothing downstream trims it — `total = voice.duration + 0.5` — so the
        # extra twelve seconds are paid for twice over, in b-roll fetched,
        # frames composited, seconds encoded, megabytes uploaded and stored. The
        # encode alone was 91s of a 208s render.
        #
        # 145 words per minute is a measured narration rate for Edge-TTS at the
        # rate="-5%" this pipeline uses. Derived rather than tabulated, so
        # changing the clip length changes the script with it — a second
        # hard-coded pair is how the first one got out of step.
        low, high = narration_word_budget(ctx.duration_seconds)

        user_prompt = (
            f"NICHE: {ctx.niche}\nTONE: {ctx.style_tone}\n"
            f"TOPIC: {state.get('topic')}{brand_block}{guidance_block}\n\n"
            f"FACTS:\n{fact_lines}\n\n"
            f"WORD BUDGET: {low}-{high} words of spoken narration, total, across "
            f"all segments. This is a {ctx.duration_seconds}-second clip and the "
            f"narration decides its length — {high} words is the ceiling, not a "
            f"target to approach from above.\n\n"
            f"SEGMENTS: exactly {wanted}, about {ctx.duration_seconds / wanted:.1f}s and "
            f"{max(1, high // wanted)} words each. Each segment is one shot.\n\n"
            f"Write the {ctx.video_mode} script for a {ctx.duration_seconds}s clip. JSON only."
        )
        resp = _meter_llm(ctx, _llm(ctx).invoke([
            {"role": "system", "content": SHORTS_SYS},
            {"role": "user", "content": user_prompt},
        ]))
        parsed = _extract_json(text_of(resp))
        parsed = _within_budget(ctx, parsed, high)
        # The prompt asks for the count; this makes sure of it. A writer that
        # returns three moments, or a tightening that merged two, would
        # otherwise ship a video with three shots.
        before = len(parsed.get("segments") or [])
        ai_segments = beats.ensure_shot_count(parsed.get("segments") or [], wanted)
        if len(ai_segments) != before:
            log.info("Script came back with %d segment(s); split to %d.", before, len(ai_segments))
        tags = list(dict.fromkeys(parsed.get("tags") or []))[:30]
        progress("scripting", 35, {"title": parsed.get("title"), "mode": mode})
        return {
            **state,
            "script": parsed.get("script", ""),
            "title": parsed.get("title"),
            "description": parsed.get("description"),
            "tags": tags,
            "metadata": {
                "hook": parsed.get("hook"),
                "cta": parsed.get("cta"),
                "segments": ai_segments,
                "mode": ctx.video_mode,
                "script_source": mode,
            },
        }

    return node


# ═══════════════════════════════════════════════════════════════════════
# Voice (Edge-TTS default, ElevenLabs optional)
# ═══════════════════════════════════════════════════════════════════════
def _clean_script(script: str) -> str:
    s = re.sub(r"\[[^\]]+\]", "", script)
    s = re.sub(r"\([^)]+\)", "", s)
    return re.sub(r"\s+", " ", s).strip()


EDGE_TTS_DEFAULT_VOICE = "en-US-JennyNeural"
EDGE_TTS_FALLBACK_VOICES = [
    EDGE_TTS_DEFAULT_VOICE,
    "en-US-AriaNeural",
    "en-US-GuyNeural",
    "en-GB-SoniaNeural",
]


def _sanitize_tts_text(text: str) -> str:
    """Strip chars observed to cause edge-tts to return NoAudioReceived."""
    if not text:
        return ""
    text = re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]", "", text)
    text = re.sub(r"[​-‏‪-‮⁠-⁯﻿]", "", text)
    text = re.sub(
        r"[\U0001F300-\U0001F9FF\U0001FA00-\U0001FAFF\U00002600-\U000027BF]",
        "", text,
    )
    text = re.sub(r"([!?.,;:])\1{4,}", r"\1\1\1", text)
    return re.sub(r"\s+", " ", text).strip()


def make_voice(ctx: UserContext, progress: ProgressCallback = _noop_progress):
    def node(state: dict) -> dict:
        progress("voicing", 45, None)
        raw_text = _clean_script(state.get("script", ""))
        text = _sanitize_tts_text(raw_text)
        if not text or len(text) < 20:
            log.error(
                "Voice node: cleaned text too short (%d chars). Raw script %d chars.",
                len(text), len(raw_text),
            )
            raise RuntimeError("voice synthesis input was empty or too short")

        out = _workdir(ctx) / f"voice_{int(time.time())}.mp3"

        from ..config import get_settings
        from .elevenlabs_tts import (
            ElevenLabsError,
            resolve_default_voice,
            synthesize as eleven_synth,
        )
        from . import tts_policy

        choice = tts_policy.choose(
            ctx.voice_id, key_configured=bool(get_settings().elevenlabs_api_key)
        )
        log.info("Narration provider: %s — %s", choice.provider, choice.reason)

        if choice.provider == "elevenlabs":
            # No fallback, deliberately. Falling through to Edge-TTS here is what
            # turned "the ElevenLabs key is wrong" into "voice synthesis failed
            # across 4 voices" on every production render — a real, fixable
            # provider error replaced by a confusing one from a service we did
            # not intend to use. The pharmacist still sees a friendly sentence;
            # what changes is that the operator now gets ElevenLabs' own words.
            try:
                voice = choice.voice or resolve_default_voice()
                eleven_synth(text, voice, str(out), ledger=ctx.usage)
            except ElevenLabsError as exc:
                raise NonRetriablePipelineError(f"ElevenLabs could not narrate this: {exc}") from exc

            if not out.exists() or out.stat().st_size < 5000:
                raise NonRetriablePipelineError(
                    "ElevenLabs returned audio that is empty or too short to use."
                )
            progress("voicing", 55, None)
            return {**state, "audio_path": str(out), "spoken_words": len(text.split())}

        # Edge-TTS: the local path, when no ElevenLabs key is configured.
        import edge_tts

        chosen = choice.voice or EDGE_TTS_DEFAULT_VOICE
        voice_attempts: list[str] = []
        for v in [chosen] + EDGE_TTS_FALLBACK_VOICES:
            if v and v not in voice_attempts:
                voice_attempts.append(v)

        last_exc: Exception | None = None
        success_voice: str | None = None
        for attempt_idx, edge_voice in enumerate(voice_attempts):
            async def _run(v=edge_voice):
                c = edge_tts.Communicate(text=text, voice=v, rate="-5%", pitch="-2Hz")
                await c.save(str(out))

            try:
                asyncio.run(_run())
                success_voice = edge_voice
                if attempt_idx > 0:
                    log.warning(
                        "Edge-TTS recovered with fallback voice %r (after %d failed attempts)",
                        edge_voice, attempt_idx,
                    )
                break
            except Exception as exc:  # noqa: BLE001
                last_exc = exc
                log.warning(
                    "Edge-TTS attempt %d/%d failed (voice=%r): %s",
                    attempt_idx + 1, len(voice_attempts), edge_voice, exc,
                )
                try:
                    if out.exists():
                        out.unlink()
                except Exception:
                    pass
                continue

        if success_voice is None:
            # Carries the last provider error rather than only the count. On a
            # host where Edge-TTS is blocked outright this is the line that says
            # so — and the fix is to set ELEVENLABS_API_KEY, which the message
            # names, because that is the supported path off a developer machine.
            raise NonRetriablePipelineError(
                f"Edge-TTS failed across {len(voice_attempts)} voices "
                f"({type(last_exc).__name__}: {last_exc}). Microsoft refuses Edge-TTS from "
                "datacentre networks, and since autumn 2026 intermittently from home "
                "connections too (edge-tts issue #490) — set ELEVENLABS_API_KEY; locally as "
                "well if renders need to work."
            ) from last_exc

        if not out.exists() or out.stat().st_size < 5000:
            raise RuntimeError("voice synthesis produced empty audio")
        progress("voicing", 55, None)
        # The words actually handed to the synthesiser, not the raw script.
        # Counted here because this is the only place that has the exact string
        # that was spoken — the editor sees a duration and would otherwise have
        # to guess what produced it.
        return {**state, "audio_path": str(out), "spoken_words": len(text.split())}

    return node


# ═══════════════════════════════════════════════════════════════════════
# Visuals — Pexels stock footage
# ═══════════════════════════════════════════════════════════════════════
PEXELS_SEARCH = "https://api.pexels.com/videos/search"

# Pharmacy-tuned fallback queries. Kept as an explicit list because the
# script may fail to produce visual_keywords, and we don't want the
# stock-footage search to fall back to "abstract" and get random noise.
PHARMACY_FALLBACK_QUERIES = [
    "pharmacist counselling patient",
    "prescription bottle close up",
    "hands sanitizing medicine",
    "pharmacy counter dispensing",
    "flu vaccine syringe close up",
    "medication blister pack top view",
    "pharmacist smiling with tablet",
    "elderly patient at pharmacy",
    "pill organizer close up",
    "blood pressure monitor",
]


_TOPIC_STOPWORDS = {
    "the", "a", "an", "and", "or", "for", "to", "of", "in", "on", "at", "this",
    "that", "your", "our", "why", "how", "what", "when", "who", "do", "does",
    "did", "should", "can", "could", "is", "are", "be", "get", "got", "take",
    "taking", "week", "today", "now", "new", "about", "with", "from", "you",
    "we", "they", "it", "its", "their", "my",
}


def _topic_phrase(topic: str, limit: int = 3) -> str:
    """A few words naming what this video is actually about.

    Used to keep every stock search tied to the subject. It does not have to
    extract the topic perfectly — "free blood pressure" and "spot signs stroke"
    are both imperfect and both return footage about the right thing, which is
    the entire requirement. Chasing a cleaner phrase would mean an ever-growing
    list of words to ignore, which is its own kind of wrong.
    """
    words = [
        w for w in re.findall(r"[a-z][a-z-]+", (topic or "").lower())
        if w not in _TOPIC_STOPWORDS and len(w) > 2
    ]
    seen: set[str] = set()
    out: list[str] = []
    for w in words:
        if w in seen:
            continue
        seen.add(w)
        out.append(w)
        if len(out) == limit:
            break
    return " ".join(out)



def _beat_queries(keyword: str, topic: str, used: set[str]) -> list[str]:
    """The stock searches to try for one beat, best first.

    Pure, and separate from the loop that uses it, so the ordering can be
    tested without Pexels, a work directory or a UserContext — the test for it
    previously held its own copy of this list, which is the drift this codebase
    keeps being bitten by.

    Every entry names the video's subject or the beat's own keyword. There is
    deliberately no generic option at the end: a beat nothing matches is left
    empty and filled later from footage this video already has.
    """
    candidates: list[str] = []
    if keyword and keyword.lower() not in used:
        candidates.append(keyword)
    if keyword and topic:
        candidates.append(f"{keyword} {topic}")
    if topic:
        candidates.append(topic)
    if keyword:
        candidates.append(keyword)
    return list(dict.fromkeys(candidates))


def _pexels_search(q: str, orientation: str, per_page: int = 30) -> list[dict]:
    """Search Pexels for stock clips matching `q`, ranked by
    duration+orientation fit, then SHUFFLED within same-quality tiers.

    The shuffle is critical: without it, Pexels' top hit for a given
    keyword is always the same clip, so every video ends up with the
    same "pharmacist at counter" B-roll. By pulling from page 1-2 and
    shuffling within the acceptable pool, back-to-back videos with
    similar keywords now get visibly different footage.
    """
    s = get_settings()
    # Random page 1-2 so subsequent videos see a different candidate pool.
    page = random.randint(1, 2)
    r = requests.get(
        PEXELS_SEARCH,
        headers={"Authorization": s.pexels_api_key or ""},
        params={
            "query": q,
            "orientation": orientation,
            "per_page": per_page,
            "page": page,
            "size": "medium",
        },
        timeout=20,
    )
    r.raise_for_status()
    vids = r.json().get("videos", [])

    def score_tier(v: dict) -> int:
        """Return a bucket integer (higher = better) so we can shuffle
        within a tier while still preferring the good candidates."""
        dur = float(v.get("duration") or 0)
        if not (4.0 <= dur <= 30.0):
            return -1
        if 5.0 <= dur <= 12.0:
            duration_bonus = 100
        elif 4.0 <= dur < 5.0 or 12.0 < dur <= 18.0:
            duration_bonus = 50
        else:
            duration_bonus = 0
        w, h = int(v.get("width") or 0), int(v.get("height") or 0)
        if orientation == "portrait" and h > w:
            orient_bonus = 60
        elif orientation == "landscape" and w > h:
            orient_bonus = 60
        else:
            orient_bonus = 0
        return duration_bonus + orient_bonus

    scored = [(score_tier(v), v) for v in vids if score_tier(v) >= 0]
    # Group by tier bucket → shuffle within → emit tiers in descending order.
    from collections import defaultdict
    tiers: dict[int, list[dict]] = defaultdict(list)
    for tier, v in scored:
        tiers[tier].append(v)
    result: list[dict] = []
    for tier in sorted(tiers.keys(), reverse=True):
        bucket = tiers[tier]
        random.shuffle(bucket)
        result.extend(bucket)
    return result


def _best_file(video: dict, prefer_vertical: bool) -> dict | None:
    files = video.get("video_files") or []
    if not files:
        return None
    target_h = 1500 if prefer_vertical else 1080

    def score(f: dict) -> int:
        h, w = int(f.get("height") or 0), int(f.get("width") or 0)
        if h == 0 or w == 0:
            return -1000
        if h < 360:
            return -500
        orient_bonus = 100 if (prefer_vertical and h > w) or (not prefer_vertical and w > h) else 0
        return orient_bonus - abs(h - target_h) // 4

    return sorted(files, key=score, reverse=True)[0]


def _download_one_clip(
    out_dir: Path,
    query: str,
    orientation: str,
    prefer_vertical: bool,
    already_downloaded: set[str],
    sources: list[dict] | None = None,
    role: str = "aligned",
) -> str | None:
    """Fetch one stock clip. Records where it came from, if asked.

    `sources` collects `{path, pexels_id, link, query, role}` for every clip
    taken. `role` is "aligned" (paired with a script segment) or "extra" (fill
    pool), and a refine needs the distinction to rebuild the same edit — see the
    note where it is written.

    That reference is what makes refinement affordable. Keeping the clip bytes
    would be roughly 120 MB per video — twelve HD files — uploaded on every
    render for something most videos never need. The footage is public and free
    to fetch again, so a refine re-downloads from the same link and only the
    couple of kilobytes describing it are stored.
    """
    try:
        videos = _pexels_search(query, orientation)
    except Exception as exc:  # noqa: BLE001
        log.warning("Pexels failed (%s): %s", query, exc)
        return None

    for v in videos:
        vid_id = str(v.get("id", ""))
        if vid_id in already_downloaded:
            continue
        best = _best_file(v, prefer_vertical)
        if not best:
            continue
        fname = re.sub(r"[^\w\-]", "_", query)[:60] + f"_{vid_id}.mp4"
        fp = out_dir / fname
        if not fp.exists():
            try:
                with requests.get(best["link"], stream=True, timeout=60) as r:
                    r.raise_for_status()
                    with open(fp, "wb") as f:
                        for chunk in r.iter_content(1 << 16):
                            f.write(chunk)
            except Exception as exc:  # noqa: BLE001
                log.warning("Download failed (%s): %s", query, exc)
                continue
        already_downloaded.add(vid_id)
        if sources is not None:
            sources.append({
                "path": str(fp),
                "pexels_id": vid_id,
                # Both, deliberately. The link is what a refine uses; the id is
                # the fallback if Pexels ever moves its CDN, because the file can
                # be looked up again by id through the API.
                "link": best.get("link") or "",
                "query": query,
                # What is actually in the clip, from its Pexels page. The query
                # is what we asked for; this is what came back — and it is what
                # a pharmacist points at ("the person washing their hands").
                "shows": beats.describe(v.get("url")),
                # Which list this clip ended up in.
                #
                # A refine has to rebuild `aligned_clips` and `extra_clips`
                # separately — the editor pairs the nth aligned clip with the
                # nth script segment and treats extras as a fill pool, so
                # collapsing them reorders the video.
                #
                # It cannot be recovered from the order of this list. Aligned
                # clips are downloaded first, but a segment whose download fails
                # still appends a placeholder to `aligned_clips` while recording
                # nothing here, so position stops matching after the first
                # failure. Guessing from the order would silently shuffle the
                # footage of exactly the videos that had a rough first render.
                "role": role,
            })
        return str(fp)
    return None


def make_visuals(ctx: UserContext, progress: ProgressCallback = _noop_progress):
    style = (ctx.visual_style or "realistic").lower()

    def _pexels_node(state: dict) -> dict:
        return _collect_pexels_b_roll(ctx, state, progress)

    if style == "ai_imagery":
        def node(state: dict) -> dict:
            from . import ai_imagery
            if not ai_imagery.is_configured():
                log.warning(
                    "AI imagery requested but Replicate not configured — "
                    "falling back to Pexels for job %s", ctx.job_id,
                )
                return _pexels_node(state)
            try:
                return _collect_ai_imagery(ctx, state, progress)
            except Exception as exc:  # noqa: BLE001
                log.exception("AI imagery failed for job %s: %s", ctx.job_id, exc)
                return _pexels_node(state)
        return node

    def node(state: dict) -> dict:
        return _pexels_node(state)
    return node


def _photo_pins(ctx: UserContext, segments: list) -> dict[int, int]:
    """`{moment: image}` — the refine's answer if it gave one, else the plan."""
    images = list(ctx.user_images or [])
    if not images:
        return {}
    if ctx.user_pins is not None:
        return {m: i for m, i in ctx.user_pins.items()
                if 0 <= m < len(segments) and 0 <= i < len(images)}
    from . import user_media

    return user_media.plan_pins(len(segments), len(images))


def _pinned_user_clips(
    ctx: UserContext, segments: list, out_dir, target_size: tuple[int, int],
) -> tuple[dict[int, str], list[str]]:
    """The pharmacy's own pictures, as clips, keyed by the beat they take.

    Empty for almost every video. Called before any stock is fetched so a pinned
    beat never downloads a Pexels clip it would then throw away, and — on the
    AI-imagery path — never pays Replicate for an image it would not use.
    """
    images = list(ctx.user_images or [])
    if not images:
        return {}, []

    from . import ai_imagery, storage, user_media

    def duration_for(index: int) -> float:
        seg = segments[index] if index < len(segments) else {}
        try:
            span = float(seg.get("end", 0)) - float(seg.get("start", 0))
        except (TypeError, ValueError):
            span = 0.0
        return max(1.5, min(span or 3.5, 8.0))

    pins = _photo_pins(ctx, segments)
    return user_media.prepare_user_clips(
        images, pins, out_dir / "user", target_size, duration_for,
        download=storage.download_file,
        to_clip=ai_imagery.image_to_video_clip,
    )


def _collect_pexels_b_roll(ctx: UserContext, state: dict, progress: ProgressCallback) -> dict:
    progress("collecting", 65, None)
    meta = state.get("metadata") or {}
    segments = list(meta.get("segments") or [])

    orientation = "portrait" if ctx.video_mode == "shorts" else "landscape"
    prefer_vertical = orientation == "portrait"
    out_dir = _workdir(ctx) / "clips"
    out_dir.mkdir(exist_ok=True)

    # What this video is about, appended to any search that needs help.
    #
    # <b>This replaced a generic pharmacy list, and that list was the bug.</b>
    # A beat whose keyword was blank — or merely a repeat of an earlier one —
    # used to be given the next entry from PHARMACY_FALLBACK_QUERIES: "blood
    # pressure monitor", "pill organizer close up". Nothing tied those to the
    # subject, so a video about flu jabs quietly contained blood-pressure
    # footage and looked, to whoever watched it, like the wrong video.
    # `state` first, `ctx` second — the same order the researcher uses. On a
    # refine the state is rehydrated from the stored document, and reading only
    # `ctx` would search for whatever the context was built with.
    topic = _topic_phrase(state.get("topic") or ctx.topic)

    # On a refine, every clip the video already has. A kept moment keeps its
    # clip without downloading it, so without this a re-searched moment could
    # come back with the same footage — a duplicate — or with the very clip
    # the pharmacist asked to replace, which is a change that changes nothing.
    already_downloaded: set[str] = {
        str(r.get("pexels_id")) for r in (state.get("clip_sources") or [])
        if isinstance(r, dict) and r.get("pexels_id")
    }
    # Back-to-back segments must not share a clip. That is enforced by
    # `already_downloaded`, which is per *file* — so a repeated keyword now
    # simply returns a different clip for the same search rather than forcing a
    # different, unrelated subject.
    used_keywords: set[str] = set()
    # Beats that needed more than their own keyword, for the log.
    reached_for_topic: list[int] = []

    # Shots recovered from the previous render, to keep across a refine. Seeded
    # by `rehydrate` only when re-entering at visuals; empty on a first render.
    previous_aligned = list(state.get("previous_aligned") or [])
    previous_sources = list(state.get("previous_sources") or [])
    kept_shots = 0
    # The record of the clip each moment got for itself — downloaded or kept.
    # See `beats.record_by_beat`: moments are recorded by index, never by
    # position in a list, because position is exactly what drifted.
    own_records: dict[int, dict] = {}
    # Moments the pharmacist asked to change whose searches all missed. They
    # take a clip already in this video — never a spare, never a default.
    override_missed: list[int] = []
    aligned_clips: list[str] = []
    extras: list[str] = []
    clip_sources: list[dict] = []

    target_w, target_h = SHORTS_RES if ctx.video_mode == "shorts" else LONGFORM_RES
    user_clips, user_skipped = _pinned_user_clips(ctx, segments, out_dir, (target_w, target_h))

    for i, seg in enumerate(segments):
        # A beat the pharmacist pinned a photograph to. No stock is fetched for
        # it at all — the download is skipped rather than done and discarded.
        if i in user_clips:
            aligned_clips.append(user_clips[i])
            continue

        kw = (seg.get("visual_keyword") or "").strip()

        # A moment the pharmacist asked to re-fill.
        #
        # Their words replace the scriptwriter's keyword for this beat and
        # nothing else — the topic-qualified retries below still apply, because
        # "elderly couple walking park" can come back empty just as any keyword
        # can, and falling back to this beat's *original* footage would undo the
        # very change they asked for.
        override = (getattr(ctx, "shot_edits", None) or {}).get(i)
        if override:
            kw = override
            log.info("Segment %d re-filled on request: %r", i, override)
            # Exactly the order the pharmacist was promised: their words, then
            # their words with the subject. No subject-only search.
            candidates = beats.replacement_queries(override, topic)
        elif i < len(previous_aligned) and previous_aligned[i]:
            # <b>A moment nobody asked about keeps the shot it had.</b>
            #
            # `_pexels_search` takes a random page and shuffles within quality
            # tiers — on purpose, so two videos on the same topic do not look
            # identical. The consequence on a refine is that re-searching an
            # unchanged beat returns *different* footage, so asking to fix the
            # last shot would hand back a video in which every shot had changed.
            # Empty on a first render, so this costs nothing there.
            aligned_clips.append(previous_aligned[i])
            # <b>And its reference, or the next refine cannot keep it.</b>
            #
            # The worker rewrites `clipSources` from whatever this render
            # reports. A kept shot that contributes no record is a reference
            # silently dropped, so the *second* shot edit would recover only the
            # handful of beats the first one re-searched and re-shoot the rest —
            # the feature working once and then quietly stopping.
            if i < len(previous_sources) and previous_sources[i]:
                own_records[i] = previous_sources[i]
            kept_shots += 1
            continue

        # Every candidate names this video's subject. Tried in order, first hit
        # wins, and there is deliberately no generic option at the end: a beat
        # we cannot fill is left empty and filled afterwards from footage this
        # video already has, which is at worst repetitive and never off-topic.
        if not override:
            candidates = _beat_queries(kw, topic, used_keywords)

        path = None
        for attempt, query in enumerate(candidates):
            path = _download_one_clip(
                out_dir, query, orientation, prefer_vertical, already_downloaded,
                clip_sources)
            if path:
                # `_download_one_clip` appends its record last, just before
                # returning the path.
                own_records[i] = clip_sources[-1]
                used_keywords.add(query.lower())
                if attempt > 0:
                    reached_for_topic.append(i)
                break

        if path:
            aligned_clips.append(path)
        else:
            aligned_clips.append("")
            if override:
                override_missed.append(i)
            log.warning(
                "Segment %d → no clip found for any of %s", i, candidates)

    # A small spare pool, on the same subject as everything else.
    #
    # These used to come from the generic pharmacy list, which meant the clips
    # standing in for a missing beat were unrelated by construction — the same
    # fault as above, one step further along, and harder to see because these
    # are only used when something else has already gone wrong.
    # Only when there is a subject to spare clips *about*. With an empty topic
    # this built " pharmacy" and " health" — generic queries with a leading
    # space, which is the very thing this change removed, sneaking back in
    # through the one branch that looked harmless.
    spare_queries = (
        dict.fromkeys([topic, f"{topic} pharmacy", f"{topic} health"]) if topic else []
    )
    for q in spare_queries:
        path = _download_one_clip(
            out_dir, q, orientation, prefer_vertical, already_downloaded,
            clip_sources, role="extra")
        if path:
            extras.append(path)

    # Fill any beat still empty from footage this video already has.
    #
    # Repeating a shot is a cosmetic problem. Introducing a shot about a
    # different condition is a correctness one — a pharmacist showing patients a
    # flu video with blood-pressure footage in it has been given the wrong
    # video, and cannot tell why.
    # Stock only. The pharmacy's own photograph standing in for a failed search
    # would show it twice, at a moment it was not placed at — unless there is
    # nothing else at all.
    stock = [p for i, p in enumerate(aligned_clips) if p and i not in user_clips]
    pool = stock + extras
    if not pool:
        pool = [p for p in aligned_clips if p]
        stock = pool

    if not pool and state.get("clip_sources"):
        # A refine of a finished video with no footage at all to work with.
        # The generic pharmacy list below is for a first render with nothing
        # else; on a change the pharmacist asked for it would put unrelated
        # footage into a video they had approved. Failing leaves their video
        # exactly as it was, and says so.
        raise RuntimeError("No footage could be found for the shots being changed")

    if not pool:
        # Nothing at all matched the subject: a topic Pexels has no footage for,
        # or the API being down. Generic pharmacy footage is the last resort
        # before failing the render outright, and it is worth saying loudly that
        # it happened, because every clip in the finished video will be off
        # subject.
        log.warning(
            "No footage matched %r. Falling back to generic pharmacy stock — "
            "the whole video will be off subject.", ctx.topic,
        )
        for q in random.sample(PHARMACY_FALLBACK_QUERIES, k=min(3, len(PHARMACY_FALLBACK_QUERIES))):
            path = _download_one_clip(
                out_dir, q, orientation, prefer_vertical, already_downloaded,
                clip_sources, role="extra")
            if path:
                extras.append(path)
                pool.append(path)

    if not pool:
        raise RuntimeError("No B-roll downloaded")

    # Unused spares first, then the least-used clip that is not next door —
    # see `beats.fill_gaps`. This was `pool[i % len(pool)]` over a pool that
    # began with clips already on screen, which is how a failed search became
    # a duplicate shot.
    if override_missed and stock:
        # Their words found nothing: reuse a shot from elsewhere in this video
        # for those moments, least-used and not next door, before any spare.
        keep = "\0keep"
        masked = [p if p else ("" if i in override_missed else keep)
                  for i, p in enumerate(aligned_clips)]
        masked = beats.fill_gaps(masked, stock, [])
        aligned_clips = ["" if p == keep else p for p in masked]
        log.warning(
            "Stock had nothing for moments %s; reused footage from this video.",
            override_missed,
        )

    aligned_clips = beats.fill_gaps(aligned_clips, stock, extras)

    # One record per moment, then the fill pool. Replaces the flat list the
    # downloads above appended to, whose positions stopped matching moments
    # at the first photograph or failed search.
    clip_sources = beats.record_by_beat(
        len(segments), aligned_clips,
        # Every moment meant to show a photo — including one whose photo would
        # not download or decode this time and fell back to stock. Recording
        # that as footage would make one storage hiccup permanent: the next
        # refine would read the photo as replaced and never try it again.
        _photo_pins(ctx, segments),
        own_records,
        # Kept shots too: a failed moment may have borrowed one of those, and
        # their records are not among this render's downloads.
        clip_sources + list(own_records.values()),
    ) + [r for r in clip_sources if r.get("role") == "extra"]

    if kept_shots:
        log.info(
            "Kept %d shot(s) unchanged; %d re-searched.",
            kept_shots, len(segments) - kept_shots,
        )

    if reached_for_topic:
        log.info(
            "Segments %s needed the topic attached to find footage — the "
            "scriptwriter's keyword found nothing on its own.", reached_for_topic,
        )

    progress("collecting", 75, {
        "clips": len(aligned_clips), "extras": len(extras), "user_images": len(user_clips),
    })
    return {
        **state,
        "video_clips": aligned_clips + extras,
        "aligned_clips": aligned_clips,
        "extra_clips": extras,
        # Pictures that could not be used. The render carries on with stock for
        # those beats; this is what lets the worker say so afterwards instead of
        # the pharmacist wondering where their photograph went.
        "user_image_warnings": user_skipped,
        "clip_sources": clip_sources,
    }


def _collect_ai_imagery(ctx: UserContext, state: dict, progress: ProgressCallback) -> dict:
    from . import ai_imagery

    s = get_settings()
    progress("collecting", 65, None)
    meta = state.get("metadata") or {}
    segments = list(meta.get("segments") or [])

    target_w, target_h = SHORTS_RES if ctx.video_mode == "shorts" else LONGFORM_RES
    aspect_ratio = "9:16" if ctx.video_mode == "shorts" else "16:9"

    out_dir = _workdir(ctx) / "clips"
    out_dir.mkdir(exist_ok=True)
    img_dir = _workdir(ctx) / "ai_images"
    img_dir.mkdir(exist_ok=True)

    style = (ctx.image_style or "cartoon").lower()
    max_imgs = max(1, int(s.ai_imagery_max_images_per_job))
    budget_usd = float(s.ai_imagery_budget_per_job_usd)
    per_image_cost = float(s.ai_imagery_cost_per_image_usd)

    aligned_clips: list[str] = []

    still_paths: list[str] = []
    # An AI-imagery job is not all AI imagery. It falls back to stock footage
    # three ways — the per-job image budget runs out, generation fails, or the
    # Ken Burns pass fails — and those clips have to be recorded like any other
    # or a refine can reproduce the generated half of a video and not the rest.
    clip_sources: list[dict] = []
    cost_so_far = 0.0
    images_made = 0
    # The same subject every search and every generated image is about.
    # Generic pharmacy queries used to stand in for a missing keyword here too,
    # with the added cost that on this path an off-topic keyword is *paid for*:
    # it becomes a Replicate image of the wrong thing.
    topic = _topic_phrase(state.get("topic") or ctx.topic)
    already_downloaded: set[str] = set()

    seg_limit = min(len(segments), max_imgs)

    user_clips, user_skipped = _pinned_user_clips(ctx, segments, out_dir, (target_w, target_h))

    for i, seg in enumerate(segments):
        # Pinned by the pharmacist. Their own photograph, and no Replicate
        # call for this beat — the spec is explicit that attaching pictures
        # costs nothing, and generating an image we then discard would be
        # charging for it anyway.
        if i in user_clips:
            aligned_clips.append(user_clips[i])
            continue

        target_dur = max(
            1.5,
            min(8.0, float(seg.get("end", 0)) - float(seg.get("start", 0))) or 3.5,
        )
        keyword = (seg.get("visual_keyword") or "").strip()
        if not keyword:
            # This video's subject, not a generic one. An empty keyword here
            # would otherwise be turned into a paid image of an unrelated
            # condition.
            keyword = topic or PHARMACY_FALLBACK_QUERIES[i % len(PHARMACY_FALLBACK_QUERIES)]

        over_count = images_made >= seg_limit
        over_budget = (cost_so_far + per_image_cost) > budget_usd
        if over_count or over_budget:
            path = _download_one_clip(
                out_dir, keyword, "portrait" if aspect_ratio == "9:16" else "landscape",
                aspect_ratio == "9:16", already_downloaded, clip_sources,
            )
            aligned_clips.append(path or "")
            continue

        try:
            img_path, cost = ai_imagery.generate_one_image(
                img_dir, keyword, style,
                niche=ctx.niche, aspect_ratio=aspect_ratio,
                file_stem=f"seg{i:02d}",
            )
        except ai_imagery.BillingError as billing_exc:
            raise RuntimeError(
                "Replicate has no billing configured — add a payment method at "
                "https://replicate.com/account/billing"
            ) from billing_exc

        if img_path is None:
            path = _download_one_clip(
                out_dir, keyword, "portrait" if aspect_ratio == "9:16" else "landscape",
                aspect_ratio == "9:16", already_downloaded, clip_sources,
            )
            aligned_clips.append(path or "")
            continue

        cost_so_far += cost
        images_made += 1

        # Kept so a refine reuses these exact pictures.
        #
        # Not a cost decision — the whole imagery budget for a job is five cents.
        # Flux is stochastic, so regenerating would return *different* images,
        # and somebody who asked to fix a caption would get a video that no
        # longer looks like the one they approved. The stills are webp and a few
        # hundred kilobytes each, capped near sixteen by the budget.
        still_paths.append(str(img_path))

        clip_path = ai_imagery.image_to_video_clip(
            img_path, out_dir, duration_s=target_dur, target_size=(target_w, target_h),
        )
        if clip_path is None:
            path = _download_one_clip(
                out_dir, keyword, "portrait" if aspect_ratio == "9:16" else "landscape",
                aspect_ratio == "9:16", already_downloaded, clip_sources,
            )
            aligned_clips.append(path or "")
            continue

        aligned_clips.append(str(clip_path))

    if not any(aligned_clips):
        raise RuntimeError("AI imagery produced zero usable clips")

    pool = [p for p in aligned_clips if p]
    # Same rule as the stock path: least-used and not next door, so a failed
    # still is not replaced by the picture right beside it.
    aligned_clips = beats.fill_gaps(aligned_clips, pool, [])

    progress(
        "collecting", 75,
        {"clips": len(aligned_clips), "images_generated": images_made, "cost_usd": round(cost_so_far, 4)},
    )
    if ctx.usage is not None:
        from . import usage as _usage
        # The only provider whose rate was already configured, so this line
        # carries both halves — units and money — where the others carry units
        # alone until somebody fills a rate in.
        ctx.usage.record(_usage.REPLICATE, _usage.IMAGES, images_made,
                         per_image_cost)

    return {
        **state,
        "video_clips": aligned_clips,
        "aligned_clips": aligned_clips,
        "user_image_warnings": user_skipped,
        "extra_clips": [],
        "ai_image_cost_usd": round(cost_so_far, 4),
        "ai_images_generated": images_made,
        "ai_still_paths": still_paths,
        "clip_sources": clip_sources,
    }


# ═══════════════════════════════════════════════════════════════════════
# Editor (MoviePy)
# ═══════════════════════════════════════════════════════════════════════
SHORTS_RES = (int(os.getenv("SHORTS_WIDTH", "1080")), int(os.getenv("SHORTS_HEIGHT", "1920")))
LONGFORM_RES = (int(os.getenv("LONGFORM_WIDTH", "1920")), int(os.getenv("LONGFORM_HEIGHT", "1080")))
FPS = int(os.getenv("VIDEO_FPS", "30"))


def _fit(clip, tw: int, th: int):
    cw, ch = clip.w, clip.h
    if cw / ch > tw / th:
        clip = resize(clip, height=th)
        return crop(clip, x_center=clip.w // 2, y_center=clip.h // 2, width=tw, height=th)
    clip = resize(clip, width=tw)
    return crop(clip, x_center=clip.w // 2, y_center=clip.h // 2, width=tw, height=th)


def _pick_font() -> str:
    env_font = os.getenv("OVERLAY_FONT")
    if env_font:
        return env_font
    for p in [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
    ]:
        if Path(p).exists():
            return p
    return "DejaVu-Sans-Bold"


def _safe_subclip(parent, target_dur: float, use: int = 0):
    """`target_dur` seconds of a clip; a different part of it each time it is reused.

    `use` is how many times this clip has already appeared in the video. The
    first appearance is the middle, as before; later ones start elsewhere
    (`beats.subclip_start`), so a clip that has to appear twice is not an
    identical copy of itself.
    """
    target_dur = max(0.5, float(target_dur))
    if parent.duration <= target_dur + 0.1:
        return parent.subclip(0, parent.duration)
    start = beats.subclip_start(parent.duration, target_dur, use)
    return parent.subclip(start, start + target_dur)


_RATE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*([kKmM]?)\s*$")


def _rate_kbps(value: str, default: int) -> int:
    """ "3500k" / "3.5M" / "3500000" → kbit/s; anything unreadable → default."""
    m = _RATE.match(value or "")
    if not m:
        return default
    number, unit = float(m.group(1)), m.group(2).lower()
    kbps = number * 1000 if unit == "m" else number if unit == "k" else number / 1000
    return int(kbps) if kbps >= 100 else default


def encode_params(env: Callable[[str, str], str] = lambda k, d: os.getenv(k, d)) -> list[str]:
    """x264 settings for the finished video: quality-driven, with a ceiling.

    <b>Why a ceiling.</b> The encode used to pass both a bitrate and a CRF.
    x264 honours the CRF and silently drops the bitrate, so `VIDEO_BITRATE`
    did nothing and the size of a video was whatever its footage made it: a
    19-second re-cut onto sunlit grass came out at 38 MB (16 Mbps) where the
    same video had been 8.5 MB. That file then streams to every screen in a
    pharmacy on shop wifi, and is buffered whole by the API on each request.

    Capped CRF keeps what CRF is good at — easy footage stays small, and
    quality is steady across a video — while `-maxrate`/`-bufsize` (VBV)
    stop hard footage exceeding the cap. `VIDEO_BITRATE` is that cap.
    """
    cap = _rate_kbps(env("VIDEO_BITRATE", "4000k"), 4000)
    return [
        "-crf", env("FFMPEG_CRF", "26"),
        "-maxrate", f"{cap}k",
        # Two seconds of buffer: room for a scene cut without starving the
        # next second, and tight enough that the average stays near the cap.
        "-bufsize", f"{cap * 2}k",
        "-pix_fmt", "yuv420p",
        "-movflags", "+faststart",
    ]


def _edit_opt(ctx: UserContext, name: str, default: Any) -> Any:
    """One editor override, or the default when this is not a refine.

    Every editor setting a refine can touch is read through here, so "what can a
    refine change?" has a single answer that can be grepped for — rather than a
    scattering of `if ctx.editor_options and ...` that each have to remember the
    None case.
    """
    options = getattr(ctx, "editor_options", None)
    if options is None:
        return default
    value = getattr(options, name, None)
    return default if value is None else value


def _faded(ctx: UserContext, clip):
    """Fade a shot in and out, honouring a refine's fade setting.

    Capped at a third of the shot. A one-second fade on a 0.8-second cut is a
    clip that is entirely fade — it reads as a blur rather than an edit, and the
    request that produced it was "slower fades", not "remove this shot".
    """
    seconds = float(_edit_opt(ctx, "fade_seconds", 0.15))
    duration = getattr(clip, "duration", None) or 0.0
    seconds = max(0.0, min(seconds, duration / 3.0))
    if seconds <= 0:
        return clip
    return fadein(clip, seconds).fx(fadeout, seconds)


#: Where captions sit by default, as a fraction of frame height. High on a
#: vertical clip (a phone's thumb and the platform's own UI occupy the bottom),
#: low on a horizontal one (the conventional subtitle position).
CAPTION_Y_VERTICAL = 0.18
CAPTION_Y_HORIZONTAL = 0.78

#: Where a refine may move them to.
CAPTION_Y_BY_NAME = {"top": 0.10, "middle": 0.45, "bottom": 0.78}


def caption_flashes(segments: Any) -> list[tuple[str, float, float]]:
    """The on-screen captions: ``(text, start, duration)``, one per beat at most.

    Each caption is the first sentence of its beat, upper-cased, up to 60
    characters, shown for at most 2.5s.

    **A caption already shown is not shown again.** Scripts repeat themselves
    on purpose — the call to action is often said at the start and again at the
    close, and a split sentence can open two beats with the same words. Spoken,
    that is emphasis; flashed on screen twice it reads as a glitch, and
    pharmacists reported it as a duplicate caption. The comparison ignores case,
    spacing and punctuation, because "Book today!" and "book today" are the same
    caption to anyone watching.

    Never raises: a beat with unreadable timings is skipped, not the video.
    """
    flashes: list[tuple[str, float, float]] = []
    seen: set[str] = set()
    for seg in segments or []:
        if not isinstance(seg, dict):
            continue
        text = str(seg.get("text") or "").split(".")[0].strip()[:60]
        key = " ".join(re.sub(r"[^\w\s]", " ", text.lower()).split())
        if not key or key in seen:
            continue
        try:
            start = float(seg.get("start", 0))
            duration = min(2.5, float(seg.get("end", start + 2.5)) - start)
        except (TypeError, ValueError):
            continue
        if duration <= 0:
            continue
        seen.add(key)
        flashes.append((text.upper(), start, duration))
    return flashes


def repeated_captions(segments: Any) -> list[str]:
    """Captions the script would have shown more than once, before de-duplication.

    Used to check a "remove the duplicate caption" request against the video
    rather than promising a fix for a repeat that is not there.
    """
    first: dict[str, str] = {}
    repeats: list[str] = []
    for seg in segments or []:
        if not isinstance(seg, dict):
            continue
        text = str(seg.get("text") or "").split(".")[0].strip()[:60]
        key = " ".join(re.sub(r"[^\w\s]", " ", text.lower()).split())
        if not key:
            continue
        if key in first:
            if first[key] not in repeats:
                repeats.append(first[key])
        else:
            first[key] = text.upper()
    return repeats


def _caption_band(ctx: UserContext, tw: int, th: int) -> int:
    """The y position captions are drawn at, honouring a refine."""
    name = _edit_opt(ctx, "caption_position", None)
    if isinstance(name, str) and name in CAPTION_Y_BY_NAME:
        return int(th * CAPTION_Y_BY_NAME[name])
    return int(th * (CAPTION_Y_VERTICAL if th > tw else CAPTION_Y_HORIZONTAL))


def _hex_to_rgb(value: str | None, fallback: tuple[int, int, int]) -> tuple[int, int, int]:
    """`#1f2937` or `#abc` → an RGB triple. Never raises."""
    if not isinstance(value, str):
        return fallback
    s = value.strip().lstrip("#")
    if len(s) == 3:
        s = "".join(c * 2 for c in s)
    if len(s) != 6:
        return fallback
    try:
        return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16))
    except ValueError:
        return fallback


def _readable_on(rgb: tuple[int, int, int]) -> str:
    """Black or white, whichever a viewer can actually read on `rgb`.

    A pharmacy picking a pale brand colour would otherwise get white text on
    near-white and an end card that reads as a blank bar. Rec. 601 luma is
    plenty for a two-way choice.
    """
    r, g, b = rgb
    return "black" if (0.299 * r + 0.587 * g + 0.114 * b) > 150 else "white"


#: How long the end card sits on screen, and the shortest video worth one.
END_CARD_SECONDS = 3.0
END_CARD_MIN_VIDEO_SECONDS = 6.0

#: Roughly what fits on two lines of the band without spilling out of it.
END_CARD_NAME_CHARS = 44
END_CARD_DETAIL_CHARS = 48


def _display_name(name: str) -> str:
    """A pharmacy name that fits the band.

    The profile allows 200 characters. At the end-card font size, wrapped to 86%
    of the frame, that is five or six lines out of a band two lines tall — text
    running off both edges of the video with no way for anyone to notice before
    a patient does.
    """
    name = name.strip()
    if len(name) <= END_CARD_NAME_CHARS:
        return name
    return name[: END_CARD_NAME_CHARS - 1].rstrip() + "…"


def _display_link(url: str) -> str:
    """A booking address a patient could actually read off a screen.

    Two things happen here. The scheme and a leading ``www.`` come off always —
    nobody types ``https://`` and it costs eight characters of a line that has
    about forty-eight.

    Then, if it is *still* too long, it falls back to the **host alone** rather
    than a truncated URL. A cut-off address with an ellipsis on the end is worse
    than useless: it cannot be typed, and it looks like the software is broken.
    The host can be typed, and it gets them to the right pharmacy's site —
    accepting that they may have to find the booking page from there. The
    profile permits 400 characters, so this is not a hypothetical.
    """
    shown = url.strip()
    for scheme in ("https://", "http://"):
        if shown.lower().startswith(scheme):
            shown = shown[len(scheme):]
            break
    if shown.lower().startswith("www."):
        shown = shown[4:]
    shown = shown.rstrip("/")

    if len(shown) <= END_CARD_DETAIL_CHARS:
        return shown

    host = shown.split("/", 1)[0] or shown

    # And the host itself still has to fit. A real one cannot exceed 253
    # characters, but this field accepts 400 of anything that parsed as a URL,
    # so the fallback needs a fallback. Nothing here is typeable any more; the
    # job is only to not run text off both edges of the frame.
    if len(host) > END_CARD_DETAIL_CHARS:
        return host[: END_CARD_DETAIL_CHARS - 1].rstrip() + "…"
    return host


def _end_card_top(th: int, band_height: int, caption_span: tuple[int, int]) -> int:
    """Where the end-card band can sit without landing on the captions.

    Two candidate positions, low and high, and the first one clear of the
    caption block wins. This replaced a rule that simply put the band opposite
    the caption's *top* edge, which worked for captions pinned high or low and
    failed for every caption in the middle of the frame — a position a refine
    can now ask for, so the case is reachable rather than theoretical.

    If neither is clear the band goes wherever it overlaps least. Something has
    to be drawn, and a card that clips a caption for three seconds is a worse
    video; a card silently missing is a worse *bug*, because the pharmacist
    asked for it and nothing says why it is not there.
    """
    low, high = int(th * 0.66), int(th * 0.08)
    c0, c1 = caption_span

    def overlap(top: int) -> int:
        return max(0, min(top + band_height, c1) - max(top, c0))

    for candidate in (low, high):
        if overlap(candidate) == 0:
            return candidate

    best = min((low, high), key=overlap)
    log.info(
        "End card and captions cannot both be placed clear at this size; "
        "using the position with the least overlap (%dpx).", overlap(best),
    )
    return best


def _brand_end_card(
    ctx: UserContext, tw: int, th: int, total: float, font: str,
    caption_span: tuple[int, int],
) -> list:
    """The pharmacy's name — and how to reach them — over the closing seconds.

    An **overlay on the existing footage**, not an extra clip appended to it.
    Appending would make a 15-second short 18 seconds long, push it past the
    duration the pharmacy chose, and leave three seconds of silence after the
    narration ends. Overlaying costs no time and no audio.

    **Text, not a QR code.** A QR is scanned by a second device, and the person
    watching a vertical short is holding the phone they would have to scan with.
    The code goes on posters and shop windows, where somebody is standing in
    front of glass with a free hand. This is the same decision recorded in
    `docs/DECISION-video-brand-and-refine.md`, applied here.

    Returns an empty list — rendering exactly as before — when the job carries
    no brand, when the video is too short to give three seconds away, or when
    anything at all goes wrong building the clips. A branded end card is worth
    having and not worth failing a finished render over.

    **On the `except` at the bottom.** It has now hidden two of my own
    programming errors: a `set_opacity` call that could never work on a maskless
    ColorClip, and an argument passed as an int where a tuple was expected. Both
    times the end card silently never rendered and the log said "skipped", which
    reads like a decision rather than a fault. So the arguments are checked
    *before* the try, and a contract violation is logged at error level with the
    word "bug" in it. Still not fatal — the render is finished and good — but no
    longer indistinguishable from a pharmacy that simply has no name.
    """
    if not (isinstance(caption_span, (tuple, list)) and len(caption_span) == 2):
        log.error(
            "Brand end card BUG: caption_span was %r, expected a (top, bottom) "
            "pair. Rendering without an end card.", caption_span,
        )
        return []

    brand = ctx.brand

    # A refine may switch the card off outright — "drop the closing card" is a
    # legitimate edit, and one the editor can satisfy on its own.
    wanted = _edit_opt(ctx, "end_card", None)
    if wanted is False:
        log.info("Brand end card suppressed by this refine.")
        return []

    if brand is None or not brand.name:
        # Asking for a card we have no name to put on it. Worth a distinct line:
        # "on" and "nothing happened" together is a support question, and the
        # answer is that the pharmacy profile has no name in it.
        if wanted is True:
            log.warning(
                "Video %s: end card was asked for, but this pharmacy has no "
                "name in its profile or brand kit. Rendering without one.",
                ctx.job_id,
            )
        return []

    if total < END_CARD_MIN_VIDEO_SECONDS:
        log.info(
            "Brand end card skipped: the video is %.1fs and the card needs %.1fs "
            "of a clip at least %.1fs long.",
            total, END_CARD_SECONDS, END_CARD_MIN_VIDEO_SECONDS,
        )
        return []

    # The one line under the name. Preferred in the order a patient can act on
    # it: a link they can type, then a number they can ring, then an address
    # they can walk to. One line only — three lines of small print over moving
    # footage is read by nobody.
    # A refine may name which line it wants. "none" leaves the pharmacy's name
    # standing alone, which is a real request — a card with just the name reads
    # as a sign-off rather than an advert.
    choice = _edit_opt(ctx, "end_card_detail", None)
    sources = {
        "booking": brand.booking_url,
        "phone": brand.phone,
        "address": brand.address,
        "none": "",
    }

    if isinstance(choice, str) and choice in sources:
        chosen = sources[choice] or ""
        if choice != "none" and not chosen:
            # Asked for a line the pharmacy has not filled in. Falling back to a
            # different one would put a phone number where somebody asked for an
            # address — quietly answering a question they did not ask.
            log.info(
                "End card detail %r requested but the pharmacy has none. "
                "Showing the name alone.", choice,
            )
        raw = chosen
    else:
        raw = brand.booking_url or brand.phone or brand.address

    if raw and raw == brand.booking_url:
        detail = _display_link(raw)
    else:
        detail = (raw or "").strip()
        if len(detail) > END_CARD_DETAIL_CHARS:
            detail = detail[: END_CARD_DETAIL_CHARS - 1].rstrip() + "…"

    if brand.logo_blob_key:
        # Deliberately not drawn yet, and said out loud rather than left as a
        # silent omission. The brand-kit logo may be an SVG — which is exactly
        # what broke poster rendering, because the imaging library throws on one
        # rather than returning nothing. Drawing it here needs the rasterised
        # copy the .NET side already produces for posters, not the raw blob.
        log.info(
            "Video %s has a logo (%s) but the end card is text-only for now — "
            "the raster conversion lives on the .NET side.",
            ctx.job_id, brand.logo_blob_key,
        )

    try:
        seconds = min(END_CARD_SECONDS, total)
        start = max(0.0, total - seconds)

        band_rgb = _hex_to_rgb(brand.primary_color, (17, 24, 39))
        ink = _readable_on(band_rgb)

        band_height = int(th * 0.22)

        # Placed clear of wherever the captions actually ended up.
        band_top = _end_card_top(th, band_height, caption_span)

        # An explicit mask rather than .set_opacity().
        #
        # moviepy 1.0.3's set_opacity multiplies the clip's existing mask, and a
        # ColorClip built from an RGB triple has no mask at all — the array is
        # three channels, so ImageClip never makes one. It would raise
        # AttributeError on None, the except below would swallow it, and the end
        # card would silently never appear while logging what looks like a
        # deliberate skip. crossfadein then fades this mask in from zero.
        band_mask = (
            ColorClip(size=(tw, band_height), color=0.88, ismask=True)
            .set_duration(seconds)
        )

        band = (
            ColorClip(size=(tw, band_height), color=band_rgb)
            .set_start(start)
            .set_duration(seconds)
            .set_mask(band_mask)
            .set_position((0, band_top))
            .crossfadein(0.4)
        )

        name = (
            TextClip(
                _display_name(brand.name),
                fontsize=int(th * 0.038),
                color=ink,
                font=font,
                method="caption",
                size=(int(tw * 0.86), None),
            )
            .set_start(start)
            .set_duration(seconds)
            .set_position(("center", band_top + int(band_height * 0.18)))
            .crossfadein(0.4)
        )

        clips = [band, name]

        if detail:
            clips.append(
                TextClip(
                    detail,
                    fontsize=int(th * 0.024),
                    color=ink,
                    font=font,
                    method="caption",
                    size=(int(tw * 0.86), None),
                )
                .set_start(start)
                .set_duration(seconds)
                .set_position(("center", band_top + int(band_height * 0.58)))
                .crossfadein(0.4)
            )

        log.info(
            "Brand end card: %r%s for the last %.1fs.",
            brand.name, f" / {detail}" if detail else "", seconds,
        )
        return clips
    except Exception as exc:  # noqa: BLE001
        # Same posture as the caption overlays above: the render is finished and
        # good, and a missing end card is a smaller loss than no video.
        log.warning("Brand end card skipped: %s", exc)
        return []


def make_editor(ctx: UserContext, progress: ProgressCallback = _noop_progress):
    def node(state: dict) -> dict:
        progress("editing", 75, None)
        tw, th = SHORTS_RES if ctx.video_mode == "shorts" else LONGFORM_RES
        voice = AudioFileClip(state["audio_path"])
        # Kept after the clip is closed, because the overrun check below needs
        # the speech length on its own — `total` has the tail padding in it, and
        # dividing words by a padded duration would understate the speaking rate.
        voice_seconds = float(voice.duration)
        total = voice_seconds + 0.5
        voice.close()

        # The video is exactly as long as the narration, so an over-long script
        # is a cost multiplier on every stage after this one and nothing here
        # trims it. Said out loud because it went unnoticed for weeks: a clip the
        # product calls 15 seconds was rendering at 27, and the only visible
        # symptom was "videos take a while".
        if total > ctx.duration_seconds * 1.4:
            # <b>Two causes, opposite fixes, and they are indistinguishable
            # from the duration alone.</b>
            #
            # Either the writer exceeded its word budget, or the budget itself
            # is wrong because NARRATION_WPM does not match how fast the voice
            # actually speaks. This warning used to assert the first one. It had
            # no word count to say that with — so a wrong constant would have
            # sent whoever read it to rewrite a prompt that was behaving.
            #
            # Both numbers are printed instead. Words inside budget with a low
            # observed rate means the constant is wrong; words over budget means
            # the writer is.
            words = int(state.get("spoken_words") or 0)
            speech = max(float(voice_seconds), 0.001)
            observed_wpm = words / speech * 60 if words else 0.0
            low, high = narration_word_budget(ctx.duration_seconds)

            if ctx.script_source == "custom":
                # A pharmacist who pasted 300 words into a 15-second clip is not
                # a bug in anything. Their call, and worth naming as theirs.
                remedy = (
                    "The creator supplied this script. It is longer than the clip "
                    "length they chose, which is their decision, but it is what "
                    "makes this render slow."
                )
            elif not words:
                remedy = (
                    "The cause is not established: the word count is recorded by "
                    "the voice stage, which an editor refine skips — this clip is "
                    "as long as the narration it reused."
                )
            elif words > high:
                remedy = (
                    f"The writer overran its budget ({words} words against a "
                    f"{low}-{high} ceiling). Check the scriptwriter prompt."
                )
            else:
                remedy = (
                    f"The writer stayed inside its budget ({words} words, ceiling "
                    f"{high}) and the clip is still long — so NARRATION_WPM={NARRATION_WPM} "
                    f"is too high for this voice. Observed {observed_wpm:.0f} wpm."
                )

            # "0 words at 0 wpm" would read as a measurement of zero rather than
            # the absence of one, which is a different and misleading claim.
            measured = (
                f"{words} words at {observed_wpm:.0f} wpm observed against an "
                f"assumed {NARRATION_WPM}"
                if words
                else "no word count recorded for this run"
            )
            log.warning(
                "Script overran: %.1fs of narration for a %ds clip (%.0f%% over), "
                "%s. Everything downstream scales with this — b-roll, compositing, "
                "encoding, upload. %s",
                total, ctx.duration_seconds, (total / ctx.duration_seconds - 1) * 100,
                measured, remedy,
            )

        meta = state.get("metadata") or {}
        max_clips = int(os.getenv("MAX_CLIPS_PER_VIDEO", "12"))
        # Where each moment really falls: the whole narration shared out across
        # the moments (see `beats.fit_timeline`). Shots and captions both follow
        # it, so every shot on screen is a moment a refine can see and name —
        # there is no leftover time to fill with clips that belong to none.
        script_segments = beats.timed_segments(
            list(meta.get("segments") or [])[:max_clips], total)

        aligned = list(state.get("aligned_clips") or [])
        extras = list(state.get("extra_clips") or [])
        legacy_only = not aligned and not extras

        parent_clips = []
        segments: list = []
        elapsed = 0.0

        def _open(path: str):
            try:
                c = VideoFileClip(path, audio=False)
                parent_clips.append(c)
                return c
            except Exception:
                return None

        # How often each clip has appeared so far, so a reuse is cut from a
        # different part of it and the top-up prefers what has been seen least.
        uses: dict[str, int] = {}
        # What was actually put on screen, shot by shot, for the refine to
        # describe: which moment, and when. Written back to the document.
        shot_timeline: list[dict] = []

        if not legacy_only and aligned:
            for i, seg in enumerate(script_segments):
                # The fitted length, not the scriptwriter's estimate — and no
                # longer capped at 8s, because the cap is what left time over.
                target = float(seg["end"]) - float(seg["start"])
                if target < 0.3:
                    continue

                path = aligned[i] if i < len(aligned) else (extras[i % len(extras)] if extras else None)
                if not path:
                    continue
                c = _open(path)
                if c is None:
                    if extras:
                        path = extras[i % len(extras)]
                        c = _open(path)
                    if c is None:
                        continue

                sub = _safe_subclip(c, target, uses.get(path, 0))
                uses[path] = uses.get(path, 0) + 1
                if sub.duration < target - 0.05:
                    # A clip shorter than its moment plays again rather than
                    # being followed by a different clip — one moment, one shot.
                    sub = loop(sub, duration=target)
                sub = _fit(sub, tw, th)
                sub = _faded(ctx, sub)
                shot_timeline.append({
                    "segment": i,
                    "start": round(elapsed, 3),
                    "end": round(elapsed + sub.duration, 3),
                })
                segments.append(sub)
                elapsed += sub.duration

            # Top up until the B-roll covers the narration. The pool is cycled,
            # so short source clips just get reused rather than leaving a gap.
            #
            # Both loop guards matter: `failures` bounds the case where every
            # download is corrupt (otherwise this spins forever), and `spins`
            # bounds the case where clips open but yield near-zero duration
            # (which would advance `elapsed` too slowly to ever terminate).
            extra_idx = 0
            failures = 0
            spins = 0
            # Everything this video has, spares first. Least-used selection
            # below means a spare is used before a shot already on screen.
            pool = [p for p in dict.fromkeys(list(extras) + list(aligned)) if p]
            max_failures = max(6, len(pool) * 2)
            max_spins = max_clips * 6

            while elapsed < total - 0.05 and pool and spins < max_spins:
                spins += 1
                # The least-shown clip, not the next in a fixed rotation: the
                # rotation began with clips already on screen, which is how
                # topping up the length became a repeated shot. Ties go round
                # in pool order so a clip that will not open is not retried
                # back to back.
                path = beats.pick_topup(pool, uses, extra_idx)
                extra_idx += 1

                c = _open(path)
                if c is None:
                    # Out of the running: least-used would otherwise pick the
                    # same unopenable clip on every pass.
                    uses[path] = uses.get(path, 0) + beats.UNOPENABLE
                    failures += 1
                    if failures >= max_failures:
                        log.warning(
                            "Gave up topping up B-roll after %d failed clip opens; "
                            "%.1fs of %.1fs covered", failures, elapsed, total)
                        break
                    continue

                target = min(3.5, total - elapsed)
                if target < 0.5:
                    break

                sub = _safe_subclip(c, target, uses.get(path, 0))
                uses[path] = uses.get(path, 0) + 1
                sub = _fit(sub, tw, th)
                sub = _faded(ctx, sub)
                # Only reached when a moment's clip would not open. Recorded, so
                # the refine can say a filler shot is there rather than pretend
                # the moments are all there is.
                shot_timeline.append({
                    "segment": None,
                    "start": round(elapsed, 3),
                    "end": round(elapsed + sub.duration, 3),
                    "filler": True,
                })
                segments.append(sub)
                elapsed += sub.duration
        else:
            clips_in = state.get("video_clips", [])[:]
            random.shuffle(clips_in)
            clips_in = clips_in[:max_clips]
            idx = 0
            while elapsed < total and idx < max_clips * 3 and clips_in:
                path = clips_in[idx % len(clips_in)]
                idx += 1
                c = _open(path)
                if c is None:
                    continue
                seg_len = min(c.duration, random.uniform(2.2, 4.5), total - elapsed)
                if seg_len <= 0.3:
                    break
                start = random.uniform(0, max(0, c.duration - seg_len - 0.1))
                sub = c.subclip(start, start + seg_len)
                sub = _fit(sub, tw, th)
                sub = _faded(ctx, sub)
                segments.append(sub)
                elapsed += seg_len

        if not segments:
            raise RuntimeError("Editor produced no usable B-roll segments")

        broll = concatenate_videoclips(segments, method="chain")

        # The timeline length comes from the voiceover, but nothing above
        # guarantees the B-roll actually reaches it: Pexels clips can be
        # shorter than the segment they were picked for, downloads can fail to
        # open, and the fill loop gives up after enough consecutive failures.
        # Whatever the cause, a short visual track means the composite falls
        # through to the black background while the narration keeps playing —
        # which reads as "the video broke" even though the audio is fine.
        #
        # So: never let the visual track be shorter than the audio. Looping the
        # existing B-roll is repetitive, but repetition is a far better failure
        # mode than a black screen.
        if broll.duration > total:
            broll = broll.subclip(0, total)
        elif broll.duration < total - 0.05:
            shortfall = total - broll.duration
            log.warning(
                "B-roll is %.1fs short of the %.1fs narration (%d segments) — "
                "looping to cover the gap. Check for Pexels download failures.",
                shortfall, total, len(segments),
            )
            broll = loop(broll, duration=total)

        log.info(
            "Editor: %d segments, b-roll %.1fs, target %.1fs",
            len(segments), broll.duration, total,
        )

        overlays = []
        font = _pick_font()

        # A refine may resize and reposition the captions. Both are read once,
        # outside the loop, so every caption in a video agrees — computing them
        # per segment would let one bad value affect a single caption and leave
        # the rest, which looks like a rendering glitch rather than a setting.
        caption_scale = float(_edit_opt(ctx, "caption_scale", 1.0))
        caption_size = max(1, int(th * 0.045 * caption_scale))
        caption_band = _caption_band(ctx, tw, th)

        # How much of the frame the captions can occupy, so the end card can
        # keep clear of it. Three wrapped lines at roughly 1.25 line spacing is
        # the worst case a 60-character caption produces at 85% width — and it
        # is the worst case that has to be avoided, not the typical one.
        caption_span = (caption_band, caption_band + int(caption_size * 3.75))

        for flash, start, duration in caption_flashes(script_segments):
            try:
                tc = (
                    TextClip(
                        flash,
                        fontsize=caption_size,
                        color="white",
                        font=font,
                        method="caption",
                        size=(int(tw * 0.85), None),
                        stroke_color="black",
                        stroke_width=2,
                    )

                    .set_start(start)
                    .set_duration(duration)
                    .crossfadein(0.25)
                    .crossfadeout(0.25)
                )
                tc = tc.set_position(("center", caption_band))
                overlays.append(tc)
            except Exception as exc:  # noqa: BLE001
                log.warning("Overlay skipped: %s", exc)

        overlays.extend(_brand_end_card(ctx, tw, th, total, font, caption_span))

        if ctx.watermark:
            try:
                wm = (
                    TextClip("Marketing25", fontsize=int(th * 0.025), color="white", font=font)
                    .set_duration(total)
                    .set_opacity(0.55)
                    .set_position(("right", "bottom"))
                    .margin(right=20, bottom=20, opacity=0)
                )
                overlays.append(wm)
            except Exception:
                pass

        bg = ColorClip(size=(tw, th), color=(0, 0, 0)).set_duration(total)
        composed = CompositeVideoClip([bg, broll.set_position("center")] + overlays, size=(tw, th)).set_duration(total)

        voice_clip = AudioFileClip(state["audio_path"])
        composed = composed.set_audio(CompositeAudioClip([voice_clip]))

        out_dir = _workdir(ctx)
        out = out_dir / f"final_{int(time.time())}.mp4"

        progress("editing", 82, None)
        log.info("Editor: starting ffmpeg encode → %s", out.name)

        composed.write_videofile(
            str(out),
            fps=FPS,
            codec="libx264",
            audio_codec="aac",
            # No `bitrate`: with a CRF alongside it x264 ignores it anyway, and
            # the cap in `encode_params` is what actually bounds the file.
            preset=os.getenv("FFMPEG_PRESET", "ultrafast"),
            threads=int(os.getenv("FFMPEG_THREADS", "4")),
            ffmpeg_params=encode_params(),
            # MoviePy 1.0.3 names its intermediate audio file from the output's
            # *basename* and writes it to the current directory — /app, which
            # belongs to root now the service runs as `app` (SEC-14). Every
            # render then failed at Editing with "Permission denied" on
            # final_<n>TEMP_MPY_wvf_snd.mp4. Keep it beside the output, in this
            # job's own scratch directory; MoviePy deletes it when done.
            temp_audiofile=str(out_dir / f"{out.stem}_audio.m4a"),
            logger=None,
        )
        composed.close()
        broll.close()
        voice_clip.close()

        log.info("Editor: render complete (%.1f MB)", out.stat().st_size / 1e6)
        progress("editing", 92, {"file": str(out)})
        return {**state, "final_video_path": str(out), "shot_timeline": shot_timeline}

    return node

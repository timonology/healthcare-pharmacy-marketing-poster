"""
Design a poster in the house style from a natural-language brief.

Flow: the model writes a *plan* — archetype, copy, a stock-search phrase —
never coordinates. `house_style.compose` draws it with the same builders as the
built-in house templates, sizes and wraps the type for both renderers, and runs
the house checks. Problems go back to the model as plain English ("the
headline does not fit: at most 2 lines of about 18 characters") and it rewrites
the copy; after MAX_ATTEMPTS it gives up rather than return something broken.

**Why the model no longer draws.** Asked for raw canvas JSON, it produced the
same full-bleed photo under a translucent scrim every time, because that is
the layout a model can freehand. The house set — circle crops on a teal ring,
diagonal cuts, sine-wave footers, line-drawn ticks — is not freehandable. See
the `house_style` docstring.

**Photographs.** Four sources, chosen by the caller, never swapped silently:

- ``none``: a typographic / geometric design.
- ``stock``: a Pexels photo — the one the caller chose by ID, or the best match
  for the model's ``photo_query``. Fetched by ID through the Pexels API (never a
  caller URL), baked here, stored by the API (never hot-linked).
- ``upload``: the caller's own image bytes, designed around.
- ``ai``: generated, only when asked for and only when an image model is
  configured. If it is not configured, that is an error, not a stock photo.

Every photo is cover-cropped to its slot and baked (circle PNG, fades in the
JPEG) by `house_style.bake_photo`, then returned as an asset for the API to
store. The canvas refers to it as `house_style.PHOTO_KEY` until then.
"""

from __future__ import annotations

import base64
import json
import logging
import re
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

from langchain_core.messages import AIMessage, HumanMessage, SystemMessage

from app.config import get_settings
from app.llm import anthropic_chat, text_of

from . import poster_imagery
from . import stock_photos as stock
from .house_style import (
    ARCHETYPES,
    PHOTO_KEY,
    BakedPhoto,
    Plan,
    archetype_block,
    bake_photo,
    canonical_archetype,
    compose,
    tone_for_brief,
)
from .template_validator import validate_canvas

log = logging.getLogger(__name__)

MAX_ATTEMPTS = 3

PHOTO_MODES = ("none", "stock", "upload", "ai")

#: Largest upload accepted, decoded. The API caps uploads at 12MB and the
#: browser downscales well below that; this is the backstop for a direct call.
MAX_UPLOAD_BYTES = 12 * 1024 * 1024


class TemplateGenerationError(RuntimeError):
    """Raised when no attempt produced a publishable canvas."""

    def __init__(self, message: str, problems: list[str] | None = None) -> None:
        super().__init__(message)
        self.problems = problems or []


@dataclass
class Photo:
    """Where the photograph comes from, as the caller asked."""

    mode: str = "none"
    stock_photo_id: str | None = None
    data: bytes | None = None
    description: str | None = None

    @classmethod
    def from_request(cls, raw: dict[str, Any] | None) -> "Photo":
        if not raw:
            return cls()
        mode = str(raw.get("mode") or "none").lower()
        if mode not in PHOTO_MODES:
            raise TemplateGenerationError(f"Unknown photo mode {mode!r}.")
        data = None
        if mode == "upload":
            encoded = raw.get("data_base64") or ""
            try:
                data = base64.b64decode(encoded, validate=True)
            except (ValueError, TypeError) as exc:
                raise TemplateGenerationError("The uploaded image could not be read.") from exc
            if not data:
                raise TemplateGenerationError("The uploaded image is empty.")
            if len(data) > MAX_UPLOAD_BYTES:
                raise TemplateGenerationError("The uploaded image is larger than 12MB.")
        description = (raw.get("description") or "").strip() or None
        stock_id = (str(raw.get("stock_photo_id") or "").strip() or None) if mode == "stock" else None
        if stock_id is not None and not stock_id.isdigit():
            raise TemplateGenerationError("That stock photo id is not valid.")
        return cls(mode=mode, stock_photo_id=stock_id, data=data, description=description)


_ASKS_FOR_AI = re.compile(
    r"\b(generate|create|make)\s+(an?\s+)?(ai\s+)?(photo|image|picture)\b"
    r"|\bai[- ](generated\s+)?(photo|image|picture)\b",
    re.I,
)


def asks_for_ai_photo(prompt: str) -> bool:
    """A clear request in the brief itself, e.g. "generate a photo of…"."""
    return bool(_ASKS_FOR_AI.search(prompt or ""))


# ── prompt ──────────────────────────────────────────────────────────────────


def _system_prompt() -> str:
    return f"""\
You are the copywriter and art director for UK community pharmacy posters in one \
house style: medical navy and blue, white and teal, generous space, big clear \
headlines, line-drawn ticks, a contact card with the pharmacy's details and a \
booking QR code at the foot. The layout is drawn for you — you choose the \
archetype and write the words. You output ONLY a JSON object.

# Archetypes — choose the one that fits the brief
{archetype_block()}

If the brief names a layout ("circle portrait", "diagonal split", \
"wave footer", "checklist bar"), use that archetype. "Bold" is a tone, not a \
layout: the layout sets larger type for a bold brief, so still choose one of \
the archetypes above.

# Fields
- archetype: one of the names above.
- name: a short, specific template name ("Walk-in Flu Clinic", not "Health Poster").
- eyebrow: 1-4 words, shown small in capitals above the headline (optional).
- headline: 2-7 words. The layout sizes it (60px or more). You may put a line \
break as \\n where it reads best.
- lede: one sentence under the headline, under 90 characters (optional).
- items: short points, 2-6 words each — services, symptoms, steps, who is eligible.
- note: one reassuring sentence for a note card (checklist only, optional).
- footer: a short strapline for the footer bar.
- photo_query: 3-6 plain words describing a suitable photograph for a stock \
search ("pharmacist talking to older woman"). People and clinics, never \
medicine packaging.

# House rules — break any of these and the design is rejected
- Never write the pharmacy's name, phone number, address, postcode or web \
address, and never invent one. The contact card adds the real ones.
- Never write "your pharmacy" — say "us", "our team" or "the pharmacy".
- No prices, percentages, discounts, offers or "free gift".
- No reviews, ratings, stars or testimonials.
- No medicine brand names. Say "hay fever tablets", not a brand.
- No statistics or medical claims you cannot stand behind. British spelling, \
calm NHS-adjacent tone. Where eligibility matters, say "ask if you're eligible".
- Plain characters only: no ✓, ★, bullets or emoji — the layout draws ticks.

# Output
Return ONLY the JSON object, no prose and no code fence:
{{"archetype":"...","name":"...","eyebrow":"...","headline":"...","lede":"...",\
"items":["..."],"note":"...","footer":"...","photo_query":"..."}}
"""


def _brief(prompt: str, category: str | None, archetype: str | None, photo: Photo) -> str:
    lines = [f"Design brief: {prompt.strip()}"]
    if category:
        lines.append(f"Category: {category}")
    if archetype:
        lines.append(f"Use the '{archetype}' archetype.")
    if photo.mode == "none":
        lines.append("There is NO photograph: the layout draws a geometric motif in its "
                     "place. photo_query may be empty.")
    elif photo.mode == "upload":
        lines.append("The pharmacy supplied its own photograph"
                     + (f", showing: {photo.description}." if photo.description else ".")
                     + " Choose an archetype that suits it — circle-portrait for "
                     "a person, diagonal-split or wave-footer for a wide scene.")
    elif photo.mode == "ai":
        lines.append("A photograph will be generated from photo_query"
                     + (f" (the pharmacy asked for: {photo.description})" if photo.description else "")
                     + ". Write photo_query as a clear scene description.")
    else:
        lines.append("A stock photograph will be found from photo_query"
                     + (f" (already chosen: {photo.description})" if photo.description else "")
                     + ".")
    return "\n".join(lines)


def _llm():
    """The design model. No temperature — see app/llm.py."""
    s = get_settings()
    return anthropic_chat(s.template_design_model, max_tokens=2048, timeout=120)


_FENCE_RE = re.compile(r"```(?:json)?\s*(.*?)```", re.DOTALL)


def _extract_json(raw: str) -> dict[str, Any]:
    """Pull a JSON object out of the reply, tolerating a stray code fence."""
    text = raw.strip()
    fenced = _FENCE_RE.search(text)
    if fenced:
        text = fenced.group(1).strip()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass
    start, end = text.find("{"), text.rfind("}")
    if start != -1 and end > start:
        return json.loads(text[start:end + 1])
    raise json.JSONDecodeError("No JSON object found in the reply.", text, 0)


def _finalise(doc: dict[str, Any], owner_id: str) -> dict[str, Any]:
    """Fill the server-owned envelope fields."""
    now = datetime.now(timezone.utc).isoformat()
    doc["schemaVersion"] = 1
    doc["id"] = uuid.uuid4().hex
    doc["ownerId"] = owner_id
    doc.setdefault("viewport", {"pan": {"x": 0, "y": 0}, "zoom": 1})
    doc["createdAtUtc"] = now
    doc["updatedAtUtc"] = now
    return doc


# ── photographs ─────────────────────────────────────────────────────────────


class _PhotoSource:
    """Fetches the photograph once, bakes it per archetype on demand."""

    def __init__(self, photo: Photo, prompt: str):
        self.photo = photo
        self.prompt = prompt
        self.source: bytes | None = photo.data
        self.origin = {"none": "none", "upload": "upload", "ai": "ai"}.get(photo.mode, "stock")
        self.notes: list[str] = []
        self._baked: dict[str, BakedPhoto] = {}
        self._tried = photo.mode == "upload"

    @property
    def wanted(self) -> bool:
        return self.photo.mode != "none"

    async def get(self, plan: Plan) -> BakedPhoto | None:
        """The photograph baked for this plan's archetype, or None for photo-free."""
        if not self.wanted:
            return None
        if self.source is None and not self._tried:
            self._tried = True
            await self._acquire(plan)
        if self.source is None:
            return None
        if plan.archetype not in self._baked:
            try:
                self._baked[plan.archetype] = bake_photo(self.source, plan.archetype)
            except ValueError as exc:
                raise TemplateGenerationError(str(exc)) from exc
        return self._baked[plan.archetype]

    async def _acquire(self, plan: Plan) -> None:
        slot = ARCHETYPES[plan.archetype].slot
        mode = self.photo.mode

        # A clear "generate a photo" in the brief upgrades the default stock
        # choice — but only when an image model exists, and out loud if not.
        if mode == "stock" and self.photo.stock_photo_id is None and asks_for_ai_photo(self.prompt):
            if poster_imagery.is_configured():
                mode = "ai"
            else:
                self.notes.append("AI images are not set up, so a stock photograph was used.")

        if mode == "ai":
            if not poster_imagery.is_configured():
                raise TemplateGenerationError(
                    "AI images are not set up on this service. Choose stock, upload or no photo.")
            description = self.photo.description or plan.photo_query or plan.headline
            try:
                data, _ = await poster_imagery.generate_poster_image(
                    description, slot.generation_aspect, centred=slot.masked)
            except poster_imagery.ImageGenerationError as exc:
                # Not swapped for a stock photo: the pharmacy asked for this.
                raise TemplateGenerationError(f"The AI image could not be made: {exc}") from exc
            self.source, self.origin = data, "ai"
            return

        # Stock.
        try:
            if self.photo.stock_photo_id:
                data, _, _ = await stock.fetch(self.photo.stock_photo_id)
                self.source, self.origin = data, "stock"
                return
            query = plan.photo_query or plan.headline
            results = await stock.search(query, orientation=slot.stock_orientation, per_page=10)
            if not results:
                results = await stock.search(query, per_page=10)
            if not results:
                self.notes.append(
                    f"No stock photograph matched “{query}”, so this design is photo-free.")
                return
            data, _, _ = await stock.fetch(results[0]["id"])
            self.source, self.origin = data, "stock"
        except stock.StockPhotoError as exc:
            if self.photo.stock_photo_id:
                raise TemplateGenerationError(f"The chosen stock photo could not be used: {exc}") from exc
            # The default photo source is unavailable: say so, design without.
            log.warning("Stock photo unavailable, designing photo-free: %s", exc)
            self.notes.append("Stock photography is unavailable right now, so this design is photo-free.")


# ── generation ──────────────────────────────────────────────────────────────


async def generate_template(
    prompt: str,
    owner_id: str,
    category: str | None = None,
    archetype: str | None = None,
    photo: dict[str, Any] | None = None,
    llm: Any = None,
) -> tuple[dict[str, Any], list[str], list[dict[str, Any]], list[str]]:
    """
    Design and check a house-style poster.

    Returns (canvas, notes, assets, photo_notes). ``assets`` holds the baked
    photograph, if any, for the API to store; the canvas refers to it as
    ``PHOTO_KEY``. ``notes`` records repair rounds (the admin console shows
    them as "look closer"); ``photo_notes`` says what happened to the photo
    when it was not what was asked for — no stock match, stock unavailable, AI
    not set up — so no choice is ever changed silently, for an admin or a
    pharmacy.

    Raises TemplateGenerationError if nothing valid came back.
    """
    archetype = canonical_archetype(archetype) or None  # blank means "the model chooses"
    # "Bold" is a tone. Asked for as a layout (an older admin build offered
    # it), it means a bold poster in whichever layout the brief suits.
    tone = tone_for_brief(prompt)
    if archetype == "bold":
        archetype, tone = None, "bold"
    if archetype:
        if archetype not in ARCHETYPES:
            raise TemplateGenerationError(f"Unknown archetype {archetype!r}.")

    source = _PhotoSource(Photo.from_request(photo), prompt)
    model = llm or _llm()
    messages: list[Any] = [
        SystemMessage(content=_system_prompt()),
        HumanMessage(content=_brief(prompt, category, archetype, source.photo)),
    ]
    notes: list[str] = []
    problems: list[str] = []

    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            reply = await model.ainvoke(messages)
        except Exception as exc:
            # The provider's error text can carry request ids, deployment
            # names and quota details: log it, tell the caller less (SEC-17).
            log.error("Design model call failed: %s", exc)
            raise TemplateGenerationError(
                "The design service could not be reached. Try again in a minute.") from exc
        raw = text_of(reply)

        plan: Plan | None = None
        try:
            plan, problems = Plan.from_json(_extract_json(raw))
        except (json.JSONDecodeError, ValueError) as exc:
            problems = [f"The reply was not valid JSON: {exc}"]

        if plan is not None and archetype and plan.archetype != archetype:
            # The caller chose the layout; the model only writes for it.
            plan.archetype = archetype
            problems = plan.problems()

        if plan is not None and tone == "bold":
            # From the brief, not the model: a bold brief is set large whatever
            # layout was chosen for it.
            plan.tone = "bold"

        if plan is not None and not problems:
            baked = await source.get(plan)
            doc, problems = compose(plan, has_photo=baked is not None)
            if not problems:
                supplied = ({"url": PHOTO_KEY, "width": baked.width, "height": baked.height}
                            if baked else None)
                problems = validate_canvas(doc, supplied_image=supplied)
            if not problems:
                doc = _finalise(doc, owner_id)
                assets = [] if baked is None else [{
                    "key": PHOTO_KEY,
                    "data_base64": base64.b64encode(baked.data).decode("ascii"),
                    "content_type": baked.content_type,
                    "width": baked.width,
                    "height": baked.height,
                    "source": source.origin,
                }]
                log.info("Designed %r as %s (%s) in %d attempt(s) (photo: %s)",
                         plan.name, plan.archetype, plan.tone, attempt,
                         source.origin if baked else "none")
                photo_notes = list(source.notes)
                arch = ARCHETYPES[plan.archetype]
                if baked is not None and plan.items and not arch.items_with_photo:
                    photo_notes.append(
                        f"The {plan.archetype} layout shows no list beside a photo, so the "
                        "points were left out. Choose checklist or circle portrait to keep them.")
                return doc, notes, assets, photo_notes

        log.info("Design attempt %d failed %d rule(s): %s",
                 attempt, len(problems), "; ".join(problems[:3]))
        if attempt == MAX_ATTEMPTS:
            break
        notes.append(f"Attempt {attempt} needed repair: {len(problems)} issue(s).")
        messages.append(AIMessage(content=raw))
        messages.append(HumanMessage(content=(
            "That plan does not work yet:\n\n"
            + "\n".join(f"- {p}" for p in problems)
            + "\n\nFix every one and return the COMPLETE corrected JSON object. Keep "
              "what was already working; shorten rather than drop the message.")))

    raise TemplateGenerationError(
        f"Could not produce a valid design in {MAX_ATTEMPTS} attempts.", problems)

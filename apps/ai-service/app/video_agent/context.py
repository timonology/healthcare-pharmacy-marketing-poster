"""
Everything the video agent needs to know about a job. Built once at
worker pickup so graph nodes are pure functions of state + context.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING

if TYPE_CHECKING:  # pragma: no cover - import cycle at runtime, fine for typing
    from .editor_options import EditorOptions
    from .usage import UsageLedger


@dataclass
class BrandContext:
    """The pharmacy's identity, as stamped on the job by the .NET side.

    **There is no resolution logic here, and there must not be.** A pharmacy's
    name lives in two records — the onboarding profile and the brand kit — and
    deciding which wins is a rule with real consequences: get it wrong and a
    video names the pharmacy one thing while the poster in the same campaign
    names it another.

    That rule lives in C# (``PharmacyBrand.Resolve``), where the campaign email
    and the poster tokens also use it. The .NET side resolves it once when the
    video is queued, writes the answer onto the document, and re-resolves it on
    Retry so a corrected profile reaches the next attempt. This class is the
    read side of that: field for field, no precedence, no fallbacks.

    Re-implementing the precedence here would put the same rule in two languages
    with nothing keeping them in step — which is the shape of nearly every fault
    this codebase has had.
    """

    #: Never blank. "Pharmacy" when nothing was filled in — but see `usable`.
    name: str

    address: str = ""
    phone: str = ""

    #: Already validated and normalised on the .NET side. http/https only.
    booking_url: str | None = None

    #: Storage key, not a URL. Download it through the storage client.
    logo_blob_key: str | None = None

    primary_color: str | None = None
    secondary_color: str | None = None
    accent_color: str | None = None

    license_number: str = ""
    regulatory_footer: str = ""

    #: Whether a real pharmacy name was found when this was resolved.
    #:
    #: False means render unbranded. Stamping the word "Pharmacy" across the end
    #: of somebody's video is worse than leaving it clean, and this flag is the
    #: recorded answer rather than a guess made by comparing `name` to a
    #: constant — a pharmacy genuinely trading as "Pharmacy" would defeat that.
    usable: bool = False


@dataclass
class UserContext:
    user_id: str
    job_id: str

    # Content brief — comes from the CreateVideoRequest on the .NET side.
    topic: str
    niche: str = "pharmacy marketing"
    style_tone: str = "friendly, informative, community-focused"

    # LLM.
    model: str = "claude-haiku-4-5-20251001"

    # Voice — Edge-TTS voice id, or "11labs:<voice_uuid>" for ElevenLabs.
    voice_id: str = "en-US-JennyNeural"

    # Format — pharmacy MVP is always 9:16 15s shorts.
    video_mode: str = "shorts"
    duration_seconds: int = 15
    watermark: bool = False

    # Script-input mode. Set by the job, not the user's profile, so the
    # same user can pick a different mode per video.
    #   "ai"      — agent researches + writes from topic (default).
    #   "guided"  — agent writes, treating `description` as required
    #               creative guidance the writer must follow.
    #   "custom"  — bypass research + writing nodes; `custom_script`
    #               is the narration.
    script_source: str = "ai"
    description: str | None = None
    custom_script: str | None = None

    # Visual rendering backend.
    #   "realistic"        → Pexels stock B-roll (default).
    #   "ai_imagery"       → Replicate Flux Schnell one image per beat,
    #                        Ken Burns per still.
    visual_style: str = "realistic"

    # The pharmacy's own pictures, chosen when the video was queued.
    #
    # Each entry is `{"blobKey": str, "source": "upload"|"logo"|"poster",
    # "description": str|None}` — the shape the .NET side stores, read with
    # `.get()` so an older row without the field is simply a video with none.
    #
    # <b>These are never sent to an image model.</b> They are pinned into the
    # visuals stage as B-roll and cost nothing; `visual_style="ai_imagery"` is a
    # separate, paid feature, and a pharmacist's own photograph going to
    # Replicate would be charging them for a picture they already had.
    user_images: list[dict] = field(default_factory=list)
    # Which AI-imagery aesthetic. Only used when visual_style=ai_imagery.
    image_style: str = "cartoon"

    # The pharmacy this video belongs to. None on every job queued before
    # branding existed, and on any pharmacy that has filled in neither its
    # profile nor its brand kit — both of which render exactly as before.
    brand: BrandContext | None = None

    # Set only on a refine. Overrides the editor applies to this pass —
    # caption size and position, the end card, fade length.
    #
    # Typed and bounded rather than free text: see editor_options. Without
    # something here an editor-stage refine re-cuts identical inputs and hands
    # back an identical video while reporting success, which is the failure this
    # whole feature has to avoid.
    editor_options: "EditorOptions | None" = None

    # Moments to re-fill with different footage, `{beat index: stock search}`.
    #
    # Set only on a shot-edit refine. Empty on a first render and on every other
    # kind of refine, so the visuals node's ordinary path is untouched — an
    # override is an exception to the scriptwriter's choice, not a replacement
    # for it.
    shot_edits: dict[int, str] = field(default_factory=dict)

    # Which moments show which of `user_images`, `{moment: image index}`. Set
    # only on a refine, from `rehydrate.pinned_beats` — what the last render
    # actually showed, minus any photo moment this refine is re-filling. None
    # means "plan it", which is what a first render does.
    user_pins: dict[int, int] | None = None

    # What this render consumed, filled in as it goes.
    #
    # On the context rather than threaded through the graph state because it is
    # written by nodes and read by the worker after the graph has finished —
    # and because a node that raises has still spent the money it spent up to
    # that point. State would be lost on the failure path, which is exactly the
    # render whose cost is most worth knowing.
    #
    # Optional so a test can build a context without one — `test_end_card` does.
    #
    # To be precise about who may assume what, because "it is optional" and
    # "everything handles None" are different claims and only the first is true:
    # every site *inside a node* checks before recording. `_render_sync` does
    # not, and does not need to — it constructs the context and always supplies
    # a ledger, so within a render the field is never None.
    usage: "UsageLedger | None" = None

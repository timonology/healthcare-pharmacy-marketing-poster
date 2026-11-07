"""Synchronous video endpoints the .NET API calls while someone is waiting.

Rendering itself is dispatched through Mongo — the worker polls a collection and
nobody is sitting watching. Classifying a refine instruction is the opposite: a
pharmacist has typed what they want changed and is waiting to be told what that
will do before they agree to it. So it is a request/response call, for the same
reason template generation is.
"""
from __future__ import annotations

import logging
from typing import Annotated

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field

from app.chains.refine_router import MAX_INSTRUCTION_CHARS, classify
from app.security import CurrentUser, get_current_user, per_user_limit

log = logging.getLogger(__name__)

router = APIRouter(prefix="/video", tags=["video"])


class ClassifyRefineRequest(BaseModel):
    # The same ceiling the classifier applies, rather than a looser one here.
    # A route that accepts 2000 characters and then hands them to something
    # that refuses above 1000 turns a clear "one change at a time" into a
    # shrug, which is the answer that tells the pharmacist least.
    instruction: str = Field(min_length=1, max_length=MAX_INSTRUCTION_CHARS)


class ClassifyRefineResponse(BaseModel):
    """What the pharmacist will be shown before anything runs.

    `stage` is a suggestion. The .NET side re-checks it against its own list of
    what each stage costs and which are open, and it is the authority on both —
    a stage named here does not become a stage that runs.
    """

    stage: str
    reason: str
    free: bool


@router.post("/classify-refine", response_model=ClassifyRefineResponse)
def classify_refine(
    request: ClassifyRefineRequest,
    # 20 a minute per pharmacy: a person asks for a change and reads the
    # answer; a script calling this service directly would otherwise spend
    # model budget without limit (security review SEC-08).
    user: Annotated[CurrentUser, Depends(per_user_limit("classify-refine", 20))],
) -> ClassifyRefineResponse:
    """Route a change request to the earliest pipeline stage that can satisfy it.

    Any pharmacy user may call this. It reads nothing, writes nothing, and
    starts nothing — it costs one short model call and answers a question about
    the pharmacist's own sentence.

    Never returns an error for a bad instruction. Something unroutable comes
    back as `stage: "unclear"` with a sentence asking for a rephrase, because a
    500 here would read to the pharmacist as "the product is broken" when the
    truth is "we did not understand that".
    """
    result = classify(request.instruction)
    log.info(
        "Refine classified for user=%s -> stage=%s free=%s",
        user.id, result["stage"], result["free"],
    )
    return ClassifyRefineResponse(**result)


class VoiceOption(BaseModel):
    id: str
    label: str


class NarrationResponse(BaseModel):
    """Which voices this deployment can actually use, and what they cost.

    Answered here because this service is the only one that knows whether
    ``ELEVENLABS_API_KEY`` is set, and that key is what decides the provider.
    The browser used to decide from the voice id and was wrong in two visible
    ways on every hosted deployment: it offered Edge voices that would be
    silently swapped for ElevenLabs, and it priced those renders at a tenth of
    what they cost. Teaching the web and the .NET API to read the key instead
    would put one fact in three places — which is how the original bug got in.
    """

    provider: str
    voices: list[VoiceOption]
    defaultVoiceId: str
    estimateUsdPerVideo: float


@router.get("/narration", response_model=NarrationResponse)
def narration(
    user: Annotated[CurrentUser, Depends(get_current_user)],
) -> NarrationResponse:
    """The voice picker's contents.

    Authenticated like everything else on this router, though it exposes no
    pharmacy data — it describes the deployment, not the caller. It deliberately
    does not report whether the key is *valid*: that would mean a live call to
    ElevenLabs every time somebody opens the dialog, and an invalid key already
    surfaces where it matters, as the render's stored diagnostic.
    """
    from app.config import get_settings
    from app.video_agent.voices import catalogue

    s = get_settings()
    result = catalogue(
        key_configured=bool(s.elevenlabs_api_key),
        configured_default=s.elevenlabs_default_voice_id,
    )
    log.debug("Narration options requested by user=%s -> %s", user.id, result["provider"])
    return NarrationResponse(**result)

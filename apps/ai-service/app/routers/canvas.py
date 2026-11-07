import base64
import logging
from typing import Annotated, Any, Literal

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field

from app.chains.canvas_assistant import suggest_edits
from app.chains.house_style import ARCHETYPES, HOUSE_COLOURS
from app.chains.poster_imagery import (
    ASPECT_RATIOS,
    DEFAULT_ASPECT,
    ImageGenerationError,
    generate_poster_image,
)
from app.chains.poster_imagery import is_configured as ai_imagery_configured
from app.chains import stock_photos as stock
from app.chains.template_designer import (
    TemplateGenerationError,
    generate_template,
)
from app.schemas.canvas import CanvasDocument
from app.security import (
    CurrentUser,
    get_current_user,
    require_poster_designer,
    require_staff,
    require_stock_searcher,
)

log = logging.getLogger(__name__)

router = APIRouter(prefix="/canvas", tags=["canvas"])


class SuggestRequest(BaseModel):
    document: CanvasDocument
    instruction: str


class SuggestResponse(BaseModel):
    suggestion: str


class PhotoRequest(BaseModel):
    """Where the poster's photograph comes from. See template_designer.

    - none: typographic / geometric.
    - stock: ``stock_photo_id`` chosen by the user, or the best match for the
      design. Always fetched by ID, never by a caller URL.
    - upload: the image bytes, read by the API from its own storage. Bytes,
      not a URL, so this service never fetches anything a caller names.
    - ai: generated, if and only if an image model is configured.
    """

    mode: Literal["none", "stock", "upload", "ai"] = "none"
    stock_photo_id: str | None = Field(None, max_length=32)
    # 12MB decoded is about 16MB of base64.
    data_base64: str | None = Field(None, max_length=17_000_000)
    content_type: str | None = None
    description: str | None = Field(None, max_length=600)


class GenerateTemplateRequest(BaseModel):
    prompt: str = Field(min_length=8, max_length=2000)
    category: str | None = None
    # Accepted and ignored: the house palette is fixed. Kept so an older API
    # build sending it is not refused mid-deploy.
    palette: str | None = None
    archetype: str | None = None
    photo: PhotoRequest | None = None


class GeneratedAsset(BaseModel):
    """A baked photograph for the API to store and swap in for ``key``."""

    key: str
    data_base64: str
    content_type: str
    width: float
    height: float
    source: str


class GenerateTemplateResponse(BaseModel):
    canvas: dict[str, Any]
    name: str
    notes: list[str] = []
    assets: list[GeneratedAsset] = []
    # What happened to the photo, when it was not what was asked for.
    photo_notes: list[str] = []


class ImageOptionsResponse(BaseModel):
    """Which photo sources this deployment can actually serve."""

    ai: bool
    stock: bool


class DesignOptionsResponse(BaseModel):
    palettes: list[dict[str, str]]
    archetypes: list[dict[str, str]]


class StockPhoto(BaseModel):
    id: str
    thumbnail_url: str
    width: float
    height: float
    photographer: str = ""
    alt: str = ""


class StockSearchResponse(BaseModel):
    photos: list[StockPhoto]


class StockFetchRequest(BaseModel):
    """Photo ID, never a URL — see stock_photos.fetch for why."""

    photo_id: str = Field(min_length=1, max_length=32)


class StockFetchResponse(BaseModel):
    data_base64: str
    content_type: str
    width: float
    height: float
    photographer: str = ""
    alt: str = ""


class GenerateImageRequest(BaseModel):
    description: str = Field(min_length=4, max_length=600)
    aspect: str = DEFAULT_ASPECT
    # "ai" means AI or an error — never a stock photo instead. "auto" keeps
    # the old behaviour (AI when configured, stock otherwise) for callers that
    # asked for "an image" rather than an AI one.
    provider: Literal["auto", "ai", "stock"] = "auto"
    # Which candidate to take when falling back to stock search. Lets the UI
    # offer "try another" without re-typing the description.
    offset: int = Field(0, ge=0, le=29)


class GenerateImageResponse(BaseModel):
    """
    The image itself, base64-encoded.

    Returned inline rather than uploaded here so that blob storage stays owned
    by the .NET API — it already mints the permanent asset URLs and knows the
    container layout. Duplicating that in Python would mean two services
    agreeing on a URL scheme forever. A few hundred KB over an internal hop is
    a cheap price for keeping storage in one place.
    """

    data_base64: str
    content_type: str
    # Natural size, so the designer can place the image without stretching it.
    width: float = 0
    height: float = 0
    # "ai" or "stock" — the UI says which was used, since the caller asked for
    # "an image" and shouldn't have to guess where it came from.
    source: str = "ai"
    description: str | None = None
    # How many further candidates exist, so the UI can hide "try another" when
    # there is nothing left to try.
    remaining: int = 0


@router.post("/suggest", response_model=SuggestResponse)
async def suggest(
    body: SuggestRequest,
    user: Annotated[CurrentUser, Depends(require_staff)],
) -> SuggestResponse:
    """Staff only.

    Nothing in the product calls this today. It was open to any signed-in user,
    and ai-service is internet-reachable, so a pharmacy could drive model spend
    through it with no allowance (security review SEC-08).
    """
    _ = user  # auditing hook lives here
    text = await suggest_edits(body.document, body.instruction)
    return SuggestResponse(suggestion=text)


@router.get("/design-options", response_model=DesignOptionsResponse)
async def design_options(
    user: Annotated[CurrentUser, Depends(require_staff)],
) -> DesignOptionsResponse:
    """The house layouts, so the admin UI offers the same vocabulary the
    generator understands instead of duplicating the list."""
    _ = user
    return DesignOptionsResponse(
        palettes=[{"name": "house", "mood": "Medical blue, white and teal.",
                   "colours": ", ".join(sorted(HOUSE_COLOURS))}],
        archetypes=[{"name": a.name, "description": a.summary} for a in ARCHETYPES.values()],
    )


@router.get("/image-options", response_model=ImageOptionsResponse)
async def image_options(
    user: Annotated[CurrentUser, Depends(get_current_user)],
) -> ImageOptionsResponse:
    """Whether AI images and stock photography are configured.

    Any signed-in user: the answer decides whether the AI mode is offered at
    all, and a mode that is shown and then fails is worse than one not shown.
    """
    _ = user
    return ImageOptionsResponse(ai=ai_imagery_configured(), stock=stock.is_configured())


@router.get("/stock-photos", response_model=StockSearchResponse)
async def stock_photos(
    user: Annotated[CurrentUser, Depends(require_stock_searcher)],
    query: str,
    aspect: str | None = None,
    per_page: int = 15,
) -> StockSearchResponse:
    """Search Pexels for candidate poster photography.

    Staff, or a pharmacy whose search the API has rate-limited and signed for.
    Thumbnails are Pexels CDN URLs for the picker only; the chosen photo is
    fetched by ID during generation and stored by the API.
    """
    _ = user
    try:
        results = await stock.search(
            query,
            orientation=stock.resolve_orientation(aspect),
            per_page=per_page,
        )
    except stock.StockPhotoError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail={"error": str(exc)},
        ) from exc

    return StockSearchResponse(
        photos=[
            StockPhoto(
                id=p["id"],
                thumbnail_url=p["thumbnailUrl"],
                width=p["width"],
                height=p["height"],
                photographer=p["photographer"],
                alt=p["alt"],
            )
            for p in results
        ]
    )


@router.post("/stock-photo", response_model=StockFetchResponse)
async def stock_photo(
    body: StockFetchRequest,
    user: Annotated[CurrentUser, Depends(require_staff)],
) -> StockFetchResponse:
    """Download one photo so the API can store it as a template asset."""
    _ = user
    try:
        data, content_type, meta = await stock.fetch(body.photo_id)
    except stock.StockPhotoError as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail={"error": str(exc)},
        ) from exc

    return StockFetchResponse(
        data_base64=base64.b64encode(data).decode("ascii"),
        content_type=content_type,
        width=meta["width"],
        height=meta["height"],
        photographer=meta["photographer"],
        alt=meta["alt"],
    )


@router.post("/generate-image", response_model=GenerateImageResponse)
async def generate_image(
    body: GenerateImageRequest,
    user: Annotated[CurrentUser, Depends(require_staff)],
) -> GenerateImageResponse:
    """
    Produce a hero photograph for a poster. Staff only.

    Picks the provider automatically: an AI model when one is configured,
    otherwise stock photography. The decision lives here because this is the
    only place that knows which credentials exist — pushing it to the caller
    would mean the UI needs to know the deployment's configuration, and would
    dead-end anyone who hasn't set up an image model.
    """
    if body.aspect not in ASPECT_RATIOS:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"aspect must be one of {sorted(ASPECT_RATIOS)}.",
        )

    # AI first when available — it can render a scene stock may not have.
    #
    # `offset` is ignored here on purpose: image models are non-deterministic,
    # so "try another" is simply another call. Gating AI to offset==0 would make
    # the second press silently return a stock photo instead, which is a
    # confusing switch of medium mid-task.
    if body.provider == "ai" and not ai_imagery_configured():
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail={"error": "AI images are not set up. Set REPLICATE_API_TOKEN, or "
                             "choose a stock photograph instead."},
        )

    if body.provider != "stock" and ai_imagery_configured():
        try:
            data, content_type = await generate_poster_image(
                body.description, body.aspect
            )
            return GenerateImageResponse(
                data_base64=base64.b64encode(data).decode("ascii"),
                content_type=content_type,
                source="ai",
                description=body.description,
            )
        except ImageGenerationError as exc:
            if body.provider == "ai":
                # Asked for AI specifically: say it failed, do not hand back a
                # stock photo the caller did not choose.
                raise HTTPException(
                    status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
                    detail={"error": f"The AI image could not be made: {exc}"},
                ) from exc
            # "auto": fall through to stock. An admin waiting on a poster cares
            # about getting a usable image, not about which provider made it.
            log.warning(
                "AI imagery failed for %s, falling back to stock: %s",
                user.email, exc,
            )

    if not stock.is_configured():
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail={"error": (
                "No image source is configured. Set PEXELS_API_KEY for stock "
                "photography, or REPLICATE_API_TOKEN for AI generation."
            )},
        )

    try:
        results = await stock.search(
            body.description,
            orientation=stock.resolve_orientation(body.aspect),
            per_page=30,
        )
        if not results:
            raise HTTPException(
                status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
                detail={"error": (
                    f"No photographs matched “{body.description}”. "
                    "Try describing the scene more simply."
                )},
            )

        index = min(body.offset, len(results) - 1)
        chosen = results[index]
        data, content_type, meta = await stock.fetch(chosen["id"])
    except stock.StockPhotoError as exc:
        log.warning("Stock lookup failed for %s: %s", user.email, exc)
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail={"error": str(exc)},
        ) from exc

    return GenerateImageResponse(
        data_base64=base64.b64encode(data).decode("ascii"),
        content_type=content_type,
        width=meta["width"],
        height=meta["height"],
        source="stock",
        # Pexels' alt text describes the actual photo, which is better for
        # layout than the user's request — the designer never sees the image.
        description=meta["alt"] or body.description,
        remaining=max(0, len(results) - index - 1),
    )


# Staff, or a pharmacy user the API has vouched for — see
# require_poster_designer. A pharmacy's AI photograph is made inside this call,
# under the same allowance as the design, so it is never a second charge; the
# standalone /generate-image above stays staff-only because each call there is
# billed separately.
@router.post("/generate-template", response_model=GenerateTemplateResponse)
async def generate(
    body: GenerateTemplateRequest,
    user: Annotated[CurrentUser, Depends(require_poster_designer)],
) -> GenerateTemplateResponse:
    """Generate a poster template canvas from a brief. Staff only.

    Returns a canvas that has passed every print rule — the caller can preview
    it, open it in the designer, or publish it straight to the gallery. It is
    deliberately NOT persisted here: ai-service does not own templates, and a
    human should see the design before the whole platform does.
    """
    try:
        canvas, notes, assets, photo_notes = await generate_template(
            prompt=body.prompt,
            owner_id=user.id,
            category=body.category,
            archetype=body.archetype,
            photo=body.photo.model_dump() if body.photo else None,
        )
    except TemplateGenerationError as exc:
        log.warning("Template generation failed for %s: %s", user.email, exc)
        # 422 rather than 500: the request was well-formed, the model just
        # could not satisfy the brief. The admin UI shows `problems` so the
        # user can adjust the brief rather than blindly retrying.
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_CONTENT,
            detail={"error": str(exc), "problems": exc.problems},
        ) from exc

    return GenerateTemplateResponse(
        canvas=canvas,
        name=str(canvas.get("name") or "Untitled template"),
        notes=notes,
        assets=[GeneratedAsset(**a) for a in assets],
        photo_notes=photo_notes,
    )

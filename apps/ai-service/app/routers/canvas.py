from typing import Annotated

from fastapi import APIRouter, Depends
from pydantic import BaseModel

from app.chains.canvas_assistant import suggest_edits
from app.schemas.canvas import CanvasDocument
from app.security import CurrentUser, get_current_user

router = APIRouter(prefix="/canvas", tags=["canvas"])


class SuggestRequest(BaseModel):
    document: CanvasDocument
    instruction: str


class SuggestResponse(BaseModel):
    suggestion: str


@router.post("/suggest", response_model=SuggestResponse)
async def suggest(
    body: SuggestRequest,
    user: Annotated[CurrentUser, Depends(get_current_user)],
) -> SuggestResponse:
    """Authenticated endpoint — only callable with a JWT issued by the .NET API."""
    _ = user  # auditing hook lives here
    text = await suggest_edits(body.document, body.instruction)
    return SuggestResponse(suggestion=text)

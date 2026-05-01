"""
Pydantic mirrors of packages/shared-types/src/canvas.ts.
Keep field names in sync (TypeScript uses camelCase — ConfigDict aliases handle that).
"""

from datetime import datetime
from typing import Annotated, Any, Literal, Union

from pydantic import BaseModel, ConfigDict, Field

CANVAS_SCHEMA_VERSION = 1


class _CamelModel(BaseModel):
    model_config = ConfigDict(populate_by_name=True, alias_generator=None)


class Vector2(_CamelModel):
    x: float
    y: float


class _BaseShape(_CamelModel):
    id: str
    position: Vector2
    rotation: float = 0.0
    scale: Vector2 = Field(default_factory=lambda: Vector2(x=1, y=1))
    opacity: float = 1.0
    draggable: bool = True
    z_index: int = Field(0, alias="zIndex")


class RectShape(_BaseShape):
    kind: Literal["rect"] = "rect"
    width: float
    height: float
    fill: str
    stroke: str | None = None
    stroke_width: float | None = Field(None, alias="strokeWidth")
    corner_radius: float | None = Field(None, alias="cornerRadius")


class CircleShape(_BaseShape):
    kind: Literal["circle"] = "circle"
    radius: float
    fill: str
    stroke: str | None = None
    stroke_width: float | None = Field(None, alias="strokeWidth")


class LineShape(_BaseShape):
    kind: Literal["line"] = "line"
    points: list[float]
    stroke: str
    stroke_width: float = Field(..., alias="strokeWidth")
    closed: bool | None = None


class TextShape(_BaseShape):
    kind: Literal["text"] = "text"
    text: str
    font_size: float = Field(..., alias="fontSize")
    font_family: str = Field(..., alias="fontFamily")
    fill: str
    width: float | None = None
    align: Literal["left", "center", "right"] | None = None


class ImageShape(_BaseShape):
    kind: Literal["image"] = "image"
    blob_key: str = Field(..., alias="blobKey")
    width: float
    height: float


class GroupShape(_BaseShape):
    kind: Literal["group"] = "group"
    children: list[str]


Shape = Annotated[
    Union[RectShape, CircleShape, LineShape, TextShape, ImageShape, GroupShape],
    Field(discriminator="kind"),
]


class CanvasViewport(_CamelModel):
    pan: Vector2
    zoom: float = 1.0


class CanvasDocument(_CamelModel):
    schema_version: int = Field(CANVAS_SCHEMA_VERSION, alias="schemaVersion")
    id: str
    owner_id: str = Field(..., alias="ownerId")
    name: str
    width: int
    height: int
    background: str
    viewport: CanvasViewport
    shapes: list[Shape]
    created_at_utc: datetime = Field(..., alias="createdAtUtc")
    updated_at_utc: datetime = Field(..., alias="updatedAtUtc")

    def to_summary(self) -> dict[str, Any]:
        """Compact summary for LLM prompts."""
        return {
            "name": self.name,
            "size": {"w": self.width, "h": self.height},
            "shape_count": len(self.shapes),
            "shape_kinds": [s.kind for s in self.shapes],
        }

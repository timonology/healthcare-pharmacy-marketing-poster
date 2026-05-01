/**
 * Canvas JSON state — source of truth.
 *
 * Consumed by:
 *   - apps/web (React-Konva renderer + Zustand store)
 *   - apps/api (mirrored as C# records in Acme.Application/Canvas/)
 *   - apps/ai-service (mirrored as Pydantic models in app/schemas/canvas.py)
 *
 * Versioning: bump CANVAS_SCHEMA_VERSION on any breaking change and add a
 * migration in apps/web/src/lib/canvas/migrate.ts.
 */

export const CANVAS_SCHEMA_VERSION = 1;

export type ShapeId = string;

export type ShapeKind =
  | "rect"
  | "circle"
  | "line"
  | "text"
  | "image"
  | "group";

export interface Vector2 {
  x: number;
  y: number;
}

export interface BaseShape {
  id: ShapeId;
  kind: ShapeKind;
  position: Vector2;
  rotation: number; // degrees
  scale: Vector2;
  opacity: number; // 0..1
  draggable: boolean;
  zIndex: number;
}

export interface RectShape extends BaseShape {
  kind: "rect";
  width: number;
  height: number;
  fill: string;
  stroke?: string;
  strokeWidth?: number;
  cornerRadius?: number;
}

export interface CircleShape extends BaseShape {
  kind: "circle";
  radius: number;
  fill: string;
  stroke?: string;
  strokeWidth?: number;
}

export interface LineShape extends BaseShape {
  kind: "line";
  points: number[]; // [x1,y1,x2,y2,...]
  stroke: string;
  strokeWidth: number;
  closed?: boolean;
}

export interface TextShape extends BaseShape {
  kind: "text";
  text: string;
  fontSize: number;
  fontFamily: string;
  fill: string;
  width?: number;
  align?: "left" | "center" | "right";
}

export interface ImageShape extends BaseShape {
  kind: "image";
  /** Blob URL — relative to AZURE_BLOB_CONTAINER. */
  blobKey: string;
  width: number;
  height: number;
}

export interface GroupShape extends BaseShape {
  kind: "group";
  children: ShapeId[];
}

export type Shape =
  | RectShape
  | CircleShape
  | LineShape
  | TextShape
  | ImageShape
  | GroupShape;

export interface CanvasViewport {
  pan: Vector2;
  zoom: number;
}

export interface CanvasDocument {
  schemaVersion: number;
  id: string;
  ownerId: string;
  name: string;
  width: number;
  height: number;
  background: string;
  viewport: CanvasViewport;
  shapes: Shape[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

/** Convenience constructor for an empty document. */
export function emptyCanvas(
  id: string,
  ownerId: string,
  name = "Untitled",
): CanvasDocument {
  const now = new Date().toISOString();
  return {
    schemaVersion: CANVAS_SCHEMA_VERSION,
    id,
    ownerId,
    name,
    width: 1920,
    height: 1080,
    background: "#ffffff",
    viewport: { pan: { x: 0, y: 0 }, zoom: 1 },
    shapes: [],
    createdAtUtc: now,
    updatedAtUtc: now,
  };
}

import type {
  CircleShape,
  ImageShape,
  LineShape,
  RectShape,
  Shape,
  TextShape,
  Vector2,
} from "@acme/shared-types";

const id = () => crypto.randomUUID();

const baseShape = (zIndex: number) => ({
  id: id(),
  rotation: 0,
  scale: { x: 1, y: 1 } satisfies Vector2,
  opacity: 1,
  draggable: true,
  zIndex,
});

export function makeRect(at: Vector2, zIndex: number): RectShape {
  return {
    ...baseShape(zIndex),
    kind: "rect",
    position: at,
    width: 200,
    height: 120,
    fill: "#3b82f6",
    cornerRadius: 8,
  };
}

export function makeCircle(at: Vector2, zIndex: number): CircleShape {
  return {
    ...baseShape(zIndex),
    kind: "circle",
    position: at,
    radius: 60,
    fill: "#10b981",
  };
}

export function makeText(at: Vector2, zIndex: number): TextShape {
  return {
    ...baseShape(zIndex),
    kind: "text",
    position: at,
    text: "Double-click to edit",
    fontSize: 28,
    fontFamily: "Inter, sans-serif",
    fill: "#0f172a",
    align: "left",
  };
}

export function makeLine(at: Vector2, zIndex: number): LineShape {
  return {
    ...baseShape(zIndex),
    kind: "line",
    position: at,
    points: [0, 0, 200, 0],
    stroke: "#0f172a",
    strokeWidth: 4,
    closed: false,
  };
}

export function makeImage(
  at: Vector2,
  zIndex: number,
  blobKey: string,
  width: number,
  height: number,
): ImageShape {
  return {
    ...baseShape(zIndex),
    kind: "image",
    position: at,
    blobKey,
    width,
    height,
  };
}

export function nextZIndex(shapes: Shape[]): number {
  if (shapes.length === 0) return 1;
  return Math.max(...shapes.map((s) => s.zIndex)) + 1;
}

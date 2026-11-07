import { BRAND_BOOKING_QR_SRC } from "@acme/shared-types";
import type {
  CircleShape,
  ImageShape,
  LineShape,
  RectShape,
  Shape,
  TextShape,
  Vector2,
} from "@acme/shared-types";

import { PHARMACY_BOOKING_QR_ID } from "./apply-pharmacy";

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

/**
 * The booking-QR slot, as a shape a pharmacist can place themselves.
 *
 * <b>Why this exists at all.</b> `applyPharmacyInfo` fills a slot with this id;
 * it never creates one, deliberately — dropping a QR onto every design unasked
 * would be the app editing somebody's artwork. But nothing else created one
 * either: no built-in template carries the slot and the editor had no way to
 * add it, so the whole feature was unreachable. A slot nothing creates is a
 * feature nobody has.
 *
 * <b>The id is fixed, not generated.</b> Everything downstream — the applier
 * here, `BrandCanvasAssets.ResolveForRender` on the API, the render cache key —
 * finds the QR by this exact id. A random one would produce a shape that looks
 * like a QR in the editor and resolves to nothing on the printed poster.
 *
 * Which also means one per design. The caller checks before adding; a second
 * would silently overwrite the first when the applier upserts by id.
 */
export function makeBookingQr(at: Vector2, zIndex: number, size = 220): ImageShape {
  return {
    ...baseShape(zIndex),
    id: PHARMACY_BOOKING_QR_ID,
    kind: "image",
    position: at,
    // The path, not an image. Each renderer resolves it at render time, which
    // is what makes "change your booking link and every poster follows" work —
    // the same arrangement the logo uses.
    blobKey: BRAND_BOOKING_QR_SRC,
    width: size,
    height: size,
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

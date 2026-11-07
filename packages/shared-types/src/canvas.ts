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

import { DEFAULT_PAGE_SIZE, pageDimensions } from "./page-size";

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
  /**
   * The wording this shape was authored with, kept when `{{tokens}}` in it are
   * filled in.
   *
   * Replacing a token destroys it, so without this the pharmacy's details could
   * be applied to a template exactly once: correct a typo in your phone number
   * afterwards and every poster already made keeps the old one, with no way to
   * refresh short of starting again. Applying re-renders from here, so it can
   * be done any number of times.
   *
   * Absent on shapes that never contained a token, which is most of them.
   */
  sourceText?: string;
  fontSize: number;
  fontFamily: string;
  fill: string;
  width?: number;
  align?: "left" | "center" | "right";
}

export interface ImageShape extends BaseShape {
  kind: "image";
  /**
   * Where the picture comes from. One of five forms, and the name is a
   * historical accident — it has not been a bare blob key for a long time:
   *
   *   · `/api/assets/templates/{file}` — an uploaded or AI-generated asset
   *   · {@link BRAND_LOGO_SRC} — this pharmacy's logo, resolved per render
   *   · {@link BRAND_BOOKING_QR_SRC} — a QR for this pharmacy's booking link,
   *     resolved per render in the same way, on the shape whose id is
   *     `pharmacy-booking-qr`
   *   · `data:image/...` — inlined bytes, used while editing before upload
   *   · `https://...` — a remote image, e.g. stock photography
   *
   * <b>Never store a credentialed or expiring URL here.</b> A canvas is saved
   * and re-read for years; a signed URL pasted into one is a picture that works
   * until the signature lapses and then silently disappears from every poster,
   * PDF and email that used it.
   */
  blobKey: string;
  width: number;
  height: number;
}

/**
 * The pharmacy's own logo, as referenced inside a canvas.
 *
 * A stable path rather than a URL to the bytes. The brand kit hands out a
 * fifteen-minute signed URL for display, which is right for a page that is open
 * now and wrong for a document that is saved — so what goes in the canvas is
 * this, and each renderer resolves it at the moment it draws:
 *
 *   · the editor and the app fetch it over the session-authenticated route
 *   · the exporter swaps it for the owner's blob key and reads storage directly
 *
 * The consequence worth stating: change your logo, and every poster you have
 * ever made shows the new one. That is the intended behaviour for a brand mark.
 */
export const BRAND_LOGO_SRC = "/api/brand-kit/logo";

/**
 * A QR code for the pharmacy's booking link, as referenced inside a canvas.
 *
 * Same idea as {@link BRAND_LOGO_SRC} and for the same reason: a stable path in
 * the document, resolved by whoever draws it. The editor fetches this route to
 * show it; the exporter encodes the current booking link and inlines it.
 *
 * So the code always points at whatever the profile says today. Change the
 * booking link and every poster you have made updates — which is right, because
 * the alternative is posters on a wall pointing at a page that no longer exists.
 *
 * A pharmacy with no booking link set gets no QR: the image is dropped from the
 * render rather than drawn as a square that scans to nothing.
 *
 * <b>Deliberately not used in video.</b> A QR is scanned by a second device, so
 * it earns its place on something physical — a printed poster, a shop window.
 * The viewer of a 9:16 short is holding the phone they would have to scan with.
 */
export const BRAND_BOOKING_QR_SRC = "/api/brand-kit/booking-qr";

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

  /**
   * What this artwork is for — a poster, a leaflet, a brochure.
   *
   * Deliberately separate from width and height. A4 is a poster and also the
   * sheet a trifold leaflet is printed on, so the size cannot answer this; and
   * a trifold is only a trifold because somebody meant it to be.
   *
   * Optional because every document made before formats existed is a poster,
   * and reading it as one needs no migration.
   */
  format?: import("./page-size").PrintFormat;

  /**
   * How many panels the sheet folds into: 3 for a trifold, 1 for a flat sheet.
   *
   * Drives the editor's fold guides, which do not print. Absent means flat.
   */
  foldPanels?: import("./page-size").FoldPanels;
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
    // A4 portrait at PRINT_DPI. This used to be 1920x1080, which is a screen
    // aspect, not a paper one — a blank poster came out landscape 16:9 while
    // every seeded template was A4 portrait, and the PDF exporter squashed it
    // onto A4 regardless.
    ...pageDimensions(DEFAULT_PAGE_SIZE, "portrait"),
    background: "#ffffff",
    viewport: { pan: { x: 0, y: 0 }, zoom: 1 },
    shapes: [],
    createdAtUtc: now,
    updatedAtUtc: now,
  };
}

/**
 * Where a pharmacist's photograph sits in the 9:16 frame — the preview's half.
 *
 * The render does the same sums in `apps/ai-service/app/video_agent/framing.py`.
 * Both are pinned to one table of answers, `fixtures/framing-geometry.json`, and
 * `scripts/check-framing.mjs` fails the build if this file stops reproducing it.
 * That is what lets the dialog promise that the framed shot *is* the render.
 *
 * Rounding is written out rather than left to `Math.round`: sizes round to the
 * nearest even number (the encoder needs even dimensions) and offsets round half
 * up. Python's built-in `round` rounds half to even and JavaScript's rounds half
 * up, so either built-in on its own would put the two a pixel apart on exactly
 * the values a slider lands on.
 */

/** Mirrors `ZOOM_MIN` / `ZOOM_MAX` in framing.py and `VideoImage.ZoomMin/Max`. */
export const VIDEO_IMAGE_ZOOM_MIN = 0.5;
export const VIDEO_IMAGE_ZOOM_MAX = 1.5;
export const VIDEO_IMAGE_ZOOM_DEFAULT = 1.0;

/** Within this of 1.0 counts as 1.0 — see `ZOOM_SNAP` in framing.py. */
export const VIDEO_IMAGE_ZOOM_SNAP = 0.005;

/** The frame the render produces. Only its shape matters to the preview. */
export const VIDEO_FRAME = { width: 1080, height: 1920 } as const;

export interface FramingGeometry {
  /** The photo's scaled size, in frame pixels. */
  width: number;
  height: number;
  /** Its top-left corner in the frame. Negative means that much is cut off. */
  x: number;
  y: number;
}

const even = (v: number) => Math.max(2, 2 * Math.floor(v / 2 + 0.5));
const halfUp = (v: number) => Math.floor(v + 0.5);

/** A zoom within bounds; 1.0 when absent, unreadable or within a whisker of it. */
export function clampZoom(value: unknown): number {
  const n = typeof value === "number" ? value : Number(value);
  if (value === null || value === undefined || typeof value === "boolean" || !Number.isFinite(n)) {
    return VIDEO_IMAGE_ZOOM_DEFAULT;
  }
  const z = Math.min(VIDEO_IMAGE_ZOOM_MAX, Math.max(VIDEO_IMAGE_ZOOM_MIN, n));
  return Math.abs(z - VIDEO_IMAGE_ZOOM_DEFAULT) < VIDEO_IMAGE_ZOOM_SNAP
    ? VIDEO_IMAGE_ZOOM_DEFAULT
    : z;
}

/** True when the zoom means "the crop every photo had before zoom existed". */
export function isUntouchedZoom(zoom: number): boolean {
  return Math.abs(zoom - VIDEO_IMAGE_ZOOM_DEFAULT) < VIDEO_IMAGE_ZOOM_SNAP;
}

/**
 * Where the photo lands for any zoom. See `geometry` in framing.py — this is
 * the same function, and the golden fixture keeps it so.
 */
export function framingGeometry(
  imageWidth: number,
  imageHeight: number,
  frameWidth: number,
  frameHeight: number,
  focalX: number,
  focalY: number,
  zoom: number,
): FramingGeometry {
  const scale = Math.max(frameWidth / imageWidth, frameHeight / imageHeight) * zoom;
  const width = even(imageWidth * scale);
  const height = even(imageHeight * scale);

  const x = width >= frameWidth
    ? -halfUp((width - frameWidth) * focalX)
    : Math.floor((frameWidth - width) / 2);
  const y = height >= frameHeight
    ? -halfUp((height - frameHeight) * focalY)
    : Math.floor((frameHeight - height) / 2);

  return { width, height, x, y };
}

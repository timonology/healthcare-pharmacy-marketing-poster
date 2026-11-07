import type { KioskSlide } from "@acme/shared-types";

/**
 * The kiosk's sequencing rules, kept pure so they can be tested without a
 * screen, a network or a `<video>` element.
 *
 * A poster is shown once its image has been fetched (it is cached as an object
 * URL for the life of the page, so it survives the wifi dropping). A video is
 * never cached — it streams from a signed, same-origin path — so it is
 * playable as soon as the content says so, unless it failed recently.
 */

/** What this kiosk tells the API it can play. */
export const KIOSK_CAPABILITIES = "video";

/** How long a video that failed to play is left out before it is tried again. */
export const VIDEO_RETRY_AFTER_MS = 5 * 60 * 1000;

/** How long a video may take to start before it counts as failed. */
export const VIDEO_START_TIMEOUT_MS = 15 * 1000;

export function isVideo(slide: KioskSlide): boolean {
  return slide.kind === "Video" && !!slide.mediaUrl;
}

/**
 * A key for a slide that survives the video link being re-signed.
 *
 * A video's `mediaUrl` carries a signature that rotates every few hours. Keyed
 * on the whole URL, a rotation would count as a different slide and could cut
 * a playing video off mid-way; keyed on the path it is the same video.
 */
export function slideKey(slide: KioskSlide): string {
  if (isVideo(slide)) return `video:${slide.mediaUrl!.split("?")[0]}`;
  return `poster:${slide.imageUrl}`;
}

/**
 * The slides that can go on screen now, in playlist order.
 *
 * - A poster needs its image fetched.
 * - A video needs a media URL and must not be in its retry cool-down.
 * - A video from an API that predates video (no `mediaUrl`) is left out — it
 *   has nothing to play and must not become a broken picture.
 */
export function playableSlides(
  slides: readonly KioskSlide[],
  loadedImages: Readonly<Record<string, string>>,
  failedVideos: ReadonlySet<string>,
): KioskSlide[] {
  return slides.filter((s) => {
    if (s.kind === "Video") return isVideo(s) && !failedVideos.has(slideKey(s));
    return !!s.imageUrl && !!loadedImages[s.imageUrl];
  });
}

/** The poster images the page should fetch and cache. Videos are never cached. */
export function posterImageUrls(slides: readonly KioskSlide[]): string[] {
  return slides.filter((s) => s.kind !== "Video" && !!s.imageUrl).map((s) => s.imageUrl);
}

/**
 * How long before the loop moves on without being told to.
 *
 * A poster: its dwell time. A video moves on when it ends; this is the safety
 * net for a stream that stalls without erroring — generous, so a slow
 * connection that is still playing is not cut off.
 */
export function advanceAfterMs(slide: KioskSlide): number {
  const seconds = Math.max(3, slide.durationSeconds || 0);
  if (isVideo(slide)) return Math.max(seconds * 3, 60) * 1000;
  return seconds * 1000;
}

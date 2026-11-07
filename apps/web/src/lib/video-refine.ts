import type { ShotChoice, Video, VideoImage, VideoShot } from "@acme/shared-types";

/**
 * A picture as the dialogs hold it: the stored image plus its framing, which
 * the dialogs keep as required numbers so the framer always has one to draw.
 */
export interface FramedImage extends VideoImage {
  focalX: number;
  focalY: number;
  zoom: number;
}

/**
 * What the API is sent for each attached picture — every framing field,
 * explicitly.
 *
 * <b>One function for both dialogs, on purpose.</b> The create dialog once
 * destructured `focalX, focalY` and then built the object without them. It
 * typechecked, because dropping an optional field is legal, and centre is also
 * the default, so a centred test photo looked right. Every framing chosen at
 * create was silently discarded while the refine dialog, written separately,
 * sent them. Written once and tested, the two cannot disagree again.
 */
export function toImagePayload(images: readonly FramedImage[]): VideoImage[] {
  return images.map(({ blobKey, source, description, focalX, focalY, zoom }) => ({
    blobKey,
    source,
    description,
    focalX,
    focalY,
    zoom,
  }));
}

export type ChangeOutcome =
  | { kind: "not-made"; videoId: string; title: string; detail: string }
  | { kind: "ready"; videoId: string; title: string; detail: string };

/**
 * Videos that have just finished, and what to tell the pharmacist about each.
 *
 * A change the worker cannot make puts the video straight back to Ready with
 * the reason in `note`. Nothing announced that: the pharmacist watched the
 * video load, saw it unchanged, and reasonably concluded the change had failed
 * in silence. `previous` is each video's status at the last refresh; an empty
 * map (the first load) announces nothing.
 */
export function finishedChanges(
  previous: ReadonlyMap<string, Video["status"]>,
  videos: readonly Video[],
): ChangeOutcome[] {
  const outcomes: ChangeOutcome[] = [];
  for (const video of videos) {
    const was = previous.get(video.id);
    const wasWorking = was === "Pending" || was === "Rendering";
    if (!wasWorking || video.status !== "Ready") continue;
    outcomes.push(
      video.note
        ? { kind: "not-made", videoId: video.id, title: "Your change wasn't made", detail: video.note }
        : { kind: "ready", videoId: video.id, title: "Video ready", detail: `“${video.name}” is up to date.` },
    );
  }
  return outcomes;
}

/**
 * The line a Ready video's card shows about its last requested change, or null.
 *
 * On a Ready video `note` is only ever the reason the last change was not
 * made — a successful render clears it — so it is safe to show as that.
 */
export function lastChangeNotMade(video: Pick<Video, "status" | "note" | "downloadUrl">): string | null {
  if (video.status !== "Ready" || !video.downloadUrl || !video.note) return null;
  return `Last change not made: ${video.note}`;
}

// ── the shot picker ─────────────────────────────────────────────────────

/**
 * What the picker sends: the tapped tiles, in shot order, each with what it
 * should show (trimmed; empty means "the video's own subject").
 *
 * The index is the tile. Nothing about what a shot is said to show is sent or
 * consulted — the shot is the one the pharmacist tapped.
 */
export function toShotChoices(selected: ReadonlyMap<number, string>): ShotChoice[] {
  return [...selected.entries()]
    .sort(([a], [b]) => a - b)
    .map(([index, show]) => {
      const text = show.trim();
      return text ? { index, show: text } : { index };
    });
}

const clock = (seconds: number) => {
  const whole = Math.max(0, Math.round(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
};

/**
 * "Shot 4 · last · 0:15–0:20" — how a tile names itself.
 *
 * First and last come from the tile's place in the list (`at`), not from its
 * moment number: a moment the editor skipped leaves a gap, and the last tile
 * of 0, 1, 3 is moment 3 — which a comparison with the count would never call
 * "last".
 */
export function shotLabel(
  shot: Pick<VideoShot, "index" | "startSeconds" | "endSeconds">,
  total: number,
  at: number = shot.index,
): string {
  const position = total === 1 ? null : at === 0 ? "first" : at === total - 1 ? "last" : null;
  return [
    `Shot ${shot.index + 1}`,
    position,
    `${clock(shot.startSeconds)}–${clock(shot.endSeconds)}`,
  ].filter(Boolean).join(" · ");
}

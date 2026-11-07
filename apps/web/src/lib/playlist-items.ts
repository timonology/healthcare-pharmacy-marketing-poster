import {
  PLAYLIST_MAX_ITEMS,
  PLAYLIST_MAX_VIDEOS,
  type PlaylistItem,
  type PlaylistItemKind,
  type Video,
} from "@acme/shared-types";

/**
 * The playlist editor's rules, mirrored from the server so a pharmacist is
 * stopped before Save rather than by it. The server still decides.
 */

/**
 * Videos that can go on a screen: finished and with a file. The same test the
 * server applies (Ready, with a rendered file) — a video mid re-cut is back to
 * Rendering and is not offered.
 */
export function screenReadyVideos(videos: readonly Video[]): Video[] {
  return videos.filter((v) => v.status === "Ready" && !!v.downloadUrl);
}

export function videoCount(items: readonly Pick<PlaylistItem, "kind">[]): number {
  return items.filter((i) => i.kind === "Video").length;
}

/**
 * Whether one more item of this kind can go on the end: under the item and
 * video caps, and not the same item as the one just before it (which on a wall
 * reads as the screen freezing).
 */
export function canAddToPlaylist(
  items: readonly Pick<PlaylistItem, "kind" | "refId">[],
  kind: PlaylistItemKind,
  refId: string,
): boolean {
  if (items.length >= PLAYLIST_MAX_ITEMS) return false;
  if (kind === "Video" && videoCount(items) >= PLAYLIST_MAX_VIDEOS) return false;
  const last = items[items.length - 1];
  return !(last && last.kind === kind && last.refId === refId);
}

/** 83 → "1:23". */
export function formatClock(seconds: number): string {
  const whole = Math.max(0, Math.round(seconds || 0));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
}

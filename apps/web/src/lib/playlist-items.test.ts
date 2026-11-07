import { describe, expect, it } from "vitest";
import { PLAYLIST_MAX_ITEMS, PLAYLIST_MAX_VIDEOS, type Video } from "@acme/shared-types";
import { canAddToPlaylist, formatClock, screenReadyVideos, videoCount } from "./playlist-items";

const v = (id: string, status: Video["status"], downloadUrl: string | null = `/api/videos/${id}/download`) =>
  ({ id, name: id, status, downloadUrl, durationSeconds: 20 }) as Video;

describe("screenReadyVideos", () => {
  it("offers only finished videos with a file", () => {
    const list = [
      v("ready", "Ready"),
      v("pending", "Pending"),
      v("rendering", "Rendering"),
      v("failed", "Failed"),
      v("ready-no-file", "Ready", null),
    ];
    expect(screenReadyVideos(list).map((x) => x.id)).toEqual(["ready"]);
  });
});

describe("canAddToPlaylist", () => {
  const item = (kind: "Poster" | "Video", refId: string) => ({ kind, refId });

  it("refuses the same item straight after itself", () => {
    expect(canAddToPlaylist([item("Video", "a")], "Video", "a")).toBe(false);
    expect(canAddToPlaylist([item("Video", "a")], "Video", "b")).toBe(true);
  });

  it("does not confuse a poster and a video that share an id", () => {
    expect(canAddToPlaylist([item("Poster", "a")], "Video", "a")).toBe(true);
  });

  it("stops at the video cap but still allows posters", () => {
    const videos = Array.from({ length: PLAYLIST_MAX_VIDEOS }, (_, i) => item("Video", `v${i}`));
    expect(videoCount(videos)).toBe(PLAYLIST_MAX_VIDEOS);
    expect(canAddToPlaylist(videos, "Video", "another")).toBe(false);
    expect(canAddToPlaylist(videos, "Poster", "p")).toBe(true);
  });

  it("stops at the item cap for anything", () => {
    const full = Array.from({ length: PLAYLIST_MAX_ITEMS }, (_, i) => item("Poster", `p${i}`));
    expect(canAddToPlaylist(full, "Poster", "x")).toBe(false);
  });
});

describe("formatClock", () => {
  it.each([[0, "0:00"], [9, "0:09"], [83, "1:23"], [59.6, "1:00"]])("%s → %s", (s, out) => {
    expect(formatClock(s)).toBe(out);
  });
});

import { describe, expect, it } from "vitest";
import type { KioskSlide } from "@acme/shared-types";
import {
  advanceAfterMs,
  isVideo,
  playableSlides,
  posterImageUrls,
  slideKey,
} from "./kiosk-playlist";

const poster = (id: string, seconds = 12): KioskSlide => ({
  kind: "Poster",
  imageUrl: `/api/display/slides/${id}`,
  mediaUrl: `/api/display/slides/${id}`,
  name: `Poster ${id}`,
  durationSeconds: seconds,
});

const video = (id: string, seconds = 24, sig = "abc"): KioskSlide => ({
  kind: "Video",
  imageUrl: "",
  mediaUrl: `/api/display/video/${id}?sig=${sig}`,
  name: `Video ${id}`,
  durationSeconds: seconds,
});

describe("playableSlides", () => {
  it("keeps playlist order, posters once their image is in, videos straight away", () => {
    const slides = [poster("p1"), video("v1"), poster("p2"), video("v2")];
    const loaded = { "/api/display/slides/p1": "blob:1" };

    expect(playableSlides(slides, loaded, new Set()).map((s) => s.name))
      .toEqual(["Poster p1", "Video v1", "Video v2"]);
  });

  it("skips a video that just failed, and only that one", () => {
    const slides = [video("v1"), video("v2")];
    const failed = new Set([slideKey(slides[0]!)]);

    expect(playableSlides(slides, {}, failed).map((s) => s.name)).toEqual(["Video v2"]);
  });

  it("never turns a video with nothing to play into a broken picture", () => {
    // A video slide with no mediaUrl (an API from before video) has nothing
    // to play and no image either.
    const bare: KioskSlide = { kind: "Video", imageUrl: "", name: "Old", durationSeconds: 20 };
    expect(playableSlides([bare], {}, new Set())).toEqual([]);
  });

  it("still plays a cached pre-video playlist, which has no mediaUrl on posters", () => {
    const old: KioskSlide = { kind: "Poster", imageUrl: "/api/display/slides/p1", name: "Old", durationSeconds: 10 };
    expect(playableSlides([old], { "/api/display/slides/p1": "blob:1" }, new Set())).toEqual([old]);
  });
});

describe("posterImageUrls", () => {
  it("caches posters only — a video is streamed, never downloaded whole", () => {
    expect(posterImageUrls([poster("p1"), video("v1"), poster("p2")]))
      .toEqual(["/api/display/slides/p1", "/api/display/slides/p2"]);
  });
});

describe("slideKey", () => {
  it("is the same video when only the signature rotated", () => {
    // Otherwise a re-signed link mid-play would count as a new slide.
    expect(slideKey(video("v1", 24, "old"))).toBe(slideKey(video("v1", 24, "new")));
  });

  it("tells a poster and a video apart", () => {
    expect(slideKey(poster("x"))).not.toBe(slideKey(video("x")));
  });
});

describe("advanceAfterMs", () => {
  it("is a poster's dwell time", () => {
    expect(advanceAfterMs(poster("p", 12))).toBe(12_000);
  });

  it("never holds a poster for less than three seconds", () => {
    expect(advanceAfterMs(poster("p", 0))).toBe(3_000);
  });

  it("is only a generous safety net for a video, which moves on when it ends", () => {
    expect(advanceAfterMs(video("v", 24))).toBe(72_000);
    expect(advanceAfterMs(video("v", 8))).toBe(60_000);
    expect(advanceAfterMs(video("v", 24))).toBeGreaterThan(24_000);
  });
});

describe("isVideo", () => {
  it("needs both the kind and something to play", () => {
    expect(isVideo(video("v"))).toBe(true);
    expect(isVideo(poster("p"))).toBe(false);
    expect(isVideo({ ...video("v"), mediaUrl: null })).toBe(false);
  });
});

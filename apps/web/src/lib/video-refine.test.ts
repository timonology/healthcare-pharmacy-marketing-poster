import { describe, expect, it } from "vitest";
import type { Video } from "@acme/shared-types";
import {
  finishedChanges,
  lastChangeNotMade,
  shotLabel,
  toImagePayload,
  toShotChoices,
  type FramedImage,
} from "./video-refine";

const framed = (over: Partial<FramedImage> = {}): FramedImage => ({
  blobKey: "videos/o/attachments/a.jpg",
  source: "upload",
  description: "shopfront",
  focalX: 0.2,
  focalY: 0.8,
  zoom: 0.6,
  ...over,
});

const video = (over: Partial<Video>): Video =>
  ({
    id: "v1",
    name: "Flu clinic",
    status: "Ready",
    note: null,
    downloadUrl: "https://example.test/v1.mp4",
    ...over,
  }) as Video;

describe("the framing chosen in a dialog reaches the render", () => {
  // The create dialog destructured the focal point and then built the object
  // without it — typechecked, looked right for a centred photo, and silently
  // discarded every framing choice made at create.
  it("sends focal point and zoom for every picture", () => {
    expect(toImagePayload([framed()])).toEqual([
      {
        blobKey: "videos/o/attachments/a.jpg",
        source: "upload",
        description: "shopfront",
        focalX: 0.2,
        focalY: 0.8,
        zoom: 0.6,
      },
    ]);
  });

  it("sends nothing the API does not take (no preview URL, no label)", () => {
    const withExtras = { ...framed(), preview: "blob:local", label: "a.jpg" };

    expect(Object.keys(toImagePayload([withExtras])[0]).sort()).toEqual(
      ["blobKey", "description", "focalX", "focalY", "source", "zoom"],
    );
  });
});

describe("a change that was not made is said, not silent", () => {
  // A refused change put the video back to Ready with the reason in `note`;
  // nothing announced it, so it looked like a change that did nothing.
  const working = new Map<string, Video["status"]>([["v1", "Pending"]]);

  it("announces the reason when a change comes back unmade", () => {
    const [outcome] = finishedChanges(working, [video({ note: "No such moment." })]);

    expect(outcome).toMatchObject({ kind: "not-made", detail: "No such moment." });
  });

  it("announces a change that worked", () => {
    const [outcome] = finishedChanges(working, [video({})]);

    expect(outcome.kind).toBe("ready");
  });

  it("says nothing on first load or for videos that were not being worked on", () => {
    expect(finishedChanges(new Map(), [video({ note: "x" })])).toEqual([]);
    expect(finishedChanges(new Map([["v1", "Ready"]]), [video({ note: "x" })])).toEqual([]);
  });

  it("the card shows the reason on a Ready video", () => {
    expect(lastChangeNotMade(video({ note: "No such moment." }))).toBe(
      "Last change not made: No such moment.",
    );
  });

  it("the card shows nothing when there is no reason, or the video is not Ready", () => {
    expect(lastChangeNotMade(video({}))).toBeNull();
    expect(lastChangeNotMade(video({ status: "Rendering", note: "Editing… 40%" }))).toBeNull();
  });
});

describe("the shot picker sends the tile that was tapped", () => {
  it("sends each tapped shot's index and trimmed words, in shot order", () => {
    const selected = new Map([[3, "  a person on a scale "], [0, ""]]);

    expect(toShotChoices(selected)).toEqual([{ index: 0 }, { index: 3, show: "a person on a scale" }]);
  });

  it("names tiles by number, position and time", () => {
    expect(shotLabel({ index: 3, startSeconds: 14.85, endSeconds: 19.8 }, 4)).toBe("Shot 4 · last · 0:15–0:20");
    expect(shotLabel({ index: 0, startSeconds: 0, endSeconds: 4.9 }, 4)).toBe("Shot 1 · first · 0:00–0:05");
    expect(shotLabel({ index: 1, startSeconds: 5, endSeconds: 9 }, 4)).toBe("Shot 2 · 0:05–0:09");
  });

  it("the last tile is 'last' even when an earlier moment was skipped", () => {
    // Tiles are moments 0, 1, 3: the third tile is moment 3, and it is last.
    expect(shotLabel({ index: 3, startSeconds: 10, endSeconds: 19.8 }, 3, 2)).toBe("Shot 4 · last · 0:10–0:20");
  });
});

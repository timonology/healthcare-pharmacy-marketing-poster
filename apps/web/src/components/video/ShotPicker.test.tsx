import { findAll, newContainer, reactProps, type FakeNode } from "@/test/dom";
// `act` comes from "react" in React 19; react-dom/test-utils no longer has it.
import { act, createElement, useState } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { VideoShot } from "@acme/shared-types";
import { api } from "@/lib/api";
import { ShotPicker } from "./ShotPicker";

/**
 * The picker that replaced guessing: "remove the last shot which shows
 * medicine" was refused because the last shot's stock title said "employees on
 * a couch". A tapped tile is the shot, whatever it is said to show.
 */

const SHOTS: VideoShot[] = [
  { index: 0, startSeconds: 0, endSeconds: 5, caption: "Extra weight strains the heart.", isPhoto: true, hasThumbnail: true, shows: null },
  { index: 1, startSeconds: 5, endSeconds: 10, caption: "Small changes add up.", isPhoto: false, hasThumbnail: false, shows: "a salad" },
  { index: 2, startSeconds: 10, endSeconds: 15, caption: "Ask about a weight check.", isPhoto: false, hasThumbnail: true, shows: "a scale" },
  { index: 3, startSeconds: 15, endSeconds: 19.8, caption: "Book today.", isPhoto: false, hasThumbnail: true, shows: "employees on a couch" },
];

let root: Root;
let container: FakeNode;
let selected: Map<number, string>;
const realList = api.listVideoShots;

let version: string | undefined;

function Harness() {
  const [value, setValue] = useState<Map<number, string>>(new Map());
  selected = value;
  return createElement(ShotPicker, { videoId: "v1", version, selected: value, onChange: setValue });
}

async function mount(shots: VideoShot[] = SHOTS, v?: string) {
  version = v;
  (api as { listVideoShots: typeof api.listVideoShots }).listVideoShots = async () => shots;
  await act(async () => { root.render(createElement(Harness)); });
}

const tiles = () => findAll(container, (n) => n.nodeName === "BUTTON");
const tap = (i: number) => act(() => { reactProps(tiles()[i]).onClick(); });
const text = () => findAll(container, (n) => n.nodeName === "#text").map((n) => n.textContent).join("|");

beforeEach(() => {
  container = newContainer();
  root = createRoot(container as unknown as Element);
});

afterEach(() => {
  act(() => root.unmount());
  (api as { listVideoShots: typeof api.listVideoShots }).listVideoShots = realList;
});

describe("every moment is a tile", () => {
  it("shows one tile per shot, named by number, position and time", async () => {
    await mount();

    expect(tiles()).toHaveLength(4);
    expect(tiles()[3].attributes["aria-label"]).toContain("Shot 4 · last · 0:15–0:20");
  });

  it("uses the rendered still when there is one, and a number when not", async () => {
    await mount();

    const stills = findAll(container, (n) => n.nodeName === "IMG").map((n) => n.attributes.src);
    expect(stills).toContain("/api/videos/v1/shots/3/thumbnail");
    expect(stills).not.toContain("/api/videos/v1/shots/1/thumbnail");
  });

  it("labels the pharmacy's own photo", async () => {
    await mount();

    expect(tiles()[0].attributes["aria-label"]).toContain("your photo");
  });

  it("says so when the video has no shots on record", async () => {
    await mount([]);

    expect(text()).toContain("not on record");
  });
});

describe("the shot is the one tapped", () => {
  it("tapping the last tile chooses the last shot, whatever it is said to show", async () => {
    await mount();

    tap(3);

    expect([...selected.keys()]).toEqual([3]);
  });

  it("a tapped shot asks what it should show; tapping again unselects", async () => {
    await mount();
    tap(3);
    const input = findAll(container, (n) => n.nodeName === "INPUT")[0];

    act(() => { reactProps(input).onChange({ target: { value: "a person on a scale" } }); });
    expect(selected.get(3)).toBe("a person on a scale");

    tap(3);
    expect(selected.size).toBe(0);
  });

  it("stops at six shots in one change", async () => {
    const many = Array.from({ length: 8 }, (_, i) => ({ ...SHOTS[1], index: i }));
    await mount(many);

    for (let i = 0; i < 8; i++) tap(i);

    expect(selected.size).toBe(6);
  });
});

describe("tiles stay truthful after a re-cut", () => {
  it("a re-cut video's stills are fetched afresh, not from the browser cache", async () => {
    await mount(SHOTS, "2026-09-25T10:00:00Z");

    const stills = findAll(container, (n) => n.nodeName === "IMG").map((n) => n.attributes.src);
    expect(stills).toContain("/api/videos/v1/shots/3/thumbnail?v=2026-09-25T10%3A00%3A00Z");
  });

  it("the last tile is labelled last when an earlier moment was skipped", async () => {
    await mount([SHOTS[0], SHOTS[1], SHOTS[3]]);

    expect(tiles()[2].attributes["aria-label"]).toContain("Shot 4 · last");
  });
});

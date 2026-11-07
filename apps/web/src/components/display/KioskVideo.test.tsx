import { findAll, newContainer, reactProps, type FakeNode, type FakeVideo } from "@/test/dom";
// `act` comes from "react" in React 19; react-dom/test-utils no longer has it.
import { act, StrictMode, createElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { KioskSlide } from "@acme/shared-types";
import { KioskVideo } from "./KioskVideo";

/**
 * The screen showed every poster and never the video: in development React
 * runs each effect twice, the teardown removed the source, and the re-run's
 * play() on an empty element was counted as a failure — so the video was
 * skipped for five minutes, silently. These run it the way React does.
 */

const SLIDE: KioskSlide = {
  kind: "Video",
  imageUrl: "",
  name: "Flu jabs",
  durationSeconds: 20,
  mediaUrl: "/api/display/video/v1?sig=s&v=1",
};

let root: Root;
let container: FakeNode;
let ended: number;
let failed: number;

beforeEach(() => {
  container = newContainer();
  root = createRoot(container as unknown as Element);
  ended = 0;
  failed = 0;
});

afterEach(() => {
  act(() => root.unmount());
});

async function mount(strict: boolean) {
  const video = createElement(KioskVideo, {
    slide: SLIDE,
    drift: 0,
    onEnded: () => { ended++; },
    onFailed: () => { failed++; },
  });
  await act(async () => {
    root.render(strict ? createElement(StrictMode, null, video) : video);
  });
  // Let any rejected play() promise settle.
  await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
  return findAll(container, (n) => n.nodeName === "VIDEO")[0] as FakeVideo;
}

describe("KioskVideo", () => {
  for (const strict of [true, false]) {
    describe(strict ? "under Strict Mode (effects run twice)" : "mounted once", () => {
      it("has its source and is playing, muted — not written off as failed", async () => {
        const el = await mount(strict);

        expect(el.getAttribute("src")).toBe(SLIDE.mediaUrl);
        expect(el.muted).toBe(true);
        expect(el.paused).toBe(false);
        expect(failed).toBe(0);
      });

      it("moves on exactly once when the video ends", async () => {
        const el = await mount(strict);
        el.startPlaying();

        await act(async () => {
          reactProps(el).onEnded!();
          reactProps(el).onEnded!();
        });

        expect(ended).toBe(1);
        expect(failed).toBe(0);
      });
    });
  }

  it("counts a real playback error as a failure", async () => {
    const el = await mount(true);

    const warnings: string[] = [];
    const realWarn = console.warn;
    console.warn = (m: string) => { warnings.push(m); };
    try {
      await act(async () => {
        reactProps(el).onError!({ currentTarget: el });
      });
    } finally {
      console.warn = realWarn;
    }

    expect(failed).toBe(1);
    expect(warnings.join("\n")).toMatch(/skipping video "Flu jabs".*would not load/);
  });

  it("fails at once, and says why, when there is nothing to play", async () => {
    const warnings: string[] = [];
    const realWarn = console.warn;
    console.warn = (m: string) => { warnings.push(m); };
    try {
      await act(async () => {
        root.render(createElement(KioskVideo, {
          slide: { ...SLIDE, mediaUrl: null },
          drift: 0,
          onEnded: () => { ended++; },
          onFailed: () => { failed++; },
        }));
      });
    } finally {
      console.warn = realWarn;
    }

    expect(failed).toBe(1);
    expect(warnings.join("\n")).toContain("Flu jabs");
  });

  it("releases the stream when it leaves the screen", async () => {
    const el = await mount(false);
    await act(async () => root.render(createElement("div")));

    expect(el.getAttribute("src")).toBeNull();
    expect(failed).toBe(0);
  });
});

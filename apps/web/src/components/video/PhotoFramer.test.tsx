import { findAll, imageSizes, newContainer, reactProps, type FakeNode } from "@/test/dom";
// `act` comes from "react" in React 19; react-dom/test-utils no longer has it.
import { act, createElement, useState } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { VIDEO_FRAME, framingGeometry } from "@acme/shared-types";
import { PhotoFramer } from "./PhotoFramer";

/**
 * Regression tests for the photo framer. Each `describe` is a bug that reached
 * a pharmacist or was caught in review; each test fails if it comes back.
 */

imageSizes.set("blob:landscape", [4032, 3024]);
imageSizes.set("blob:tall", [1200, 2400]);

interface Framing {
  src: string;
  fx: number;
  fy: number;
  z: number;
}

let root: Root;
let container: FakeNode;
let state: Framing;
let setState: (update: Framing | ((s: Framing) => Framing)) => void;
let renders = 0;
let errors: string[] = [];
const originalError = console.error;

function Harness({ initial }: { initial: Framing }) {
  renders++;
  const [framing, set] = useState(initial);
  state = framing;
  setState = set;
  return createElement(PhotoFramer, {
    src: framing.src,
    alt: "shop",
    focalX: framing.fx,
    focalY: framing.fy,
    zoom: framing.z,
    onChange: (fx: number, fy: number, z: number) => set((s) => ({ ...s, fx, fy, z })),
  });
}

function mount(initial: Partial<Framing> = {}) {
  act(() => {
    root.render(createElement(Harness, {
      initial: { src: "blob:landscape", fx: 0.5, fy: 0.5, z: 1, ...initial },
    }));
  });
}

const update = (next: Partial<Framing>) => act(() => setState((s) => ({ ...s, ...next })));
const sliders = () =>
  findAll(container, (n) => n.nodeName === "INPUT").map((n) => n.attributes["aria-label"]);
const images = () => findAll(container, (n) => n.nodeName === "IMG");
const frame = () => findAll(container, (n) => n.attributes.role === "group")[0];
const text = () =>
  findAll(container, (n) => n.nodeName === "#text").map((n) => n.textContent).join("|");

function pointer(id: number, x: number, y: number, pointerType = "mouse") {
  return { currentTarget: frame(), pointerType, button: 0, pointerId: id, clientX: x, clientY: y };
}

function wheel(deltaY: number, ctrlKey = true) {
  const handlers = frame().listeners.wheel ?? [];
  let prevented = 0;
  act(() => {
    for (const h of handlers) {
      h({ ctrlKey, metaKey: false, deltaY, deltaMode: 0, preventDefault: () => prevented++ });
    }
  });
  return prevented;
}

beforeEach(() => {
  renders = 0;
  errors = [];
  console.error = (...args: unknown[]) => errors.push(String(args[0]).slice(0, 200));
  container = newContainer();
  root = createRoot(container as unknown as Element);
});

afterEach(() => {
  act(() => root.unmount());
  console.error = originalError;
});

describe("attaching a photo does not crash the dialog", () => {
  // "Maximum update depth exceeded" the moment a photo was attached: a ref
  // callback recreated every render measured an already-loaded blob image and
  // set a fresh state object each time — render, ref, set, render, forever.
  it("mounts an already-decoded photo and settles", () => {
    mount();

    expect(errors).toEqual([]);
    expect(renders).toBeLessThan(5);
  });
});

describe("only an axis with spare picture can move", () => {
  it("a landscape photo offers Zoom and Across, never Down", () => {
    mount();

    expect(sliders()).toEqual(["Zoom for shop", "Horizontal framing for shop"]);
  });

  it("a vertical drag on a landscape photo changes nothing", () => {
    mount();

    act(() => reactProps(frame()).onPointerDown(pointer(1, 50, 50)));
    act(() => reactProps(frame()).onPointerMove(pointer(1, 50, 120)));

    expect(state.fy).toBe(0.5);
  });

  it("a tall photo offers Down; zoomed out to fit, it offers nothing to move", () => {
    mount({ src: "blob:tall" });
    expect(sliders()).toEqual(["Zoom for shop", "Vertical framing for shop"]);

    update({ z: 0.5 });

    expect(sliders()).toEqual(["Zoom for shop"]);
    expect(text()).toContain("All of it fits.");
  });

  it("a different photo in the same slot is measured afresh", () => {
    mount();
    update({ src: "blob:tall" });

    expect(sliders()).toContain("Vertical framing for shop");
  });
});

describe("the preview is the render", () => {
  it("at 100% it is the original single cover image", () => {
    mount();

    expect(images()).toHaveLength(1);
  });

  it("zoomed out, the photo sits exactly where the worker's geometry puts it, over a blurred copy", () => {
    mount();
    update({ z: 0.6 });

    const g = framingGeometry(4032, 3024, VIDEO_FRAME.width, VIDEO_FRAME.height, 0.5, 0.5, 0.6);
    const [background, photo] = images();
    const pct = (v: number, d: number) => `${(v / d) * 100}%`;

    expect(background.style.filter).toBe("blur(2.1cqw)");
    expect(photo.style).toMatchObject({
      left: pct(g.x, VIDEO_FRAME.width),
      top: pct(g.y, VIDEO_FRAME.height),
      width: pct(g.width, VIDEO_FRAME.width),
      height: pct(g.height, VIDEO_FRAME.height),
    });
  });
});

describe("gestures", () => {
  it("the picture follows the pointer one-to-one", () => {
    mount();
    const slack = framingGeometry(4032, 3024, 1080, 1920, 0.5, 0.5, 1).width - 1080;

    act(() => reactProps(frame()).onPointerDown(pointer(1, 50, 50)));
    act(() => reactProps(frame()).onPointerMove(pointer(1, 60, 50)));

    // 10px right on a 108px-wide frame is 100 frame pixels.
    expect(state.fx).toBeCloseTo(0.5 - 100 / slack, 9);
  });

  it("releasing the pointer ends the drag", () => {
    mount();
    act(() => reactProps(frame()).onPointerDown(pointer(1, 50, 50)));
    act(() => reactProps(frame()).onPointerUp(pointer(1, 50, 50)));
    const after = state.fx;

    act(() => reactProps(frame()).onPointerMove(pointer(1, 90, 50)));

    expect(state.fx).toBe(after);
  });

  it("releasing a pointer that never pressed does not throw", () => {
    // Releasing a capture that was never taken throws in a browser; the fake
    // DOM does the same.
    mount();

    expect(() => act(() => reactProps(frame()).onPointerUp(pointer(7, 0, 0)))).not.toThrow();
  });

  it("two fingers pinch the zoom, and lifting one does not jump the picture", () => {
    mount();
    act(() => reactProps(frame()).onPointerDown(pointer(1, 40, 100, "touch")));
    act(() => reactProps(frame()).onPointerDown(pointer(2, 60, 100, "touch")));
    act(() => reactProps(frame()).onPointerMove(pointer(2, 50, 100, "touch")));
    expect(state.z).toBeCloseTo(0.5, 9);

    const fx = state.fx;
    act(() => reactProps(frame()).onPointerUp(pointer(2, 50, 100, "touch")));
    act(() => reactProps(frame()).onPointerMove(pointer(1, 40, 100, "touch")));

    expect(state.fx).toBe(fx);
  });
});

describe("trackpad pinch and Ctrl+scroll", () => {
  it("a plain scroll is left to scroll the dialog", () => {
    mount();

    const prevented = wheel(50, false);

    expect(prevented).toBe(0);
    expect(state.z).toBe(1);
  });

  it("many tiny pinch steps from exactly 100% add up instead of snapping back", () => {
    // Each 1px step alone is inside the 0.5% snap around 1.0 and rounded
    // straight back — the pinch could never leave 100%.
    mount();

    for (let i = 0; i < 10; i++) wheel(1);

    expect(state.z).toBeGreaterThan(0.94);
    expect(state.z).toBeLessThan(0.96);
  });

  it("resumes from a zoom set by the slider, and stops at 150%", () => {
    mount();
    update({ z: 1.2 });
    wheel(-20);
    expect(state.z).toBeCloseTo(1.2 * Math.exp(0.1), 9);

    for (let i = 0; i < 50; i++) wheel(-200);

    expect(state.z).toBe(1.5);
  });
});

describe("controls fit a thumbnail a quarter of the dialog wide", () => {
  // Label, slider and readout side by side left the Zoom slider ~18px wide.
  it("each slider sits full width under its label", () => {
    mount();

    for (const input of findAll(container, (n) => n.nodeName === "INPUT")) {
      expect(input.attributes.class).toContain("w-full");
      expect(input.parentNode?.attributes.class).toContain("block");
      expect(input.parentNode?.attributes.class).not.toContain("flex");
    }
  });

  it("Reset appears once framing changes, names its photo, and puts it back", () => {
    mount();
    expect(findAll(container, (n) => n.nodeName === "BUTTON")).toHaveLength(0);

    update({ z: 0.7, fx: 0.2 });
    const [reset] = findAll(container, (n) => n.nodeName === "BUTTON");
    expect(reset.attributes["aria-label"]).toBe("Reset framing for shop");

    act(() => reactProps(reset).onClick());

    expect(state).toMatchObject({ fx: 0.5, fy: 0.5, z: 1 });
  });
});

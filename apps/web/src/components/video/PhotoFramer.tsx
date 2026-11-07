"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import {
  VIDEO_FRAME,
  VIDEO_IMAGE_ZOOM_DEFAULT,
  VIDEO_IMAGE_ZOOM_MAX,
  VIDEO_IMAGE_ZOOM_MIN,
  clampZoom,
  framingGeometry,
  isUntouchedZoom,
  type FramingGeometry,
} from "@acme/shared-types";

/**
 * Frame a photograph for the 9:16 video: pan it, zoom out to show more of it,
 * or punch in.
 *
 * <b>The preview is the render, not an impression of it.</b> Every position
 * here comes from `framingGeometry`, the same function the worker runs in
 * `framing.py`. Both are held to one golden table by `check-framing.mjs` and
 * `test_framing.py`, and the worker's output is checked against that geometry
 * in real ffmpeg pixels. What somebody frames here is what comes out.
 *
 * <b>Zoom 1.0 keeps the old drawing path.</b> At 1.0 the preview uses
 * `object-fit: cover` with `object-position`, which was proved identical to the
 * worker's original crop filter, and the worker keeps that filter byte for
 * byte. Every photo nobody zooms therefore previews and renders exactly as it
 * did before zoom existed.
 *
 * <b>Below 1.0 the gaps are a blurred copy of the photo</b>, as in the render.
 * No black bars, no stretching.
 *
 * <b>Only an axis with slack moves.</b> Whether the photo can pan across or
 * down depends on the zoom as well as its shape, so it is worked out from the
 * geometry at the current zoom. A drag along an axis with nothing to spare does
 * nothing, and that axis has no slider.
 */
export function PhotoFramer({
  src,
  alt,
  focalX,
  focalY,
  zoom = VIDEO_IMAGE_ZOOM_DEFAULT,
  onChange,
  className,
}: {
  src: string;
  alt: string;
  focalX: number;
  focalY: number;
  zoom?: number;
  onChange: (focalX: number, focalY: number, zoom: number) => void;
  className?: string;
}) {
  const frame = useRef<HTMLDivElement>(null);

  /**
   * The photo's own size, once the browser has decoded it.
   *
   * `naturalWidth/Height` are after EXIF rotation, which is what the worker
   * now renders from too (`_upright` in ai_imagery.py). Before that fix, the
   * two sides measured a phone portrait on different axes.
   *
   * <b>Hands back the previous state when nothing changed.</b> A fresh object
   * every call, combined with a ref callback that fires on commit, was the
   * "Maximum update depth exceeded" loop the first time a photo was attached.
   */
  const [measured, setMeasured] = useState<{ src: string; w: number; h: number } | null>(null);

  const measure = useCallback((image: HTMLImageElement) => {
    const { naturalWidth: w, naturalHeight: h } = image;
    // The attribute, not `.src`: the property is resolved to an absolute URL
    // and would never equal the prop it is compared against below.
    const from = image.getAttribute("src") ?? "";
    if (!w || !h) return;
    setMeasured((prev) =>
      prev && prev.src === from && prev.w === w && prev.h === h ? prev : { src: from, w, h },
    );
  }, []);

  /**
   * Catches an image that finished loading before React attached `onLoad`,
   * which is every local blob URL. Stable on purpose: an inline ref callback is
   * a new function each render, so React would call it on every commit.
   */
  const measureIfLoaded = useCallback(
    (el: HTMLImageElement | null) => {
      if (el?.complete) measure(el);
    },
    [measure],
  );

  // Keyed by source rather than reset in an effect. An effect runs *after* the
  // ref callback has measured an already-loaded image, so resetting there
  // would throw away the only measurement that picture will ever get. A
  // different picture in the same slot simply does not match.
  const natural = measured && measured.src === src ? measured : null;

  const z = clampZoom(zoom);
  const geometry = natural
    ? framingGeometry(natural.w, natural.h, VIDEO_FRAME.width, VIDEO_FRAME.height, focalX, focalY, z)
    : null;

  // A sliver of tolerance so a photo that is 9:16 to within rounding does not
  // offer a pan that moves it by a pixel or two.
  const pansAcross = !!geometry && geometry.width - VIDEO_FRAME.width > VIDEO_FRAME.width * 0.005;
  const pansDown = !!geometry && geometry.height - VIDEO_FRAME.height > VIDEO_FRAME.height * 0.005;

  // The native listeners below outlive any single render, so they read the
  // current values through a ref rather than closing over stale ones.
  const live = useRef({ focalX, focalY, z, geometry, pansAcross, pansDown, onChange });
  live.current = { focalX, focalY, z, geometry, pansAcross, pansDown, onChange };

  // ── pointer: one finger pans, two fingers pinch ──────────────────────
  const pointers = useRef(new Map<number, { x: number; y: number }>());
  const gesture = useRef<
    | { kind: "pan"; x: number; y: number; fx: number; fy: number }
    | { kind: "pinch"; distance: number; zoom: number }
    | null
  >(null);
  const [dragging, setDragging] = useState(false);

  /** Starts whichever gesture the current number of fingers means. */
  function begin() {
    const points = [...pointers.current.values()];
    const { focalX: fx, focalY: fy, z: current } = live.current;
    if (points.length >= 2) {
      gesture.current = { kind: "pinch", distance: spread(points), zoom: current };
    } else if (points.length === 1) {
      // Taken from wherever the finger is *now*. Lifting one finger of a pinch
      // must not make the picture jump to the remaining one.
      gesture.current = { kind: "pan", x: points[0].x, y: points[0].y, fx, fy };
    } else {
      gesture.current = null;
    }
    setDragging(gesture.current !== null);
  }

  function follow() {
    const g = gesture.current;
    const box = frame.current?.getBoundingClientRect();
    const state = live.current;
    if (!g || !box || !box.width || !box.height) return;
    const points = [...pointers.current.values()];

    if (g.kind === "pinch" && points.length >= 2 && g.distance > 0) {
      state.onChange(state.focalX, state.focalY, clampZoom(g.zoom * (spread(points) / g.distance)));
      return;
    }

    if (g.kind === "pan" && points.length === 1 && state.geometry) {
      // The picture follows the finger one-to-one. The pointer's movement in
      // screen pixels becomes frame pixels, and a focal step of 1.0 is the
      // whole of the spare width — so dividing by the slack gives the step.
      const { width, height } = state.geometry;
      const dx = ((points[0].x - g.x) * VIDEO_FRAME.width) / box.width;
      const dy = ((points[0].y - g.y) * VIDEO_FRAME.height) / box.height;
      const nx = state.pansAcross ? clamp01(g.fx - dx / (width - VIDEO_FRAME.width)) : state.focalX;
      const ny = state.pansDown ? clamp01(g.fy - dy / (height - VIDEO_FRAME.height)) : state.focalY;
      if (nx !== state.focalX || ny !== state.focalY) state.onChange(nx, ny, state.z);
    }
  }

  // ── wheel: ctrl/⌘ + scroll, and trackpad pinch ───────────────────────
  //
  // Native and non-passive, because React's `onWheel` is passive and cannot
  // stop the page zooming. Only a modified wheel is taken — trackpad pinch
  // arrives as ctrl+wheel in every desktop browser — so an ordinary scroll
  // over the photo still scrolls the dialog rather than being swallowed.
  //
  // The unsnapped value is carried between events. A trackpad pinch arrives as
  // many tiny steps, and each one taken alone from exactly 1.0 lands inside
  // the snap and rounds straight back to 1.0 — the pinch would never leave
  // 100%. Accumulating lets the steps add up; the snap applies only to what is
  // reported.
  const wheelZoom = useRef<number | null>(null);
  useEffect(() => {
    const el = frame.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      const { focalX: fx, focalY: fy, z: current, onChange: change } = live.current;
      // Resume from the carried value only if nothing else (slider, Reset)
      // has moved the zoom since.
      const base =
        wheelZoom.current !== null && clampZoom(wheelZoom.current) === current
          ? wheelZoom.current
          : current;
      // Exponential, so each notch is the same proportion at any zoom. Line-
      // mode wheels report in lines, not pixels.
      const pixels = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
      const raw = Math.min(
        VIDEO_IMAGE_ZOOM_MAX,
        Math.max(VIDEO_IMAGE_ZOOM_MIN, base * Math.exp(-pixels * 0.005)),
      );
      wheelZoom.current = raw;
      const next = clampZoom(raw);
      if (next !== current) change(fx, fy, next);
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, []);

  function end(el: HTMLDivElement, pointerId: number) {
    if (!pointers.current.delete(pointerId)) return;
    // Only release a capture that was taken, which throws otherwise.
    if (el.hasPointerCapture(pointerId)) el.releasePointerCapture(pointerId);
    begin();
  }

  const zoomPercent = Math.round(z * 100);
  const untouched = isUntouchedZoom(z) && focalX === 0.5 && focalY === 0.5;

  return (
    <div className={className}>
      <div
        ref={frame}
        role="group"
        aria-label={`Framing for ${alt}`}
        className={[
          // Touch: claim only what the photo uses. A photo that pans down needs
          // vertical swipes, so it takes all of them; otherwise a vertical
          // swipe still scrolls the dialog — on a phone these thumbnails cover
          // most of it, and a dialog you cannot scroll by touching its content
          // reads as frozen. Horizontal drags and pinches come here either way.
          // `containerType` below lets the blur be sized to the frame, the
          // same proportion the render uses.
          "relative aspect-[9/16] w-full select-none overflow-hidden rounded-md border bg-muted",
          pansDown ? "touch-none" : "touch-pan-y",
          pansAcross || pansDown ? (dragging ? "cursor-grabbing" : "cursor-grab") : "cursor-default",
        ].join(" ")}
        style={{ containerType: "inline-size" }}
        onPointerDown={(e) => {
          if (e.pointerType === "mouse" && e.button !== 0) return;
          e.currentTarget.setPointerCapture(e.pointerId);
          pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
          // Starting a gesture moves nothing. Mapping the press straight to a
          // position makes the picture jump the instant it is touched.
          begin();
        }}
        onPointerMove={(e) => {
          if (!pointers.current.has(e.pointerId)) return;
          pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
          follow();
        }}
        onPointerUp={(e) => end(e.currentTarget, e.pointerId)}
        onPointerCancel={(e) => end(e.currentTarget, e.pointerId)}
      >
        {geometry && !isUntouchedZoom(z) ? (
          <ZoomedPreview
            src={src}
            alt={alt}
            geometry={geometry}
            imageRef={measureIfLoaded}
            onLoad={measure}
          />
        ) : (
          // eslint-disable-next-line @next/next/no-img-element
          <img
            // Keyed so a different picture is a new element: the ref then
            // measures it even when it was already decoded and no load fires.
            key={src}
            src={src}
            alt={alt}
            ref={measureIfLoaded}
            onLoad={(e) => measure(e.currentTarget)}
            draggable={false}
            className="h-full w-full object-cover"
            style={{ objectPosition: `${focalX * 100}% ${focalY * 100}%` }}
          />
        )}
      </div>

      {/* The sliders are the keyboard route, and the only one for a value the
          render depends on without a pointer. */}
      <div className="mt-2 space-y-1">
        <Slider
          label="Zoom"
          aria={`Zoom for ${alt}`}
          min={Math.round(VIDEO_IMAGE_ZOOM_MIN * 100)}
          max={Math.round(VIDEO_IMAGE_ZOOM_MAX * 100)}
          value={zoomPercent}
          suffix={`${zoomPercent}%`}
          onChange={(v) => onChange(focalX, focalY, clampZoom(v / 100))}
        />
        {pansAcross && (
          <Slider
            label="Across"
            aria={`Horizontal framing for ${alt}`}
            min={0}
            max={100}
            value={Math.round(focalX * 100)}
            onChange={(v) => onChange(v / 100, focalY, z)}
          />
        )}
        {pansDown && (
          <Slider
            label="Down"
            aria={`Vertical framing for ${alt}`}
            min={0}
            max={100}
            value={Math.round(focalY * 100)}
            onChange={(v) => onChange(focalX, v / 100, z)}
          />
        )}
        {/* Short on purpose: this sits under a thumbnail a quarter of the
            dialog wide. How to drag and pinch is said once, above the grid. */}
        {geometry && !pansAcross && !pansDown && (
          <p className="text-[11px] leading-tight text-muted-foreground">
            {z < 1 ? "All of it fits." : "Already fits."}
          </p>
        )}
        {!untouched && (
          <button
            type="button"
            className="text-[11px] text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
            onClick={() => onChange(0.5, 0.5, VIDEO_IMAGE_ZOOM_DEFAULT)}
            aria-label={`Reset framing for ${alt}`}
          >
            Reset
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * The zoomed drawing: a blurred, cover-cropped copy underneath and the photo on
 * top at exactly the geometry's rectangle — the render's `overlay`, as CSS.
 *
 * A top-level component on purpose. Declared inside `PhotoFramer` it would be a
 * new component type on every render, and React would unmount and remount both
 * images on every step of a drag.
 */
function ZoomedPreview({
  src,
  alt,
  geometry: g,
  imageRef,
  onLoad,
}: {
  src: string;
  alt: string;
  geometry: FramingGeometry;
  imageRef: (el: HTMLImageElement | null) => void;
  onLoad: (image: HTMLImageElement) => void;
}) {
  return (
    <>
      {/* The render blurs with boxblur at radius min(W,H)/40, two passes —
          roughly a Gaussian of 2.1% of the frame's width. `cqw` sizes the CSS
          blur to this frame, so the preview matches at any size. `scale`
          hides the soft edge a blur leaves at the border. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src={src}
        alt=""
        aria-hidden
        draggable={false}
        className="pointer-events-none absolute inset-0 h-full w-full scale-110 object-cover"
        style={{ filter: "blur(2.1cqw)" }}
      />
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        key={src}
        src={src}
        alt={alt}
        ref={imageRef}
        onLoad={(e) => onLoad(e.currentTarget)}
        draggable={false}
        className="pointer-events-none absolute max-w-none"
        style={{
          left: `${(g.x / VIDEO_FRAME.width) * 100}%`,
          top: `${(g.y / VIDEO_FRAME.height) * 100}%`,
          width: `${(g.width / VIDEO_FRAME.width) * 100}%`,
          height: `${(g.height / VIDEO_FRAME.height) * 100}%`,
        }}
      />
    </>
  );
}

/**
 * How to frame, said once above the thumbnails rather than under each one — a
 * sentence in a column a quarter of the dialog wide wraps to four lines, four
 * times over.
 */
export function FramingHint() {
  return (
    <p className="text-xs text-muted-foreground">
      Each thumbnail is exactly what the video shows. Drag to choose what&apos;s
      kept; zoom out to fit more in (slider, pinch, or Ctrl + scroll).
    </p>
  );
}

function Slider({
  label,
  aria,
  min,
  max,
  value,
  suffix,
  onChange,
}: {
  label: string;
  aria: string;
  min: number;
  max: number;
  value: number;
  suffix?: string;
  onChange: (value: number) => void;
}) {
  return (
    // Label above, slider full width below. Side by side, a label and a
    // readout left the Zoom slider ~18px wide in a 110px thumbnail column, and
    // nothing at all on a phone.
    <label className="block text-[11px] leading-tight text-muted-foreground">
      <span className="flex justify-between gap-1">
        <span>{label}</span>
        {suffix && <span className="tabular-nums">{suffix}</span>}
      </span>
      <input
        type="range"
        min={min}
        max={max}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        className="h-1 w-full cursor-pointer"
        aria-label={aria}
      />
    </label>
  );
}

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

function spread(points: { x: number; y: number }[]) {
  return Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y);
}

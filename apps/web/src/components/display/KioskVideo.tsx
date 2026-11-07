"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { KioskSlide } from "@acme/shared-types";
import { VIDEO_START_TIMEOUT_MS } from "@/lib/kiosk-playlist";

/**
 * One video on a screen, played once from the start, muted, then `onEnded`.
 *
 * <b>Letterboxed, never stretched or cropped.</b> `object-contain` on black:
 * a 9:16 video on a landscape panel is a column in the middle with black
 * either side, which is what the pharmacist saw when they made it.
 *
 * <b>Muted, always.</b> Browsers only autoplay muted video, and a screen in a
 * shop is not somewhere to start playing sound unasked.
 *
 * The source is fixed when the turn starts. A poll that re-signs the link
 * mid-play would otherwise reload the element and restart the video; the link
 * it started with stays valid for hours.
 *
 * <b>The effect owns the source, and must survive running twice.</b> React
 * mounts, tears down and re-mounts every effect in development (Strict Mode),
 * and may do the same in production for a component it preserves. The first
 * version set `src` in JSX and removed it in the teardown; the re-mount then
 * called `play()` on an element with no source, the rejection counted as a
 * failure, and the screen skipped every video for five minutes — posters only,
 * no error anywhere. So the effect sets the source itself, and a rejection
 * from a play() that the teardown cancelled (AbortError, or a run that is no
 * longer current) is not a failure.
 */
export function KioskVideo({
  slide,
  drift,
  onEnded,
  onFailed,
}: {
  slide: KioskSlide;
  drift: number;
  onEnded: () => void;
  onFailed: () => void;
}) {
  const ref = useRef<HTMLVideoElement>(null);
  const [src] = useState(() => slide.mediaUrl ?? "");
  const done = useRef(false);

  // The latest callbacks, read at the moment the turn ends. Held in a ref so a
  // parent re-render (every poll) cannot re-run the effect below — which would
  // tear the stream down and restart the video.
  const callbacks = useRef({ onEnded, onFailed });
  callbacks.current = { onEnded, onFailed };

  // Each turn ends exactly once, however many events fire.
  //
  // A skip is logged, with why. The first version skipped silently, and a
  // screen showing posters but never its video gave nobody — pharmacist,
  // installer or developer — anything to go on. One line in the console of
  // the kiosk tab turns that into a two-minute diagnosis.
  const finish = useCallback((failed: boolean, reason?: string) => {
    if (done.current) return;
    done.current = true;
    if (failed) {
      console.warn(`[display] skipping video "${slide.name}" for now: ${reason ?? "it did not play"}`);
      callbacks.current.onFailed();
    } else {
      callbacks.current.onEnded();
    }
  }, [slide.name]);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (!src) {
      finish(true, "no media link");
      return;
    }
    let active = true;

    // Set as a property as well as the attribute: autoplay policy reads the
    // property, and React has not always reflected `muted` to the element.
    el.muted = true;
    el.src = src;

    const started = el.play();
    started?.catch((err: unknown) => {
      // Cancelled by our own teardown (or a newer run) — not the video's fault.
      // A real AbortError while current (a browser pausing background media
      // to save power) is left to the start timer below.
      const name = (err as { name?: string } | null)?.name;
      if (active && name !== "AbortError") {
        finish(true, name === "NotAllowedError"
          ? "the browser refused to autoplay it"
          : `play() failed (${name ?? "unknown"})`);
      }
    });

    // A stream that never starts (captive portal, a proxy holding the
    // request) fires no error. Give it a fair chance, then move on.
    const startTimer = setTimeout(() => {
      if (active && el.currentTime === 0) {
        finish(true, `it had not started after ${VIDEO_START_TIMEOUT_MS / 1000}s`);
      }
    }, VIDEO_START_TIMEOUT_MS);

    return () => {
      active = false;
      clearTimeout(startTimer);
      // Release the network stream and decoder now, not at garbage collection.
      el.pause();
      el.removeAttribute("src");
      el.load();
    };
  }, [finish, src]);

  return (
    <video
      ref={ref}
      muted
      autoPlay
      playsInline
      preload="auto"
      disablePictureInPicture
      aria-label={slide.name}
      onEnded={() => finish(false)}
      // An error event from an element with no source is our own teardown.
      onError={(e) => {
        const el = e.currentTarget;
        if (el.getAttribute("src")) {
          // MediaError code 2 is a network failure, 4 an unplayable response —
          // usually a 403/404 from the link. The Network tab has the status.
          finish(true, `the video would not load (media error ${el.error?.code ?? "?"})`);
        }
      }}
      className="absolute inset-0 h-full w-full bg-black object-contain"
      style={{ transform: `translate(${drift}px, ${drift}px)` }}
    />
  );
}

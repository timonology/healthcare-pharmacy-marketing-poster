"use client";

import { useEffect, useRef, useState } from "react";
import type { CanvasDocument } from "@acme/shared-types";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import type { TokenValues } from "@/lib/canvas/display-tokens";
import { cn } from "@/lib/utils";

interface PosterCardImageProps {
  /** `/api/posters/{id}/card?v=…` from the list API. */
  src: string | null | undefined;
  alt: string;
  /** Drawn in the browser only if the rendered card cannot be had. */
  canvas: CanvasDocument;
  tokenValues?: TokenValues | null;
  className?: string;
}

/**
 * A poster card on My Posters, as the PDF renderer draws it.
 *
 * The image is the server's render — the same code as the PDF and PNG, so
 * waves and diagonals are filled, circle photos keep their transparent
 * corners, a missing photo costs only its own slot, and a pharmacy without a
 * booking link gets no QR rather than a grey box.
 *
 * <b>It can never show a broken image.</b> While loading it is a quiet
 * placeholder; if the render fails (a canvas the renderer refuses, the API
 * down) the card falls back to the in-browser preview of the same canvas. The
 * old card linked straight to storage and showed the browser's "?" instead.
 */
export function PosterCardImage({ src, alt, canvas, tokenValues, className }: PosterCardImageProps) {
  const [state, setState] = useState<"loading" | "loaded" | "failed">(src ? "loading" : "failed");
  const img = useRef<HTMLImageElement>(null);

  // A new URL (the poster was saved) starts over.
  useEffect(() => setState(src ? "loading" : "failed"), [src]);

  // An image served from the browser cache can finish before React attaches
  // onLoad, and then the event never arrives. Check once after mount.
  //
  // Only ever promotes to "loaded". `complete` is also true for a lazy image
  // the browser has not started fetching yet (it is below the fold), with a
  // natural width of 0 — reading that as a failure would swap every card
  // below the fold for the fallback before it had a chance to load. Real
  // failures arrive through onError.
  useEffect(() => {
    const el = img.current;
    if (el?.complete && el.naturalWidth > 0) setState("loaded");
  }, [src]);

  return (
    <div className={cn("relative aspect-[1/1.414] w-full overflow-hidden bg-muted", className)}>
      {state === "failed" ? (
        <CanvasPreview doc={canvas} className="h-full w-full" tokenValues={tokenValues} />
      ) : (
        <>
          {state === "loading" && (
            <div aria-hidden className="absolute inset-0 animate-pulse bg-muted" />
          )}
          {/* eslint-disable-next-line @next/next/no-img-element -- an API-rendered image, not a static asset */}
          <img
            ref={img}
            src={src!}
            alt={alt}
            loading="lazy"
            decoding="async"
            onLoad={(e) => setState(e.currentTarget.naturalWidth > 0 ? "loaded" : "failed")}
            onError={() => setState("failed")}
            className={cn(
              "h-full w-full object-cover transition-opacity duration-200",
              state === "loaded" ? "opacity-100" : "opacity-0",
            )}
          />
        </>
      )}
    </div>
  );
}

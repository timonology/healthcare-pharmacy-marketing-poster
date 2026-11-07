import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { HERO_PREVIEW } from "./house-previews.generated";

/**
 * The hero poster: a real house template, drawn the way the gallery draws it.
 *
 * Not a mock-up. It is "Trusted circle" from the built-in set, snapshotted by
 * scripts/marketing/build-house-previews.py — circle-cropped pharmacist on the
 * teal ring, ticked services, contact card and footer bar — so what a visitor
 * sees here is what they get when they pick it. The contact card shows the
 * same labels as a gallery card ("Your pharmacy", "Your phone"): on a real
 * poster those are the pharmacy's own details, from its brand kit.
 *
 * Static on purpose: the home page must render with the API down, and it must
 * not invent a pharmacy, a phone number or a booking code to look finished.
 */
export function HousePosterPreview() {
  return (
    <figure className="relative mx-auto w-full max-w-[420px]">
      <div
        aria-hidden
        className="absolute -inset-8 -z-10 rounded-[2rem] bg-gradient-to-br from-primary/20 via-cyan-400/10 to-teal-400/15 blur-3xl"
      />

      <div className="overflow-hidden rounded-xl border border-border/60 bg-white shadow-2xl ring-1 ring-black/5 dark:ring-white/10">
        <CanvasPreview
          doc={HERO_PREVIEW.canvas}
          className="aspect-[1240/1754] h-auto w-full"
        />
      </div>

      <figcaption className="mt-4 flex flex-wrap items-center justify-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
        <span className="font-medium text-foreground">{HERO_PREVIEW.name}</span>
        <span aria-hidden>·</span>
        <span>A4, from the built-in template set</span>
        <span aria-hidden>·</span>
        <span>your logo and details added for you</span>
      </figcaption>
    </figure>
  );
}

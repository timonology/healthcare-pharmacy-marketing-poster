import { cn } from "@/lib/utils";

/**
 * The Marketing25 mark: a pharmacy cross whose right arm is a play arrow —
 * health care that goes out and gets seen (posters, video, social) — on a
 * teal tile, with a small "spark" in the corner.
 *
 * One component for every place the logo appears, so the sidebar, headers,
 * sign-in pages and footer cannot drift apart again. The same drawing lives in
 * `app/icon.svg` (browser tab and home-screen icon) and
 * `public/brand/` (files for app stores and developer consoles); change all
 * three together.
 */
export const BRAND_TEAL = "#11798A";
export const BRAND_SPARK = "#7FE3D6";

/** The cross-and-arrow outline on a 64-unit grid, without the tile. */
export const MARK_PATH = "M26 14H34V22L49 32L34 42V50H26V36H14V28H26Z";

export function LogoMark({
  className,
  title,
}: {
  className?: string;
  /** Set when the mark stands alone; leave empty when the name is beside it. */
  title?: string;
}) {
  return (
    <svg
      viewBox="0 0 64 64"
      xmlns="http://www.w3.org/2000/svg"
      className={cn("h-8 w-8 shrink-0", className)}
      role={title ? "img" : undefined}
      aria-hidden={title ? undefined : true}
      aria-label={title}
      focusable="false"
    >
      <rect width="64" height="64" rx="16" fill={BRAND_TEAL} />
      <path d={MARK_PATH} fill="#FFFFFF" stroke="#FFFFFF" strokeWidth="4" strokeLinejoin="round" />
      <circle cx="51" cy="13" r="4" fill={BRAND_SPARK} />
    </svg>
  );
}

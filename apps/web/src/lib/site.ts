/**
 * Who we are, in one place, for every page's metadata, the sitemap, robots
 * and structured data.
 *
 * <b>The public address is read at request time.</b> The root layout reads
 * cookies, so every page renders per request and `SITE_URL` can be set on the
 * running container (Azure sets it from the web hostname) rather than baked
 * into the build. Without it, canonical links and social previews would point
 * at whatever host the build happened to run on.
 */
export const SITE_NAME = "Marketing25";

export const SITE_TAGLINE = "Professional pharmacy marketing";

/** One sentence: what it is, for whom, and the four things it makes. */
export const SITE_DESCRIPTION =
  "Marketing for UK community pharmacies: print-ready posters, AI poster design, " +
  "short marketing videos, patient email campaigns, social posts and in-pharmacy " +
  "screens, with your brand kit applied. No design skills needed.";

export const SITE_KEYWORDS = [
  "pharmacy marketing",
  "pharmacy posters",
  "community pharmacy",
  "pharmacy poster templates",
  "AI poster design",
  "pharmacy marketing videos",
  "pharmacy email campaigns",
  "pharmacy digital signage",
  "Pharmacy First posters",
  "flu vaccination posters",
  "UK pharmacy",
];

const FALLBACK_URL = "https://marketing25.co.uk";

/** The public origin, without a trailing slash. */
export function siteUrl(): string {
  const raw = process.env.SITE_URL || process.env.NEXT_PUBLIC_SITE_URL || FALLBACK_URL;
  try {
    return new URL(raw).origin;
  } catch {
    return FALLBACK_URL;
  }
}

/** The link-preview picture drawn by app/opengraph-image.tsx. */
const SHARE_IMAGE = {
  url: "/opengraph-image",
  width: 1200,
  height: 630,
  alt: `${SITE_NAME} — ${SITE_TAGLINE} for UK community pharmacies`,
};

/**
 * Canonical address and link-preview metadata for one public page.
 *
 * <b>Use this rather than writing `openGraph` by hand.</b> Next.js replaces a
 * parent's `openGraph` and `twitter` objects wholesale when a page sets its
 * own — it does not merge them — so a page that set only a title silently
 * lost the site name, locale and preview image, and X fell back to a small
 * card with no picture. Every field is restated here, once.
 */
export function pageMetadata({
  title,
  description,
  path,
}: {
  title: string;
  description: string;
  /** Omit for site-wide defaults: no page's address is inherited by another. */
  path?: string;
}) {
  const shareTitle = title.includes(SITE_NAME) ? title : `${title} · ${SITE_NAME}`;
  return {
    description,
    ...(path ? { alternates: { canonical: path } } : {}),
    openGraph: {
      type: "website" as const,
      siteName: SITE_NAME,
      locale: "en_GB",
      ...(path ? { url: path } : {}),
      title: shareTitle,
      description,
      images: [SHARE_IMAGE],
    },
    twitter: {
      card: "summary_large_image" as const,
      title: shareTitle,
      description,
      images: [SHARE_IMAGE.url],
    },
  };
}

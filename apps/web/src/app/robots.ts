import type { MetadataRoute } from "next";
import { siteUrl } from "@/lib/site";

/**
 * Crawl the public site; stay out of the app.
 *
 * The signed-in areas redirect a crawler to the login page anyway, but each
 * is listed so none of them is fetched, queued and reported as a redirect in
 * Search Console. `/api/` covers the proxy routes, including card and asset
 * images. `/display/` is a shop-window kiosk page reached by pairing code.
 */
/** Per request: the host comes from SITE_URL on the running container, not the build. */
export const dynamic = "force-dynamic";

const PRIVATE = [
  "/api/",
  "/activate",
  "/admin",
  "/analytics",
  "/billing",
  "/brand-kit",
  "/campaigns",
  "/canvas",
  "/dashboard",
  "/display",
  "/displays",
  "/forgot-password",
  "/onboarding",
  "/patients",
  "/pharmacies",
  "/playlists",
  "/posters",
  "/profile",
  "/reset-password",
  "/social",
  "/unsubscribe",
  "/verify-email",
  "/videos",
];

export default function robots(): MetadataRoute.Robots {
  const url = siteUrl();
  return {
    rules: [{ userAgent: "*", allow: "/", disallow: PRIVATE }],
    sitemap: `${url}/sitemap.xml`,
    host: url,
  };
}

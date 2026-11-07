import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * The rendered campaign email, viewable in a browser.
 *
 * Needs its own route for the same reason the CSV template does: the
 * `/api/admin/[...path]` catch-all parses every response as JSON and sends
 * `null` when that fails. This returns `text/html`, so through the catch-all it
 * would arrive as the word `null` — the identical failure the Template download
 * had, in a different content type.
 */
export const GET = (req: Request) => {
  const search = new URL(req.url).search;
  return proxyDownload(
    `/api/admin/diagnostics/campaign-email/preview${search}`,
    "text/html; charset=utf-8",
  );
};

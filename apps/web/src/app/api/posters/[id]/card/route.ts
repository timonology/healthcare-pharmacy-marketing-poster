import { proxyImage } from "@/lib/server/download-proxy";

/**
 * A poster's card on My Posters, drawn by the same renderer as its PDF.
 *
 * Proxied rather than linked to storage: the browser cannot reach blob
 * storage in Docker, and a signed link expires while the page is open. See
 * `PosterExportController.Card` for the caching contract (ETag, no-cache).
 */
export const dynamic = "force-dynamic";

export const GET = async (req: Request, { params }: { params: Promise<{ id: string }> }) =>
  proxyImage(req, `/api/posters/${encodeURIComponent((await params).id)}/card`);

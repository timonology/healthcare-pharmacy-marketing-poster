import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * Poster PDF export.
 *
 * There was no web route for this at all — the posters list and the editor
 * top bar both navigate to `/api/posters/{id}/export.pdf`, which Next answered
 * with its own 404. The end-to-end suite exercises the PDF path against the
 * API directly, so it passed throughout: the API was never the problem.
 */
export const GET = async (_req: Request, { params }: { params: Promise<{ id: string }> }) =>
  proxyDownload(`/api/posters/${encodeURIComponent((await params).id)}/export.pdf`, "application/pdf");

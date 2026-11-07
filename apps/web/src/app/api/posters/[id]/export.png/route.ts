import { proxyDownload } from "@/lib/server/download-proxy";

/** Poster PNG export — same reasoning as export.pdf alongside it. */
export const GET = async (_req: Request, { params }: { params: Promise<{ id: string }> }) =>
  proxyDownload(`/api/posters/${encodeURIComponent((await params).id)}/export.png`, "image/png");

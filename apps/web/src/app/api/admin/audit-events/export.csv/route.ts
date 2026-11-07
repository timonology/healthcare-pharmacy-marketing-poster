import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * The audit console's CSV export.
 *
 * Its own route rather than the admin JSON catch-all — see download-proxy for
 * why a CSV routed through one arrives as a page reading "null". The filters
 * are passed through unchanged; the API decides what this caller may see.
 */
export const GET = (req: Request) =>
  proxyDownload(
    `/api/admin/audit-events/export.csv${new URL(req.url).search}`,
    "text/csv; charset=utf-8",
  );

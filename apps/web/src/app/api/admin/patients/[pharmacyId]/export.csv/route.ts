import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * A chosen pharmacy's contacts, exported by platform staff.
 *
 * Its own route rather than the JSON catch-all — see download-proxy for why a
 * CSV routed through one arrives as a page reading "null".
 */
export const GET = async (
  _req: Request,
  { params }: { params: Promise<{ pharmacyId: string }> },
) =>
  proxyDownload(
    `/api/admin/patients/${encodeURIComponent((await params).pharmacyId)}/export.csv`,
    "text/csv; charset=utf-8",
  );

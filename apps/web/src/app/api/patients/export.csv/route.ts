import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * The pharmacy's own contacts, as a CSV download.
 *
 * Its own route rather than the JSON catch-all, for the reason documented in
 * download-proxy: a catch-all parses every body as JSON and serves `null` when
 * that fails, so a CSV arrives as a page reading "null" while the API log shows
 * a perfectly good 200.
 */
export const GET = () => proxyDownload("/api/patients/export.csv", "text/csv; charset=utf-8");

import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * The blank CSV a SuperAdmin downloads before a bulk pharmacist import.
 *
 * Needs its own route because `/api/admin/[...path]` is JSON-only: it parses
 * every response as JSON and sends `null` when that fails. Clicking "Template"
 * navigated the browser to a page reading `null` while the API log showed a
 * perfectly good 200 with 181 bytes of text/csv.
 *
 * A static segment outranks the catch-all in Next's matcher, so simply existing
 * here is enough to take the route over.
 */
export const GET = () =>
  proxyDownload("/api/admin/invitations/csv-template", "text/csv; charset=utf-8");

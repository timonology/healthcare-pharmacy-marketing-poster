import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * A QR code for the pharmacy's booking link.
 *
 * <b>This route exists because a canvas stores this path.</b> A poster that
 * carries a booking code holds `/api/brand-kit/booking-qr` as the image source,
 * and in the editor that is resolved by the browser asking this app for it —
 * the same arrangement the logo has, and for the same reason: the document
 * outlives any signed URL that could have been baked into it.
 *
 * Without it the export would be correct and the editor would show a broken
 * image, which is the worst way round to be wrong. The person laying out the
 * poster would see a fault that is not there and move or delete a code that
 * would have printed perfectly.
 *
 * `proxyDownload` rather than the JSON proxy: this returns image bytes, and the
 * JSON path would try to parse a PNG and hand back the word `null`.
 *
 * A 404 here is the ordinary case for a pharmacy that has not set a booking
 * link. The API answers that deliberately — no link means no code, rather than
 * a square that scans to nothing.
 */
export const GET = () => proxyDownload("/api/brand-kit/booking-qr", "image/png");

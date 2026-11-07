/**
 * The Content-Security-Policy for pages (security review SEC-09 / R3-09).
 *
 * Scripts are allowed only with this request's nonce ('strict-dynamic' lets
 * the scripts Next loads that way load their own chunks). That is what stops
 * an injected <script> — the main thing a CSP is for — and it needs a fresh
 * nonce per request, which is why it is built in middleware rather than in
 * next.config.mjs.
 *
 * Shipped in **report-only** mode first (`CSP_MODE=report-only`, the
 * default): the browser reports what the policy would have blocked, to
 * `/api/csp-report`, and blocks nothing. Switch to `CSP_MODE=enforce` once the
 * reports are clean. A policy enforced blind is how a site ends up a blank
 * page.
 *
 * Everything the browser loads is same-origin: the app calls the API through
 * its own /api routes, and images and videos come through them too. `https:`
 * stays in img-src for stock-photo thumbnails shown while choosing a photo.
 */
export type CspMode = "enforce" | "report-only" | "off";

export const NONCE_HEADER = "x-nonce";
export const REPORT_PATH = "/api/csp-report";

export function cspMode(): CspMode {
  const v = (process.env.CSP_MODE ?? "report-only").toLowerCase();
  return v === "enforce" || v === "off" ? v : "report-only";
}

export function buildCsp(
  nonce: string,
  { dev = false, frameAncestors }: { dev?: boolean; frameAncestors?: string } = {},
): string {
  const directives: Record<string, string[]> = {
    "default-src": ["'self'"],
    // 'unsafe-eval' in development only: React's dev build and Fast Refresh use eval.
    "script-src": ["'self'", `'nonce-${nonce}'`, "'strict-dynamic'", ...(dev ? ["'unsafe-eval'"] : [])],
    // Inline style attributes are everywhere (canvas previews, Radix); styles
    // cannot run code, so they are not what this policy is for.
    "style-src": ["'self'", "'unsafe-inline'"],
    "img-src": ["'self'", "data:", "blob:", "https:"],
    "font-src": ["'self'", "data:"],
    "media-src": ["'self'", "blob:"],
    // data:/blob: — the photo picker turns its prepared data URL back into
    // a Blob with fetch(), and that counts as a connection.
    "connect-src": ["'self'", "data:", "blob:", ...(dev ? ["ws:", "wss:"] : [])],
    "worker-src": ["'self'", "blob:"],
    "frame-src": ["'self'"],
    "object-src": ["'none'"],
    "base-uri": ["'self'"],
    "form-action": ["'self'"],
    "report-uri": [REPORT_PATH],
  };
  // Only when enforcing: browsers ignore frame-ancestors in a report-only
  // policy (and say so in the console). next.config.mjs also sends it, so an
  // enforced policy carries it either way the two headers end up combined.
  if (frameAncestors) directives["frame-ancestors"] = [frameAncestors];
  return Object.entries(directives)
    .map(([k, v]) => `${k} ${v.join(" ")}`)
    .join("; ");
}

/** 128 random bits, base64 — the form the CSP spec expects for a nonce. */
export function newNonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s);
}

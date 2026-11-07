import { API_BASE } from "@/lib/server/api-proxy";

/**
 * A video playing on an in-pharmacy screen.
 *
 * The kiosk's `<video>` element cannot send the screen token in a header, so
 * the content API hands it this path with a signature for this one video and
 * a window of time (`sig`). This route is same-origin for the kiosk and passes
 * the request to the API's signed download, which checks the signature.
 *
 * Streamed, never buffered, and with `Range` passed both ways: browsers fetch
 * video in pieces, and Safari will not play a video whose server ignores
 * ranges. No blob key and no screen token ever reach the browser.
 *
 * <b>Cacheable by the screen's own browser, on purpose.</b> A playlist loops
 * every few minutes for months; without caching each loop pulls every MP4
 * again. The URL carries the video's version (`v`), so a re-cut is a new URL,
 * and the API's ETag lets the browser keep ranged responses and revalidate
 * with `If-Range` / `If-None-Match`, both forwarded here. This is the
 * browser's ordinary HTTP cache — nothing is downloaded ahead of play.
 */

/** Video ids are Mongo-safe GUIDs; anything else is not one of ours. */
const VIDEO_ID = /^[A-Za-z0-9_-]{1,64}$/;

/** Request headers the browser uses to fetch in pieces and revalidate. */
const FORWARD_REQUEST = ["range", "if-range", "if-none-match", "if-modified-since"] as const;

/** Response headers the browser needs to play, seek and cache. */
const FORWARD_RESPONSE = [
  "content-type",
  "content-length",
  "content-range",
  "accept-ranges",
  "etag",
  "last-modified",
] as const;

/**
 * As long as the signing window (6h), never longer: every link the API hands
 * out stays valid for at least that long, and the version in the URL means the
 * bytes behind it cannot change.
 */
const CACHE_CONTROL = "private, max-age=21600";

export async function GET(req: Request, { params: paramsPromise }: { params: Promise<{ videoId: string }> }) {
  const params = await paramsPromise;
  // Checked, not just encoded: encodeURIComponent leaves "." alone, so an id of
  // ".." would otherwise walk the upstream path up a level.
  if (!VIDEO_ID.test(params.videoId)) return new Response(null, { status: 404 });

  const sig = new URL(req.url).searchParams.get("sig");
  if (!sig) return new Response("Missing signature", { status: 400 });

  const headers: Record<string, string> = {};
  for (const name of FORWARD_REQUEST) {
    const value = req.headers.get(name);
    if (value) headers[name] = value;
  }

  let upstream: Response;
  try {
    upstream = await fetch(
      `${API_BASE}/api/videos/${params.videoId}/public-download?token=${encodeURIComponent(sig)}`,
      // The kiosk moving on (or a seek) aborts its request; passing the signal
      // stops the upstream transfer too rather than draining it to nobody.
      { headers, cache: "no-store", signal: req.signal },
    );
  } catch {
    // The API is down or the kiosk went away. The kiosk treats either as
    // "skip this video for now".
    return new Response(null, { status: 502 });
  }

  const out = new Headers();
  for (const name of FORWARD_RESPONSE) {
    const value = upstream.headers.get(name);
    if (value) out.set(name, value);
  }

  if (upstream.status === 304) {
    // The browser's cached copy is current. Validators and cache policy go
    // back with it so the entry stays fresh.
    out.set("cache-control", CACHE_CONTROL);
    return new Response(null, { status: 304, headers: out });
  }

  if (!upstream.ok) {
    // 403 for an expired or wrong signature, 404 for a deleted video, 416 for
    // a range past the end. The kiosk skips the slide and picks up a fresh
    // link on its next poll, so the API's error body adds nothing — and is not
    // cached.
    const status = upstream.status;
    await upstream.body?.cancel().catch(() => {});
    return new Response(null, {
      status,
      headers: status === 416 && out.has("content-range")
        ? { "content-range": out.get("content-range")! }
        : undefined,
    });
  }

  if (!out.has("content-type")) out.set("content-type", "video/mp4");
  out.set("cache-control", CACHE_CONTROL);

  return new Response(upstream.body, { status: upstream.status, headers: out });
}

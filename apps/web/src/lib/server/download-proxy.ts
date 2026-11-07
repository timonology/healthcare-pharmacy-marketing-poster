import { readAccessToken } from "@/lib/server/cookies";
import { isSafeApiPath } from "@/lib/server/api-path";
import { forwardingHeaders } from "@/lib/server/forwarding";

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

/**
 * Streams a file response from the API straight to the browser.
 *
 * `apiFetch` — and therefore every catch-all proxy built on it — reads the body
 * as text, tries to parse it as JSON, and returns null when that fails. A CSV
 * or a PDF routed through one of those does not error: it arrives as a page
 * showing the word `null`. That is exactly what the "Template" download link on
 * the invitations screen did, and the API log looked perfect throughout,
 * because the API had in fact served the file correctly.
 *
 * So anything that is not JSON needs a route like this one, and needs it to
 * sit at a more specific path than the catch-all so Next matches it first.
 *
 * Content-Type and Content-Disposition are forwarded from the API rather than
 * restated here. The API already decides the filename, and a second opinion
 * about it in the web layer is one more thing to drift.
 */
export async function proxyDownload(
  path: string,
  fallbackContentType = "application/octet-stream",
): Promise<Response> {
  if (!isSafeApiPath(path)) return new Response("Invalid request path.", { status: 400 });

  const token = (await readAccessToken());
  if (!token) return new Response("Unauthorized", { status: 401 });

  const upstream = await fetch(`${API_BASE}${path}`, {
    headers: { ...(await forwardingHeaders()), Authorization: `Bearer ${token}` },
    cache: "no-store",
  });

  if (!upstream.ok) {
    // Plain text, not JSON: the browser is navigating to this URL, so whatever
    // comes back is rendered as a page and should be readable by a person.
    const detail = await upstream.text().catch(() => "");
    return new Response(detail || `Download failed (${upstream.status})`, {
      status: upstream.status,
      headers: { "Content-Type": "text/plain; charset=utf-8" },
    });
  }

  const headers: Record<string, string> = {
    "Content-Type": upstream.headers.get("Content-Type") ?? fallbackContentType,
    "Cache-Control": "private, no-store",
  };

  const disposition = upstream.headers.get("Content-Disposition");
  if (disposition) headers["Content-Disposition"] = disposition;

  const length = upstream.headers.get("Content-Length");
  if (length) headers["Content-Length"] = length;

  return new Response(upstream.body, { status: 200, headers });
}

/**
 * Streams an image the API revalidates with an ETag, keeping the browser's
 * cache in the loop.
 *
 * `proxyDownload` is for files a person saves: it marks everything
 * `no-store` and drops the validators, which for an image drawn on every
 * visit to a page would mean re-downloading every card every time. This
 * forwards `If-None-Match` upstream and passes `ETag`, `Cache-Control` and a
 * 304 straight back, so an unchanged card costs a request and no bytes.
 *
 * Errors come back as an empty body with the upstream status: the caller is
 * an `<img>`, which cannot show a message, and its `onError` handler decides
 * what to draw instead.
 */
export async function proxyImage(req: Request, path: string): Promise<Response> {
  if (!isSafeApiPath(path)) return new Response(null, { status: 400 });

  const token = (await readAccessToken());
  if (!token) return new Response(null, { status: 401 });

  const headers: Record<string, string> = {
    ...(await forwardingHeaders()),
    Authorization: `Bearer ${token}`,
  };
  const ifNoneMatch = req.headers.get("If-None-Match");
  if (ifNoneMatch) headers["If-None-Match"] = ifNoneMatch;

  const upstream = await fetch(`${API_BASE}${path}`, { headers, cache: "no-store" });

  const out: Record<string, string> = {
    "Cache-Control": upstream.headers.get("Cache-Control") ?? "private, no-cache",
  };
  const etag = upstream.headers.get("ETag");
  if (etag) out["ETag"] = etag;

  if (upstream.status === 304) return new Response(null, { status: 304, headers: out });
  if (!upstream.ok) {
    await upstream.body?.cancel().catch(() => undefined);
    return new Response(null, { status: upstream.status, headers: { "Cache-Control": "no-store" } });
  }

  out["Content-Type"] = upstream.headers.get("Content-Type") ?? "image/jpeg";
  const length = upstream.headers.get("Content-Length");
  if (length) out["Content-Length"] = length;

  return new Response(upstream.body, { status: 200, headers: out });
}

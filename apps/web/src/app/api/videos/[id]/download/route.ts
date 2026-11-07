import { readAccessToken } from "@/lib/server/cookies";
import { forwardingHeaders } from "@/lib/server/forwarding";

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

/**
 * BFF proxy for owner-authenticated video download. We can't use apiFetch
 * here because we need to stream the raw response body (mp4 bytes) back to
 * the browser, and apiFetch is JSON-only.
 */
export async function GET(_req: Request, { params: paramsPromise }: { params: Promise<{ id: string }> }) {
  const params = await paramsPromise;
  if (params.id === "." || params.id === "..") return new Response(null, { status: 404 });
  const token = (await readAccessToken());
  if (!token) return new Response("Unauthorized", { status: 401 });

  const upstream = await fetch(`${API_BASE}/api/videos/${encodeURIComponent(params.id)}/download`, {
    headers: { ...(await forwardingHeaders()), Authorization: `Bearer ${token}` },
    cache: "no-store",
  });

  if (!upstream.ok) {
    const text = await upstream.text().catch(() => "");
    return new Response(text || `Video download failed (${upstream.status})`, {
      status: upstream.status,
    });
  }

  return new Response(upstream.body, {
    status: 200,
    headers: {
      "Content-Type": upstream.headers.get("Content-Type") ?? "video/mp4",
      // Range support so the <video> element can seek. `Content-Length` and
      // `Accept-Ranges` are only forwarded if the upstream provided them.
      ...(upstream.headers.get("Content-Length")
        ? { "Content-Length": upstream.headers.get("Content-Length")! }
        : {}),
      "Accept-Ranges": upstream.headers.get("Accept-Ranges") ?? "bytes",
      "Cache-Control": "private, max-age=60",
    },
  });
}

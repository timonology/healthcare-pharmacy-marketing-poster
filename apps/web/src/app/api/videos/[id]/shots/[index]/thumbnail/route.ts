import { readAccessToken } from "@/lib/server/cookies";

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

/**
 * A still of one shot — a tile in the picker. Streamed, not JSON: an image
 * through a JSON proxy arrives as `null` (see check-proxies.mjs).
 */
export async function GET(
  _req: Request,
  { params: paramsPromise }: { params: Promise<{ id: string; index: string }> },
) {
  const params = await paramsPromise;
  const token = (await readAccessToken());
  if (!token) return new Response("Unauthorized", { status: 401 });

  // A number only: this is spliced into the upstream path.
  if (!/^\d{1,3}$/.test(params.index)) return new Response("Not found", { status: 404 });

  const upstream = await fetch(
    `${API_BASE}/api/videos/${encodeURIComponent(params.id)}/shots/${params.index}/thumbnail`,
    { headers: { Authorization: `Bearer ${token}` }, cache: "no-store" },
  );

  if (!upstream.ok) {
    return new Response(null, { status: upstream.status });
  }

  return new Response(upstream.body, {
    status: 200,
    headers: {
      "Content-Type": upstream.headers.get("Content-Type") ?? "image/jpeg",
      // Private and short: a refine replaces the still under the same key.
      "Cache-Control": "private, max-age=60",
    },
  });
}

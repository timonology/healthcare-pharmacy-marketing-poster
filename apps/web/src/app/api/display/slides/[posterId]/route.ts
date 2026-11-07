import { NextResponse } from "next/server";
import { API_BASE } from "@/lib/server/api-proxy";

/**
 * One rendered poster, on its way to a screen.
 *
 * <b>A file, so it needs a route of its own.</b> The JSON catch-alls in this
 * app parse what they proxy and re-serialise it; a PNG through one of those
 * arrives as the word `null`, which is the exact bug that made the poster
 * download return nothing and is why `check-proxies.mjs` exists. It caught
 * this route being missing before it was written.
 *
 * The token travels in a header rather than the query string, so the
 * credential never appears in an access log or a Referer.
 */
export async function GET(
  req: Request,
  { params }: { params: Promise<{ posterId: string }> },
) {
  const token = req.headers.get("x-display-token");
  if (!token) {
    // 400, not 401. A 401 tells the device its credential is no good and makes
    // it forget the token; a request that never carried one is simply malformed,
    // and a proxy dropping the header must not cost a screen its pairing.
    return NextResponse.json(
      { error: "This request needs a screen token." },
      { status: 400 },
    );
  }

  const { posterId } = await params;

  const res = await fetch(
    `${API_BASE}/api/display/slides/${encodeURIComponent(posterId)}`,
    { headers: { "X-Display-Token": token }, cache: "no-store" },
  );

  if (!res.ok) {
    return NextResponse.json(
      { error: "That poster is not available." },
      { status: res.status },
    );
  }

  // Streamed rather than buffered: an A4 render at 150dpi is a few megabytes,
  // and a wall of screens waking up together should not each hold one in the
  // BFF's memory.
  return new NextResponse(res.body, {
    status: 200,
    headers: {
      "Content-Type": res.headers.get("content-type") ?? "image/png",
      // The kiosk keeps its own offline copy, and the render is content-hashed
      // upstream, so a long private cache is safe and saves a device on a poor
      // connection from re-fetching the same poster every loop.
      "Cache-Control": "private, max-age=3600",
    },
  });
}

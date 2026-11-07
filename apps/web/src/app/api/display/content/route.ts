import { NextResponse } from "next/server";
import { API_BASE } from "@/lib/server/api-proxy";
import { forwardingHeaders } from "@/lib/server/forwarding";

/**
 * Read lower-case, because that is how headers arrive.
 *
 * <b>Not exported.</b> A Next route file may only export route handlers and a
 * short list of known config values; anything else fails the build with a type
 * error against the generated route types, which is a message that names
 * `OmitWithTag` and takes a while to connect to a stray `export const`.
 *
 * The canonical name lives in `@acme/shared-types` as `DISPLAY_TOKEN_HEADER`
 * for callers that set it. `Headers.get` is case-insensitive, so the two agree.
 */
const TOKEN_HEADER = "x-display-token";

/**
 * The playlist a screen should be showing.
 *
 * <b>The ETag has to survive the trip.</b> A screen polls this every thirty
 * seconds for years, and the whole reason that is affordable is that an
 * unchanged playlist answers 304 with no body. A proxy that dropped either the
 * incoming `If-None-Match` or the outgoing `ETag` would turn every poll into a
 * full download — it would still work, and the cost would only show up as a
 * bandwidth bill nobody could explain.
 */
export async function GET(req: Request) {
  const token = req.headers.get(TOKEN_HEADER);
  if (!token) {
    // 400, not 401. A 401 tells the device its credential is no good and makes
    // it forget the token; a request that never carried one is simply malformed,
    // and a proxy dropping the header must not cost a screen its pairing.
    return NextResponse.json(
      { error: "This request needs a screen token." },
      { status: 400 },
    );
  }

  const headers: Record<string, string> = { ...(await forwardingHeaders()), "X-Display-Token": token };

  const ifNoneMatch = req.headers.get("if-none-match");
  if (ifNoneMatch) headers["If-None-Match"] = ifNoneMatch;

  // What this kiosk can play. Without it the API sends posters only — which is
  // how a kiosk still running pre-video code keeps working through a deploy.
  const capabilities = req.headers.get("x-display-capabilities");
  if (capabilities) headers["X-Display-Capabilities"] = capabilities;

  const res = await fetch(`${API_BASE}/api/display/content`, {
    headers,
    cache: "no-store",
  });

  if (res.status === 304) {
    // No body on a 304, by definition — constructing one would make the
    // browser treat it as a change.
    return new NextResponse(null, {
      status: 304,
      headers: res.headers.get("etag") ? { ETag: res.headers.get("etag")! } : undefined,
    });
  }

  const text = await res.text();
  const out = new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": "application/json" },
  });

  const etag = res.headers.get("etag");
  if (etag) out.headers.set("ETag", etag);

  // Never shared. One screen's playlist sitting in a shared cache and being
  // handed to the next screen that asks would be a cross-tenant leak created
  // by infrastructure rather than by code.
  out.headers.set("Cache-Control", "private, no-store");

  return out;
}

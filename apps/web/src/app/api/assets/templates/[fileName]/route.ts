import { NextResponse } from "next/server";

/**
 * Serves template images through the web origin.
 *
 * Canvas documents store a relative URL ("/api/assets/templates/….jpg") rather
 * than an absolute one, because an absolute URL bakes a hostname into a
 * document that outlives it — the same template is rendered from localhost, a
 * staging host and production, and the PDF exporter reads it server-side too.
 *
 * A relative URL resolves against whichever origin is rendering, which for the
 * browser is this Next.js app, not the .NET API. Hence this passthrough.
 *
 * Anonymous, matching the API endpoint it proxies: the template gallery itself
 * is public, so its imagery has to be.
 */

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

// Next 15+: params is a Promise.
type Ctx = { params: Promise<{ fileName: string }> };

export async function GET(_req: Request, ctx: Ctx) {
  const { fileName } = await ctx.params;

  // Reject anything that isn't the shape the API mints (32 hex chars + a known
  // extension) before it reaches the network. The API validates this too; this
  // just avoids proxying obvious junk.
  if (!/^[0-9a-f]{32}\.(png|jpg|webp)$/i.test(fileName)) {
    return new NextResponse(null, { status: 404 });
  }

  const upstream = await fetch(
    `${API_BASE}/api/assets/templates/${fileName}`,
    { cache: "no-store" },
  );

  if (!upstream.ok || !upstream.body) {
    return new NextResponse(null, { status: upstream.status || 404 });
  }

  return new NextResponse(upstream.body, {
    status: 200,
    headers: {
      "Content-Type": upstream.headers.get("content-type") ?? "image/jpeg",
      // Immutable: the name is a GUID minted at upload, so the bytes behind a
      // given URL never change.
      "Cache-Control": "public, max-age=31536000, immutable",
    },
  });
}

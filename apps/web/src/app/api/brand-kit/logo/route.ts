import { NextResponse } from "next/server";
import type { BrandKit } from "@acme/shared-types";
import { API_BASE } from "@/lib/server/api-proxy";
import { readAccessToken } from "@/lib/server/cookies";
import { proxyDownload } from "@/lib/server/download-proxy";
import { forwardingHeaders } from "@/lib/server/forwarding";

/**
 * The pharmacy's logo: read it, or replace it.
 *
 * <b>GET is the path a canvas stores.</b> It never expires and carries no
 * signature, which is what lets a poster saved today still show its logo in a
 * year — the signed URL on the brand-kit DTO lasts fifteen minutes and is only
 * good for a page that is open right now. It goes through `proxyDownload`
 * because the JSON proxy would parse an image and hand back the word `null`.
 */
export async function GET(): Promise<Response> {
  const res = await proxyDownload("/api/brand-kit/logo", "image/png");
  // New uploads are stored as pixels, but a logo uploaded as SVG before that
  // (security review SEC-10) is a document that can carry script. Served from
  // our own origin, opening its URL would run it as us. A sandboxed,
  // script-free policy makes it an inert picture; <img> ignores it either way.
  res.headers.set(
    "Content-Security-Policy",
    "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; sandbox",
  );
  return res;
}

/**
 * POST is the upload.
 *
 * Hand-written rather than left to a proxy because this is multipart: the body
 * is a stream with a boundary in the content type, and reading it as JSON or
 * re-encoding it corrupts the file. The request body and its content type are
 * both forwarded untouched.
 */
export async function POST(req: Request): Promise<Response> {
  const token = (await readAccessToken());
  if (!token) {
    return NextResponse.json({ error: "Not signed in." }, { status: 401 });
  }

  const contentType = req.headers.get("content-type");
  if (!contentType?.includes("multipart/form-data")) {
    return NextResponse.json(
      { error: "Upload the logo as a file." },
      { status: 400 },
    );
  }

  const upstream = await fetch(`${API_BASE}/api/brand-kit/logo`, {
    method: "POST",
    headers: {
      ...(await forwardingHeaders()),
      Authorization: `Bearer ${token}`,
      // Forwarded verbatim: it carries the multipart boundary, and a boundary
      // that does not match the body makes the upload unparseable upstream.
      "Content-Type": contentType,
    },
    body: await req.arrayBuffer(),
    cache: "no-store",
  });

  const text = await upstream.text();
  if (!upstream.ok) {
    return NextResponse.json(
      { error: text || "Could not upload the logo." },
      { status: upstream.status },
    );
  }

  return NextResponse.json(JSON.parse(text) as BrandKit);
}

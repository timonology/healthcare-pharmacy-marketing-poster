import { NextResponse } from "next/server";
import { API_BASE } from "@/lib/server/api-proxy";
import { readAccessToken } from "@/lib/server/cookies";
import { forwardingHeaders } from "@/lib/server/forwarding";

/**
 * Uploads a photo for an AI design.
 *
 * Its own route because the body is multipart: the boundary in Content-Type
 * must reach the API untouched, so the body is forwarded as bytes and the
 * header copied, not rebuilt. The API checks the file really is a JPEG, PNG or
 * WebP and answers with a reason when it is not — passed through verbatim so
 * the pharmacist reads it before they press Generate.
 */
export async function POST(req: Request) {
  const token = (await readAccessToken());
  if (!token) return NextResponse.json({ error: "Not signed in." }, { status: 401 });

  const upstream = await fetch(`${API_BASE}/api/posters/photos`, {
    method: "POST",
    headers: {
      ...(await forwardingHeaders()),
      Authorization: `Bearer ${token}`,
      "Content-Type": req.headers.get("content-type") ?? "multipart/form-data",
    },
    body: await req.arrayBuffer(),
    cache: "no-store",
  });

  const text = await upstream.text();
  let body: unknown = null;
  try {
    body = text ? JSON.parse(text) : null;
  } catch {
    // A 413 from a proxy in front of the API is HTML, not JSON.
    body = { error: upstream.status === 413 ? "That photo is too large." : "Upload failed." };
  }
  return NextResponse.json(body ?? { error: "Upload failed." }, { status: upstream.status });
}

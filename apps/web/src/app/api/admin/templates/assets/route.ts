import { NextResponse } from "next/server";
import { readAccessToken } from "@/lib/server/cookies";
import { forwardingHeaders } from "@/lib/server/forwarding";

/**
 * Image upload proxy.
 *
 * Deliberately NOT handled by the /api/admin/[...path] catch-all. That proxy
 * reads the request with `req.text()`, which decodes the body as UTF-8 — fine
 * for JSON, destructive for a JPEG — and forwards it as a string, losing the
 * multipart boundary along with it. The upload would arrive corrupt and
 * unparseable. A static segment takes priority over the catch-all, so this file
 * intercepts the route.
 *
 * The body is passed straight through as a stream and the original
 * Content-Type (boundary included) is preserved.
 */

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

export const maxDuration = 120;

export async function POST(req: Request) {
  const token = (await readAccessToken());
  if (!token) {
    return NextResponse.json({ error: "Not authenticated." }, { status: 401 });
  }

  const contentType = req.headers.get("content-type");
  if (!contentType?.includes("multipart/form-data")) {
    return NextResponse.json(
      { error: "Expected a multipart upload." },
      { status: 400 },
    );
  }

  // Buffer rather than stream: undici requires `duplex: "half"` to stream a
  // request body, support for which varies by runtime, and these are capped at
  // 12MB by the API anyway.
  const body = await req.arrayBuffer();

  const res = await fetch(`${API_BASE}/api/admin/templates/assets`, {
    method: "POST",
    headers: {
      ...(await forwardingHeaders()),
      "Content-Type": contentType,
      Authorization: `Bearer ${token}`,
    },
    body,
    cache: "no-store",
  });

  const text = await res.text();
  let payload: unknown = null;
  try {
    payload = text ? JSON.parse(text) : null;
  } catch {
    payload = null;
  }

  if (!res.ok) {
    const error =
      payload && typeof payload === "object" && "error" in payload
        ? String((payload as { error: unknown }).error)
        : res.statusText || "Upload failed.";
    return NextResponse.json({ error }, { status: res.status || 400 });
  }

  return NextResponse.json(payload);
}

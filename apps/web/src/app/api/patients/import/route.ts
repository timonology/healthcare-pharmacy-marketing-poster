import { NextResponse } from "next/server";
import { readAccessToken } from "@/lib/server/cookies";
import { forwardingHeaders } from "@/lib/server/forwarding";

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

/**
 * Uploads a CSV of contacts.
 *
 * Needs its own route because the catch-all proxy sends JSON bodies. A
 * multipart upload has a boundary in its Content-Type that must survive
 * untouched, so the body is forwarded as a stream and the header is copied
 * rather than rebuilt — reconstructing it drops the boundary and the API sees
 * an empty form.
 */
export async function POST(req: Request) {
  const token = (await readAccessToken());
  if (!token) return NextResponse.json({ error: "Not signed in." }, { status: 401 });

  const upstream = await fetch(`${API_BASE}/api/patients/import`, {
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
  const body = text ? JSON.parse(text) : null;

  return NextResponse.json(body, { status: upstream.status });
}

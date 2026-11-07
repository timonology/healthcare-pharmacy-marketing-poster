import { NextResponse } from "next/server";
import { readAccessToken } from "@/lib/server/cookies";
import { forwardingHeaders } from "@/lib/server/forwarding";

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

/**
 * Uploads a CSV into a chosen pharmacy, as platform staff.
 *
 * Forwards the multipart body as a stream with its Content-Type copied intact —
 * the boundary in that header is what separates the parts, and rebuilding the
 * header drops it, leaving the API with an empty form.
 */
export async function POST(
  req: Request,
  { params: paramsPromise }: { params: Promise<{ pharmacyId: string }> },
) {
  const params = await paramsPromise;
  const token = (await readAccessToken());
  if (!token) return NextResponse.json({ error: "Not signed in." }, { status: 401 });

  const upstream = await fetch(
    `${API_BASE}/api/admin/patients/${encodeURIComponent(params.pharmacyId)}/import`,
    {
      method: "POST",
      headers: {
        ...(await forwardingHeaders()),
        Authorization: `Bearer ${token}`,
        "Content-Type": req.headers.get("content-type") ?? "multipart/form-data",
      },
      body: await req.arrayBuffer(),
      cache: "no-store",
    },
  );

  const text = await upstream.text();
  const body = text ? JSON.parse(text) : null;

  return NextResponse.json(body, { status: upstream.status });
}

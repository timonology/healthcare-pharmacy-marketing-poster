import { NextResponse } from "next/server";
import type { BrandKit } from "@acme/shared-types";
import { readAccessToken } from "@/lib/server/cookies";

const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5000";

/**
 * Forwards the multipart upload to the .NET API. We can't reuse the JSON-only
 * apiFetch helper because we need to stream the binary body through with the
 * correct boundary header.
 */
export async function POST(req: Request) {
  const token = await readAccessToken();
  if (!token) {
    return NextResponse.json({ error: "Not authenticated." }, { status: 401 });
  }

  const formData = await req.formData();
  const file = formData.get("file");
  if (!(file instanceof File)) {
    return NextResponse.json({ error: "Missing file." }, { status: 400 });
  }

  // Re-pack so the boundary matches what fetch generates internally.
  const upstream = new FormData();
  upstream.set("file", file, file.name);

  const res = await fetch(`${API_BASE}/api/brand-kit/logo`, {
    method: "POST",
    headers: { Authorization: `Bearer ${token}` },
    body: upstream,
  });

  const text = await res.text();
  const json = text ? safeJson(text) : null;

  if (!res.ok) {
    const error =
      (json && typeof json === "object" && "error" in json
        ? String((json as { error: unknown }).error)
        : null) ?? res.statusText;
    return NextResponse.json({ error }, { status: res.status });
  }

  return NextResponse.json(json as BrandKit);
}

function safeJson(text: string): unknown {
  try { return JSON.parse(text); } catch { return null; }
}

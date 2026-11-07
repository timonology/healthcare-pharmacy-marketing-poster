import { NextResponse } from "next/server";
import type { Video } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(_req: Request, { params: paramsPromise }: { params: Promise<{ id: string }> }) {
  const params = await paramsPromise;
  const r = await apiFetch<Video>(`/api/videos/${params.id}/retry`, {
    method: "POST",
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to retry video." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data, { status: 202 });
}

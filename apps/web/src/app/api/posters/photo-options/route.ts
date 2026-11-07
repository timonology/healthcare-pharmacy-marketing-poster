import { NextResponse } from "next/server";
import type { PhotoOptions } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/** Which photo sources are set up, so the AI mode is only offered when it works. */
export async function GET() {
  const r = await apiFetch<PhotoOptions>("/api/posters/photo-options", { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json({ error: r.error ?? "Could not read photo options." }, {
      status: r.status || 500,
    });
  }
  return NextResponse.json(r.data);
}

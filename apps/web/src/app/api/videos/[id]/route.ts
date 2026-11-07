import { NextResponse } from "next/server";
import type { Video } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(_req: Request, ctx: { params: Promise<{ id: string }> }) {
  const { id } = await ctx.params;
  const r = await apiFetch<Video>(`/api/videos/${id}`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load video." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

export async function DELETE(_req: Request, ctx: { params: Promise<{ id: string }> }) {
  const { id } = await ctx.params;
  const r = await apiFetch<unknown>(`/api/videos/${id}`, {
    method: "DELETE",
    auth: true,
  });
  if (!r.ok) {
    return NextResponse.json(
      { error: r.error ?? "Failed to delete video." },
      { status: r.status || 400 },
    );
  }
  return new NextResponse(null, { status: 204 });
}

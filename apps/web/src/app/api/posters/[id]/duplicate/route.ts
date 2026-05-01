import { NextResponse } from "next/server";
import type { Poster } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const result = await apiFetch<Poster>(`/api/posters/${id}/duplicate`, {
    method: "POST",
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to duplicate poster." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data, { status: 201 });
}

import { NextResponse } from "next/server";
import type { Poster, PosterStatus } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function PATCH(
  req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const { status } = (await req.json()) as { status: PosterStatus };

  const result = await apiFetch<Poster>(`/api/posters/${id}/status`, {
    method: "PATCH",
    body: JSON.stringify({ status }),
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to update status." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

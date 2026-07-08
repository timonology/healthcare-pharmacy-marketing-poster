import { NextResponse } from "next/server";
import type { Campaign } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(_req: Request, ctx: { params: Promise<{ id: string }> }) {
  const { id } = await ctx.params;
  const r = await apiFetch<Campaign>(`/api/campaigns/${id}/stop`, {
    method: "POST",
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to stop campaign." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data);
}

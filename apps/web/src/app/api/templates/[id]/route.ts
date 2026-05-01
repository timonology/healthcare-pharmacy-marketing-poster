import { NextResponse } from "next/server";
import type { Template } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const result = await apiFetch<Template>(`/api/templates/${id}`, { auth: true });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load template." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

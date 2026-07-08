import { NextResponse } from "next/server";
import type { Patient } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const r = await apiFetch<Patient[]>(`/api/patient-groups/${id}/members`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load members." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

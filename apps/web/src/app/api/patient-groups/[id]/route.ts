import { NextResponse } from "next/server";
import type { PatientGroup, UpsertPatientGroupRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const r = await apiFetch<PatientGroup>(`/api/patient-groups/${id}`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load group." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

export async function PUT(
  req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const body = (await req.json()) as UpsertPatientGroupRequest;
  const r = await apiFetch<PatientGroup>(`/api/patient-groups/${id}`, {
    method: "PUT",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to save group." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data);
}

export async function DELETE(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const r = await apiFetch<unknown>(`/api/patient-groups/${id}`, {
    method: "DELETE",
    auth: true,
  });
  if (!r.ok) {
    return NextResponse.json(
      { error: r.error ?? "Failed to delete group." },
      { status: r.status || 400 },
    );
  }
  return new NextResponse(null, { status: 204 });
}

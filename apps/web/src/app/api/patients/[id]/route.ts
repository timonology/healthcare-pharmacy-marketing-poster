import { NextResponse } from "next/server";
import type { Patient, UpsertPatientRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const r = await apiFetch<Patient>(`/api/patients/${id}`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load patient." },
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
  const body = (await req.json()) as UpsertPatientRequest;
  const r = await apiFetch<Patient>(`/api/patients/${id}`, {
    method: "PUT",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to save patient." },
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
  const r = await apiFetch<unknown>(`/api/patients/${id}`, {
    method: "DELETE",
    auth: true,
  });
  if (!r.ok) {
    return NextResponse.json(
      { error: r.error ?? "Failed to delete patient." },
      { status: r.status || 400 },
    );
  }
  return new NextResponse(null, { status: 204 });
}

import { NextResponse } from "next/server";
import type { PatientGroup, UpsertPatientGroupRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const r = await apiFetch<PatientGroup[]>("/api/patient-groups", { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load groups." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

export async function POST(req: Request) {
  const body = (await req.json()) as UpsertPatientGroupRequest;
  const r = await apiFetch<PatientGroup>("/api/patient-groups", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to create group." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data, { status: 201 });
}

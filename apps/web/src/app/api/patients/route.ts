import { NextResponse } from "next/server";
import type {
  Patient,
  PatientListResponse,
  UpsertPatientRequest,
} from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(req: Request) {
  const { searchParams } = new URL(req.url);
  const qs = new URLSearchParams();
  for (const key of ["search", "groupId", "skip", "take"] as const) {
    const value = searchParams.get(key);
    if (value) qs.set(key, value);
  }
  const suffix = qs.toString() ? `?${qs.toString()}` : "";
  const r = await apiFetch<PatientListResponse>(`/api/patients${suffix}`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load patients." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

export async function POST(req: Request) {
  const body = (await req.json()) as UpsertPatientRequest;
  const r = await apiFetch<Patient>("/api/patients", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to create patient." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data, { status: 201 });
}

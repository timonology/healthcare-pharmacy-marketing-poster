import { NextResponse } from "next/server";
import type { Me, UpsertProfileRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function PUT(req: Request) {
  const body = (await req.json()) as UpsertProfileRequest;
  const r = await apiFetch<Me>("/api/me/profile", {
    method: "PUT",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to save profile." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data);
}

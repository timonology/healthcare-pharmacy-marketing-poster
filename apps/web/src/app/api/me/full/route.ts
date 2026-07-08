import { NextResponse } from "next/server";
import type { Me } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const r = await apiFetch<Me>("/api/me/full", { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load profile." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

import { NextResponse } from "next/server";
import type { Plan } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const result = await apiFetch<Plan[]>("/api/subscription/plans");
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load plans." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

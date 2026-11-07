import { NextResponse } from "next/server";
import type { AnalyticsOverview } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const r = await apiFetch<AnalyticsOverview>("/api/analytics/overview", { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load analytics." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

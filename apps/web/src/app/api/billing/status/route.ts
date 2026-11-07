import { NextResponse } from "next/server";
import type { BillingStatusResponse } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const result = await apiFetch<BillingStatusResponse>("/api/billing/status", {
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load billing status." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

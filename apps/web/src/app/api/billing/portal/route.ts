import { NextResponse } from "next/server";
import type { CheckoutSession } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST() {
  const result = await apiFetch<CheckoutSession>("/api/billing/portal", {
    method: "POST",
    auth: true,
  });

  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Could not open the billing portal." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

import { NextResponse } from "next/server";
import type { CheckoutSession, StartCheckoutRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(req: Request) {
  // Only the tier and interval cross this boundary. Amounts are resolved
  // server-side from Stripe price ids, so a tampered body can at worst pick a
  // different published plan — never a different price.
  const body = (await req.json()) as StartCheckoutRequest;

  const result = await apiFetch<CheckoutSession>("/api/billing/checkout", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });

  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Could not start checkout." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

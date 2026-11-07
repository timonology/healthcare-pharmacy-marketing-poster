import { NextResponse } from "next/server";
import type { BuyCreditsRequest, CheckoutSession } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(req: Request) {
  const body = (await req.json()) as BuyCreditsRequest;

  const result = await apiFetch<CheckoutSession>("/api/billing/credits", {
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

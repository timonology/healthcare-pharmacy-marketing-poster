import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/** Sends a fresh verification link to the signed-in account. */
export async function POST() {
  const result = await apiFetch<{ message?: string }>("/api/auth/resend-verification", {
    method: "POST",
    auth: true,
  });

  return NextResponse.json(
    { message: result.data?.message ?? "If the address still needs confirming, a link is on its way." },
    { status: result.ok ? 202 : result.status || 400 },
  );
}

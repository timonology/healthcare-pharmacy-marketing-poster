import { NextResponse } from "next/server";
import type { Me } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import { setOnboardedCookie } from "@/lib/server/cookies";

/**
 * BFF proxy for GET /api/me — proxies to the .NET API and also refreshes
 * the `acme_onboarded` cookie based on truth. This is what breaks the
 * classic "middleware says onboarded, /api/me says no → redirect loop":
 * any time the client hits /api/me, the public onboarding cookie gets
 * re-synced to the DB value.
 */
export async function GET() {
  const r = await apiFetch<Me>("/api/me", { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load profile." },
      { status: r.status || 500 },
    );
  }
  const response = NextResponse.json(r.data);
  // Sync the public onboarded cookie so middleware and app agree.
  setOnboardedCookie(response, r.data.profile.onboardingCompleted === true);
  return response;
}

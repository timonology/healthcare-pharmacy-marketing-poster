import { NextResponse } from "next/server";
import type { Me } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import { setOnboardedCookie } from "@/lib/server/cookies";

/**
 * BFF proxy for GET /api/me/full — and the one place the app loads a profile.
 *
 * <b>This must re-sync `acme_onboarded`.</b> The sibling `/api/me` route does
 * so and explains that the sync is what prevents middleware and the app from
 * disagreeing — but nothing in the app calls `/api/me`. `api.ts` loads the
 * profile here and only here, so that sync never ran, and the cookie drifted
 * from the truth with nothing to correct it.
 *
 * What that looked like: an account whose setup genuinely was not finished saw
 * the "finish setup" prompt (which reads the API), clicked it, and was bounced
 * to the dashboard by middleware (which reads the cookie). Switching pharmacy
 * produced the same split, because the effective pharmacy changes while a
 * cookie describing the previous one stays put.
 *
 * Every page load passes through here, so this is also what heals a cookie
 * that went stale for any other reason.
 */
export async function GET() {
  const r = await apiFetch<Me>("/api/me/full", { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load profile." },
      { status: r.status || 500 },
    );
  }

  const response = NextResponse.json(r.data);
  setOnboardedCookie(response, r.data.profile.onboardingCompleted === true);
  return response;
}

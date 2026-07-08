import { NextResponse } from "next/server";
import type { Me, OnboardRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import { setOnboardedCookie } from "@/lib/server/cookies";

export async function POST(req: Request) {
  const body = (await req.json()) as OnboardRequest;
  const r = await apiFetch<Me>("/api/me/onboard", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Onboarding failed." },
      { status: r.status || 400 },
    );
  }
  const response = NextResponse.json(r.data);
  if (r.data.profile.onboardingCompleted) {
    setOnboardedCookie(response, true);
  }
  return response;
}

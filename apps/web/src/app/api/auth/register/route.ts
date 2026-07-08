import { NextResponse } from "next/server";
import type { AuthResponse, RegisterRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import { applyAuthCookies } from "@/lib/server/cookies";

export async function POST(req: Request) {
  const body = (await req.json()) as RegisterRequest;
  const result = await apiFetch<AuthResponse>("/api/auth/register", {
    method: "POST",
    body: JSON.stringify(body),
  });

  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Registration failed." },
      { status: result.status || 400 },
    );
  }

  const response = NextResponse.json({ user: result.data.user }, { status: 201 });
  return applyAuthCookies(response, {
    ...result.data,
    onboardingCompleted: result.data.user.onboardingCompleted,
  });
}

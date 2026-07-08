import { NextResponse } from "next/server";
import type { AuthResponse, LoginRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import { applyAuthCookies } from "@/lib/server/cookies";

export async function POST(req: Request) {
  const body = (await req.json()) as LoginRequest;
  const result = await apiFetch<AuthResponse>("/api/auth/login", {
    method: "POST",
    body: JSON.stringify(body),
  });

  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Login failed." },
      { status: result.status || 401 },
    );
  }

  const response = NextResponse.json({ user: result.data.user });
  return applyAuthCookies(response, {
    ...result.data,
    onboardingCompleted: result.data.user.onboardingCompleted,
  });
}

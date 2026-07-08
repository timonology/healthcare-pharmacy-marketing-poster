import { NextResponse } from "next/server";
import type { AuthResponse } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import {
  applyAuthCookies,
  clearAuthCookies,
  readRefreshToken,
} from "@/lib/server/cookies";

export async function POST() {
  const refreshToken = readRefreshToken();
  if (!refreshToken) {
    return NextResponse.json({ error: "No session." }, { status: 401 });
  }

  const result = await apiFetch<AuthResponse>("/api/auth/refresh", {
    method: "POST",
    body: JSON.stringify({ refreshToken }),
  });

  if (!result.ok || !result.data) {
    const failure = NextResponse.json(
      { error: result.error ?? "Refresh failed." },
      { status: result.status || 401 },
    );
    return clearAuthCookies(failure);
  }

  const response = NextResponse.json({ user: result.data.user });
  return applyAuthCookies(response, {
    ...result.data,
    onboardingCompleted: result.data.user.onboardingCompleted,
  });
}

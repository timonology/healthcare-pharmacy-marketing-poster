import { cookies } from "next/headers";
import type { NextResponse } from "next/server";

export const ACCESS_COOKIE = "acme_access";
export const REFRESH_COOKIE = "acme_refresh";
/** Public (non-httpOnly) marker so middleware can route past onboarding without a DB hit. */
export const ONBOARDED_COOKIE = "acme_onboarded";

// Cookies are marked Secure (HTTPS-only) when COOKIE_SECURE=true. Default
// behaviour: secure cookies on whenever NODE_ENV is "production" UNLESS
// COOKIE_SECURE is explicitly set to "false" (useful for local docker where
// `next start` forces NODE_ENV=production but the server is on plain HTTP).
const cookieSecure = process.env.COOKIE_SECURE === undefined
  ? process.env.NODE_ENV === "production"
  : process.env.COOKIE_SECURE === "true";

interface SetTokensInput {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
  onboardingCompleted?: boolean;
}

export function applyAuthCookies(
  response: NextResponse,
  input: SetTokensInput,
): NextResponse {
  response.cookies.set(ACCESS_COOKIE, input.accessToken, {
    httpOnly: true,
    sameSite: "lax",
    secure: cookieSecure,
    path: "/",
    expires: new Date(input.accessTokenExpiresAtUtc),
  });

  response.cookies.set(REFRESH_COOKIE, input.refreshToken, {
    httpOnly: true,
    sameSite: "lax",
    secure: cookieSecure,
    path: "/api/auth",
    expires: new Date(input.refreshTokenExpiresAtUtc),
  });

  if (input.onboardingCompleted !== undefined) {
    setOnboardedCookie(response, input.onboardingCompleted, new Date(input.refreshTokenExpiresAtUtc));
  }

  return response;
}

export function setOnboardedCookie(
  response: NextResponse,
  completed: boolean,
  expires?: Date,
): NextResponse {
  response.cookies.set(ONBOARDED_COOKIE, completed ? "1" : "0", {
    httpOnly: false,
    sameSite: "lax",
    secure: cookieSecure,
    path: "/",
    expires: expires ?? new Date(Date.now() + 1000 * 60 * 60 * 24 * 30),
  });
  return response;
}

export function clearAuthCookies(response: NextResponse): NextResponse {
  response.cookies.set(ACCESS_COOKIE, "", { path: "/", expires: new Date(0) });
  response.cookies.set(REFRESH_COOKIE, "", { path: "/api/auth", expires: new Date(0) });
  response.cookies.set(ONBOARDED_COOKIE, "", { path: "/", expires: new Date(0) });
  return response;
}

export function readAccessToken(): string | null {
  return cookies().get(ACCESS_COOKIE)?.value ?? null;
}

export function readRefreshToken(): string | null {
  return cookies().get(REFRESH_COOKIE)?.value ?? null;
}

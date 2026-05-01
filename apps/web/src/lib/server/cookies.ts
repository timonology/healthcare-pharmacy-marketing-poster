import { cookies } from "next/headers";
import type { NextResponse } from "next/server";

export const ACCESS_COOKIE = "acme_access";
export const REFRESH_COOKIE = "acme_refresh";

// Cookies are marked Secure (HTTPS-only) when COOKIE_SECURE=true.
// Tying this to NODE_ENV breaks local docker since `next start` forces
// NODE_ENV=production but localhost is served over plain HTTP.
const cookieSecure = process.env.COOKIE_SECURE === "true";

interface SetTokensInput {
  accessToken: string;
  accessTokenExpiresAtUtc: string;
  refreshToken: string;
  refreshTokenExpiresAtUtc: string;
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

  return response;
}

export function clearAuthCookies(response: NextResponse): NextResponse {
  response.cookies.set(ACCESS_COOKIE, "", { path: "/", expires: new Date(0) });
  response.cookies.set(REFRESH_COOKIE, "", { path: "/api/auth", expires: new Date(0) });
  return response;
}

export function readAccessToken(): string | null {
  return cookies().get(ACCESS_COOKIE)?.value ?? null;
}

export function readRefreshToken(): string | null {
  return cookies().get(REFRESH_COOKIE)?.value ?? null;
}

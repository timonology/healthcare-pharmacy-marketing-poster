import { cookies } from "next/headers";
import type { NextResponse } from "next/server";

export const ACCESS_COOKIE = "acme_access";
export const REFRESH_COOKIE = "acme_refresh";
/** Public (non-httpOnly) marker so middleware can route past onboarding without a DB hit. */
export const ONBOARDED_COOKIE = "acme_onboarded";
/** Set when someone chooses "I'll do this later" on onboarding. */
export const ONBOARDING_SKIPPED_COOKIE = "acme_onboarding_skipped";

/**
 * Short-lived delegated token, set while a parent pharmacy is managing a child.
 *
 * httpOnly like the others — the token grants access to another account, so
 * client JavaScript must never be able to read, copy or persist it. Kept in its
 * own cookie rather than overwriting the session so that leaving "manage as" is
 * a delete rather than a re-login.
 */
export const MANAGE_AS_COOKIE = "acme_manage_as";

/** Non-httpOnly companion so the UI can render the banner. Name only, no token. */
export const MANAGE_AS_LABEL_COOKIE = "acme_manage_as_name";

/**
 * Which pharmacy the person chose to work as.
 *
 * The id only — never a credential. It outlives the delegated token in
 * MANAGE_AS_COOKIE, which is capped at an hour and cannot refresh itself, and
 * is what lets a refresh re-issue a scoped token instead of silently dropping
 * somebody back into their own pharmacy mid-task.
 *
 * <b>It is not trusted.</b> Every refresh presents it to /api/workspaces/switch,
 * which resolves it against a membership row before minting anything. Tampering
 * with it gets a 403, not another pharmacy — which is also why it can be a
 * plain id rather than something signed.
 */
export const WORKSPACE_COOKIE = "acme_workspace";

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

/**
 * "I'll do this later" — remembered only until they sign out.
 *
 * Deliberately a session cookie with no expiry date, because that is exactly
 * the required behaviour: skipping lets somebody get on with their day, and
 * the next sign-in asks again. It is cleared on sign-out too, so the answer
 * cannot outlive the person who gave it on a shared dispensary computer.
 *
 * It records a *choice*, never a fact. Whether setup is actually finished is
 * the API's `onboardingCompleted`, and this cookie can never stand in for it.
 */
export function setOnboardingSkippedCookie(response: NextResponse): NextResponse {
  response.cookies.set(ONBOARDING_SKIPPED_COOKIE, "1", {
    httpOnly: true,
    sameSite: "lax",
    secure: cookieSecure,
    path: "/",
    // No maxAge and no expires: dies with the browser session.
  });
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
  // Without this, "later" would quietly mean "never" for anyone who signs
  // back in on the same browser — which is the one thing the skip must not
  // become.
  response.cookies.set(ONBOARDING_SKIPPED_COOKIE, "", { path: "/", expires: new Date(0) });

  // The delegated token too, and this is not tidiness.
  //
  // readAccessToken() prefers MANAGE_AS_COOKIE over ACCESS_COOKIE, so leaving
  // it behind meant signing out while managing another pharmacy did not sign
  // you out at all: every subsequent request still carried a valid token for
  // that pharmacy, for up to an hour. Revoking refresh tokens server-side does
  // not help — a delegated token is stateless and carries no refresh.
  response.cookies.set(MANAGE_AS_COOKIE, "", { path: "/", expires: new Date(0) });
  response.cookies.set(WORKSPACE_COOKIE, "", { path: "/", expires: new Date(0) });
  response.cookies.set(MANAGE_AS_LABEL_COOKIE, "", { path: "/", expires: new Date(0) });
  return response;
}

/**
 * The token to send upstream.
 *
 * The delegated token wins when present. That single line is what makes "manage
 * as" work across the entire app without touching a single endpoint — every
 * proxied call carries the child's subject for as long as the cookie lives, and
 * reverting is a cookie delete.
 */
export async function readAccessToken(): Promise<string | null> {
  const jar = await cookies();
  return jar.get(MANAGE_AS_COOKIE)?.value ?? jar.get(ACCESS_COOKIE)?.value ?? null;
}

/** True when the caller is currently acting as another pharmacy. */
export async function isManagingAnotherPharmacy(): Promise<boolean> {
  return (await cookies()).get(MANAGE_AS_COOKIE)?.value != null;
}

export async function readRefreshToken(): Promise<string | null> {
  return (await cookies()).get(REFRESH_COOKIE)?.value ?? null;
}

/** The pharmacy the person last chose to work as, if any. */
export async function readWorkspaceId(): Promise<string | null> {
  return (await cookies()).get(WORKSPACE_COOKIE)?.value ?? null;
}

/**
 * Records a switch: the scoped token, the chosen pharmacy, and its name.
 *
 * The token cookie expires with the token so a stale cookie cannot produce
 * requests that 401 with no explanation. The workspace id deliberately lives
 * longer — it is the thing a refresh uses to rebuild the token.
 */
export function applyWorkspaceCookies(
  response: NextResponse,
  input: { token: string; expiresAtUtc: string; pharmacyId: string; pharmacyName: string },
): NextResponse {
  const seconds = Math.max(
    60,
    Math.floor((new Date(input.expiresAtUtc).getTime() - Date.now()) / 1000),
  );

  response.cookies.set(MANAGE_AS_COOKIE, input.token, {
    httpOnly: true,
    sameSite: "lax",
    secure: cookieSecure,
    path: "/",
    maxAge: seconds,
  });

  // Outlives the token on purpose: a refresh reads this to re-issue one.
  response.cookies.set(WORKSPACE_COOKIE, input.pharmacyId, {
    httpOnly: true,
    sameSite: "lax",
    secure: cookieSecure,
    path: "/",
    maxAge: 60 * 60 * 24 * 30,
  });

  // Readable, because the header chip renders it. A name, never a token.
  //
  // Same lifetime as the token, and that is not tidiness. This label is the
  // only thing the banner has to describe the session it labels; given thirty
  // days it outlived that session by weeks and announced "you are managing
  // New Home" to people who had signed out. A label for a thing that no longer
  // exists is worse than no label. Unlike WORKSPACE_COOKIE above, nothing
  // rebuilds anything from this — so there is no reason for it to survive.
  response.cookies.set(MANAGE_AS_LABEL_COOKIE, input.pharmacyName, {
    httpOnly: false,
    sameSite: "lax",
    secure: cookieSecure,
    path: "/",
    maxAge: seconds,
  });

  return response;
}

/** Returns to the person's own pharmacy. */
export function clearWorkspaceCookies(response: NextResponse): NextResponse {
  response.cookies.set(MANAGE_AS_COOKIE, "", { path: "/", maxAge: 0 });
  response.cookies.set(WORKSPACE_COOKIE, "", { path: "/", maxAge: 0 });
  response.cookies.set(MANAGE_AS_LABEL_COOKIE, "", { path: "/", maxAge: 0 });
  return response;
}

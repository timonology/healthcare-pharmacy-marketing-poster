import { NextResponse } from "next/server";
import type { AuthResponse } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import {
  applyAuthCookies,
  applyWorkspaceCookies,
  clearAuthCookies,
  clearWorkspaceCookies,
  readRefreshToken,
  readWorkspaceId,
} from "@/lib/server/cookies";

interface SwitchResponse {
  token: string;
  expiresAtUtc: string;
  pharmacyId: string;
  pharmacyName: string;
}

export async function POST() {
  const refreshToken = (await readRefreshToken());
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
  applyAuthCookies(response, {
    ...result.data,
    onboardingCompleted: result.data.user.onboardingCompleted,
  });

  return reapplyWorkspace(response, result.data);
}

/**
 * Re-issues the scoped token for whichever pharmacy the person was working as.
 *
 * Without this the feature quietly breaks after an hour. A scoped token is a
 * delegated token: capped at sixty minutes and carrying no refresh of its own.
 * When it lapses, `readAccessToken` falls back to the session token and the
 * person is silently returned to their own pharmacy — mid-task, with no
 * message, and quite possibly mid-campaign.
 *
 * <b>The re-issue is a real authorisation check, not a renewal.</b> The stored
 * pharmacy id is presented to /api/workspaces/switch exactly as the browser
 * would present it, and the API resolves it against a membership row before
 * minting anything. So revoking somebody's membership ends their access at the
 * next refresh rather than whenever they next choose to switch, and a tampered
 * cookie gets a 403.
 *
 * A failure here is not a failed refresh. The session is valid; only the
 * elevated context is gone, so the cookies are cleared and the person lands
 * back in their own pharmacy — which is the safe direction to fail.
 */
async function reapplyWorkspace(
  response: NextResponse,
  session: AuthResponse,
): Promise<NextResponse> {
  const pharmacyId = (await readWorkspaceId());

  // Nothing to restore, or it is already their own pharmacy.
  if (!pharmacyId || pharmacyId === session.user.id) return response;

  const switched = await apiFetch<SwitchResponse>("/api/workspaces/switch", {
    method: "POST",
    // The freshly minted access token, not the cookie: the cookie still holds
    // the token we are replacing, and on this path it may already have expired.
    headers: { Authorization: `Bearer ${session.accessToken}` },
    body: JSON.stringify({ pharmacyId }),
  });

  if (!switched.ok || !switched.data) {
    return clearWorkspaceCookies(response);
  }

  return applyWorkspaceCookies(response, switched.data);
}

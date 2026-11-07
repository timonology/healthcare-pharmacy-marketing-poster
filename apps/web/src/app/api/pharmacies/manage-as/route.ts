import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";
import {
  MANAGE_AS_COOKIE,
  MANAGE_AS_LABEL_COOKIE,
} from "@/lib/server/cookies";

/**
 * Starts and ends a "manage as" session.
 *
 * The delegated token never reaches the browser as a value — it is exchanged
 * here and written straight into an httpOnly cookie. The response carries only
 * the child's name, which is all the banner needs.
 */
interface ManageAsResponse {
  token: string;
  expiresAtUtc: string;
  childUserId: string;
  childPharmacyName: string;
}

export async function POST(req: Request) {
  const { childId } = (await req.json().catch(() => ({}))) as { childId?: string };
  if (!childId) {
    return NextResponse.json({ error: "A branch is required." }, { status: 400 });
  }

  const result = await apiFetch<ManageAsResponse>(
    `/api/pharmacies/children/${childId}/manage-as`,
    { method: "POST", auth: true },
  );

  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Could not start managing that pharmacy." },
      { status: result.status || 400 },
    );
  }

  const { token, expiresAtUtc, childPharmacyName, childUserId } = result.data;

  const response = NextResponse.json({ childUserId, childPharmacyName, expiresAtUtc });

  // maxAge from the token's own expiry, so the cookie cannot outlive the
  // credential inside it. A cookie that survives its token produces requests
  // that 401 with no explanation.
  const seconds = Math.max(
    60,
    Math.floor((new Date(expiresAtUtc).getTime() - Date.now()) / 1000),
  );

  response.cookies.set(MANAGE_AS_COOKIE, token, {
    httpOnly: true,
    sameSite: "lax",
    secure: process.env.COOKIE_SECURE === undefined
      ? process.env.NODE_ENV === "production"
      : process.env.COOKIE_SECURE === "true",
    path: "/",
    maxAge: seconds,
  });

  // Readable by the client so the banner can name the pharmacy. Deliberately
  // the name only — never the token.
  response.cookies.set(MANAGE_AS_LABEL_COOKIE, childPharmacyName, {
    httpOnly: false,
    sameSite: "lax",
    path: "/",
    maxAge: seconds,
  });

  return response;
}

/** Ends the session by dropping the cookies. The parent's own session is untouched. */
export async function DELETE() {
  const response = NextResponse.json({ ended: true });
  response.cookies.set(MANAGE_AS_COOKIE, "", { path: "/", maxAge: 0 });
  response.cookies.set(MANAGE_AS_LABEL_COOKIE, "", { path: "/", maxAge: 0 });
  return response;
}

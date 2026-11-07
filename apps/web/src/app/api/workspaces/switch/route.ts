import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";
import {
  applyWorkspaceCookies,
  clearWorkspaceCookies,
  readWorkspaceId,
} from "@/lib/server/cookies";

interface SwitchResponse {
  token: string;
  expiresAtUtc: string;
  pharmacyId: string;
  pharmacyName: string;
}

/**
 * Starts working as a pharmacy.
 *
 * The scoped token never reaches the browser as a value — it is exchanged here
 * and written straight into an httpOnly cookie, the same way manage-as already
 * works. The response carries only the name, which is all the header chip needs.
 *
 * Switching back to your own pharmacy goes through the same call. The API
 * treats it as a switch like any other, so there is no second path here that
 * could drift.
 */
export async function POST(req: Request) {
  const { pharmacyId } = (await req.json().catch(() => ({}))) as { pharmacyId?: string };

  if (!pharmacyId) {
    return NextResponse.json({ error: "Choose a pharmacy." }, { status: 400 });
  }

  const result = await apiFetch<SwitchResponse>("/api/workspaces/switch", {
    method: "POST",
    auth: true,
    body: JSON.stringify({ pharmacyId }),
  });

  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Could not switch pharmacy." },
      { status: result.status || 400 },
    );
  }

  const { pharmacyId: id, pharmacyName } = result.data;
  const response = NextResponse.json({ pharmacyId: id, pharmacyName });

  return applyWorkspaceCookies(response, result.data);
}

/**
 * Returns to your own pharmacy by dropping the scoped token.
 *
 * A delete rather than another switch, so leaving works even if the API is
 * unreachable — being stuck inside another pharmacy with no way out is a worse
 * failure than a brief error.
 */
export async function DELETE() {
  const wasIn = (await readWorkspaceId());

  // Close the audit window before dropping the token — afterwards the request
  // would carry the session token and the API could not tell which pharmacy
  // was being left. Best-effort: leaving must not depend on it.
  if (wasIn) {
    await apiFetch("/api/workspaces/stop", { method: "POST", auth: true }).catch(() => null);
  }

  const response = NextResponse.json({ left: wasIn });
  return clearWorkspaceCookies(response);
}

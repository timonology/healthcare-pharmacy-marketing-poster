import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";
import { clearAuthCookies } from "@/lib/server/cookies";

export async function POST() {
  // Best-effort: revoke refresh tokens server-side, but always clear cookies.
  await apiFetch("/api/auth/logout", { method: "POST", auth: true });
  const response = NextResponse.json({ ok: true });
  return clearAuthCookies(response);
}

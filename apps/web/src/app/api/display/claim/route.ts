import { NextResponse } from "next/server";
import { API_BASE } from "@/lib/server/api-proxy";
import { forwardingHeaders } from "@/lib/server/forwarding";

/**
 * Pairing, for a device that has no account and never will.
 *
 * Deliberately its own route rather than part of an authed catch-all. Every
 * other proxy in this app attaches the caller's access cookie; a kiosk has
 * none, and if this went through one of those it would either forward a
 * passing pharmacist's session or refuse the device outright.
 *
 * Nothing here reads a cookie, and nothing here sets one. The device's only
 * credential is the token in the response body, which it keeps in local
 * storage — a cookie would be sent on every request to the whole origin, which
 * is a much larger blast radius than a header the kiosk chooses to attach.
 */
export async function POST(req: Request) {
  const body = await req.text();

  const res = await fetch(`${API_BASE}/api/display/claim`, {
    method: "POST",
    headers: { ...(await forwardingHeaders()), "Content-Type": "application/json" },
    body,
    cache: "no-store",
  });

  const text = await res.text();

  return new NextResponse(text, {
    status: res.status,
    headers: { "Content-Type": "application/json" },
  });
}

import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/** What this person has asked for, and how it went. */
export async function GET() {
  const result = await apiFetch<unknown>("/api/workspaces/access-requests", { auth: true });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Could not load your requests." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

/**
 * Asks for access to a pharmacy.
 *
 * Passes the API's answer through unchanged, including its deliberate
 * vagueness: it reports the same thing whether or not the address has an
 * account, so this cannot be used to discover which pharmacies are registered.
 * Do not "improve" this by reporting a 404.
 */
export async function POST(req: Request) {
  const body = (await req.json().catch(() => ({}))) as {
    pharmacyEmail?: string;
    reason?: string;
  };

  const result = await apiFetch<{ message?: string }>("/api/workspaces/access-requests", {
    method: "POST",
    auth: true,
    body: JSON.stringify({
      pharmacyEmail: body.pharmacyEmail ?? "",
      reason: body.reason ?? "",
    }),
  });

  // A complaint about the reason is the person's own input and safe to show.
  // Anything else is flattened into the uniform acknowledgement.
  if (result.status === 400) {
    return NextResponse.json(
      { error: result.error ?? "Please check the details and try again." },
      { status: 400 },
    );
  }

  return NextResponse.json(
    {
      message:
        result.data?.message ??
        "Request submitted. A Marketing25 administrator will review it.",
    },
    { status: 202 },
  );
}

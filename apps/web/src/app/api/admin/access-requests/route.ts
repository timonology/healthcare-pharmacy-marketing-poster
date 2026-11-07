import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/** The review queue. */
export async function GET(req: Request) {
  // Read the two supported filters explicitly rather than forwarding the whole
  // query string. A nested template here (`...${q ? `?${q}` : ""}`) also
  // defeated the contract check's path extraction, which reads the first
  // quoted literal and stopped at the inner backtick.
  const url = new URL(req.url);
  const pendingOnly = url.searchParams.get("pendingOnly") ?? "true";
  const take = url.searchParams.get("take") ?? "50";

  const result = await apiFetch<unknown>(
    `/api/admin/access-requests?pendingOnly=${pendingOnly}&take=${take}`,
    { auth: true },
  );

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Could not load access requests." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * The pharmacies the signed-in person may work as.
 *
 * Sends whichever token is current — delegated if they have already switched —
 * because the API reads the actor claim to answer this. That is what keeps the
 * way back to their own pharmacy visible while they are in another one.
 */
export async function GET() {
  const result = await apiFetch<unknown>("/api/workspaces", { auth: true });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Could not load your pharmacies." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Whether the API can tell this browser apart from every other one.
 *
 * Must be asked through this server, not the API directly: the question is
 * whether the address this server signs and forwards is accepted. See the
 * API's ClientAddressController. Used by `pnpm smoke`.
 */
export const dynamic = "force-dynamic";

export async function GET() {
  const result = await apiFetch<Record<string, unknown>>("/api/diagnostics/client-address", { auth: true });
  return result.ok
    ? NextResponse.json(result.data)
    : NextResponse.json({ error: result.error ?? "Unavailable." }, { status: result.status || 502 });
}

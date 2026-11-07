import { NextResponse } from "next/server";
import type { StockPhoto } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Stock photo search for a pharmacy's AI design.
 *
 * The API rate-limits this per person (the stock key is shared by every
 * pharmacy), so a 429 is passed through with its sentence rather than turned
 * into a generic failure.
 */
export async function GET(req: Request) {
  const search = new URL(req.url).search;
  const r = await apiFetch<StockPhoto[]>(`/api/posters/stock-photos${search}`, { auth: true });
  if (!r.ok) {
    return NextResponse.json({ error: r.error ?? "Search failed." }, { status: r.status || 500 });
  }
  return NextResponse.json(r.data ?? []);
}

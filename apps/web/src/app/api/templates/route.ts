import { NextResponse } from "next/server";
import type { PagedResponse, TemplateSummary } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(req: Request) {
  const url = new URL(req.url);
  const qs = url.search; // forward all query params verbatim

  // Anonymous on the API — guests can browse templates without signing in.
  const result = await apiFetch<PagedResponse<TemplateSummary>>(
    `/api/templates${qs}`,
  );

  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load templates." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

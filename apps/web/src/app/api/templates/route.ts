import { NextResponse } from "next/server";
import type { PagedResponse, Template, TemplateSummary } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Publishing a template can carry a large canvas — a full-bleed design with an
 * embedded image description and 20+ shapes — so give it room beyond the
 * default serverless ceiling.
 */
export const maxDuration = 60;

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

/**
 * Publish a template to the shared gallery.
 *
 * Authenticated, unlike GET above — the API gates this behind the AdminArea
 * policy, and without `auth: true` the token never reaches it, so a staff user
 * would get a 401 that looks like a permissions bug.
 */
export async function POST(req: Request) {
  const body = await req.text();

  const result = await apiFetch<Template>("/api/templates", {
    method: "POST",
    body,
    auth: true,
  });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Failed to publish template." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

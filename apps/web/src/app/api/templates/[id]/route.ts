import { NextResponse } from "next/server";
import type { Template } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  // Public — guests can preview templates before deciding to sign up. The
  // token goes along when there is one, so a signed-in view is attributed.
  const result = await apiFetch<Template>(`/api/templates/${id}`, { auth: "optional" });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load template." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

/** Update a template. Staff only — enforced by the API's AdminArea policy. */
export async function PUT(
  req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const body = await req.text();

  const result = await apiFetch<Template>(`/api/templates/${id}`, {
    method: "PUT",
    body,
    auth: true,
  });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Failed to update template." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

/** Delete a template. Staff only. */
export async function DELETE(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;

  const result = await apiFetch<unknown>(`/api/templates/${id}`, {
    method: "DELETE",
    auth: true,
  });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Failed to delete template." },
      { status: result.status || 400 },
    );
  }

  // The API answers 204 No Content, so there is no body to echo. Return a JSON
  // literal rather than an empty 204 — api.ts calls res.ok and then parses,
  // and an empty body would throw on parse.
  return NextResponse.json(true);
}

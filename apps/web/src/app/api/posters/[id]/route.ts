import { NextResponse } from "next/server";
import type { Poster, UpdatePosterRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const result = await apiFetch<Poster>(`/api/posters/${id}`, { auth: true });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load poster." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

export async function PUT(
  req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const body = (await req.json()) as UpdatePosterRequest;
  const result = await apiFetch<Poster>(`/api/posters/${id}`, {
    method: "PUT",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to save poster." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

export async function DELETE(
  _req: Request,
  ctx: { params: Promise<{ id: string }> },
) {
  const { id } = await ctx.params;
  const result = await apiFetch<unknown>(`/api/posters/${id}`, {
    method: "DELETE",
    auth: true,
  });
  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Failed to delete poster." },
      { status: result.status || 400 },
    );
  }
  return new NextResponse(null, { status: 204 });
}

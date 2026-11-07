import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Catch-all proxy for the pharmacy hierarchy API.
 *
 * Same reasoning as the admin proxy: a dozen endpoints that all attach the
 * caller's token and forward. A route file per endpoint is a lot of identical
 * code and an easy place to forget `auth: true` on one of them.
 *
 * Nothing is granted here that the browser did not already have — the token
 * comes from the httpOnly cookie and the .NET API re-checks ownership on every
 * request. Platform credentials never pass through this layer in either
 * direction.
 */
export const maxDuration = 60;

// Next 15+: params is a Promise.
type Ctx = { params: Promise<{ path?: string[] }> };

async function targetPath(req: Request, ctx: Ctx): Promise<string> {
  const segments = ((await ctx.params).path ?? []).join("/");
  const search = new URL(req.url).search;
  return `/api/pharmacies/${segments}${search}`;
}

async function forward(req: Request, ctx: Ctx, method: string) {
  const hasBody = method !== "GET" && method !== "DELETE";
  let body: string | undefined;

  if (hasBody) {
    const raw = await req.text();
    body = raw.length > 0 ? raw : undefined;
  }

  const result = await apiFetch<unknown>(await targetPath(req, ctx), {
    method,
    body,
    auth: true,
  });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Request failed." },
      { status: result.status || 400 },
    );
  }

  // A GET keeps whatever the API said, including `null` — a resource that
  // legitimately has no value must not be turned into one. Only methods that
  // routinely answer with no content at all (204 from a delete or a fire-and-
  // forget write) get a truthy stand-in, because their callers check for
  // success rather than reading a value.
  if (result.data === null || result.data === undefined) {
    return method === "GET"
      ? NextResponse.json(null)
      : NextResponse.json(true);
  }

  return NextResponse.json(result.data);
}

export const GET = (req: Request, ctx: Ctx) => forward(req, ctx, "GET");
export const POST = (req: Request, ctx: Ctx) => forward(req, ctx, "POST");
export const PUT = (req: Request, ctx: Ctx) => forward(req, ctx, "PUT");
export const PATCH = (req: Request, ctx: Ctx) => forward(req, ctx, "PATCH");
export const DELETE = (req: Request, ctx: Ctx) => forward(req, ctx, "DELETE");

import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Catch-all proxy for the admin API.
 *
 * The admin surface is a dozen-plus endpoints that all do the same thing here:
 * attach the caller's access token and forward. Writing a route file per
 * endpoint would be a lot of identical code and an easy place to forget
 * `auth: true` on one of them — which would turn that endpoint into an
 * unauthenticated passthrough.
 *
 * This forwards nothing the browser didn't already have: the token comes from
 * the httpOnly cookie, and the .NET API independently enforces role and scope
 * on every request. This proxy grants no authority of its own.
 */

/**
 * Template generation runs a large model with up to two repair rounds, which
 * can exceed the default serverless function ceiling. This is an upper bound,
 * not a delay — every other admin call still returns in milliseconds.
 */
export const maxDuration = 300;

// Next 15+: params is a Promise.
type Ctx = { params: Promise<{ path?: string[] }> };

async function targetPath(req: Request, ctx: Ctx): Promise<string> {
  const segments = ((await ctx.params).path ?? []).join("/");
  const search = new URL(req.url).search;
  return `/api/admin/${segments}${search}`;
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

  // Some admin actions return no content; normalise those to `true` so the
  // client can await a JSON response uniformly.
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

// Every method the admin API uses must be exported here. Next returns 405 for
// one that is missing, and the request never reaches the API at all — so the
// failure looks like a broken endpoint rather than a missing route handler.
// This has bitten the project once already, on Publish.
export const GET = (req: Request, ctx: Ctx) => forward(req, ctx, "GET");
export const POST = (req: Request, ctx: Ctx) => forward(req, ctx, "POST");
export const PUT = (req: Request, ctx: Ctx) => forward(req, ctx, "PUT");
export const PATCH = (req: Request, ctx: Ctx) => forward(req, ctx, "PATCH");
export const DELETE = (req: Request, ctx: Ctx) => forward(req, ctx, "DELETE");

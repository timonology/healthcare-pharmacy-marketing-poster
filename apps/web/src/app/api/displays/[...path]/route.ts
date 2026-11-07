import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Catch-all proxy for the screens and playlists API.
 *
 * The pharmacy-facing half only. Everything through here attaches the caller's
 * access token and is scoped server-side to whichever pharmacy they are working
 * as, so a head office managing a branch's screens needs nothing special.
 *
 * <b>The kiosk does not come this way.</b> `/api/display/*` — singular, and no
 * auth — is a separate set of routes for devices that have no session at all.
 * Keeping the two apart at the URL means a screen's token can never be mistaken
 * for a person's, and this proxy can attach a cookie unconditionally without
 * having to reason about whether the caller has one.
 *
 * JSON only, both directions. The one binary response in this feature is a
 * poster image, and that has its own route because a catch-all like this one
 * would re-serialise it into the word `null`.
 */
export const maxDuration = 60;

// Next 15+: params is a Promise.
type Ctx = { params: Promise<{ path?: string[] }> };

async function targetPath(req: Request, ctx: Ctx): Promise<string> {
  const segments = ((await ctx.params).path ?? []).join("/");
  const search = new URL(req.url).search;
  return `/api/displays/${segments}${search}`;
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

  // Same rule as the other catch-alls: a GET keeps `null` because a resource
  // that genuinely has no value must not be turned into one; a write that
  // answers with no content gets a truthy stand-in, because its caller is
  // checking for success rather than reading a value.
  if (result.data === null || result.data === undefined) {
    return method === "GET" ? NextResponse.json(null) : NextResponse.json(true);
  }

  // Deliberately no caching. A pairing code has a four-hour life and a screen's
  // last-seen time is the one field somebody checks when a display goes dark;
  // both are worthless if a stale copy comes back.
  const res = NextResponse.json(result.data);
  res.headers.set("Cache-Control", "no-store");
  return res;
}

export const GET = (req: Request, ctx: Ctx) => forward(req, ctx, "GET");
export const POST = (req: Request, ctx: Ctx) => forward(req, ctx, "POST");
export const PUT = (req: Request, ctx: Ctx) => forward(req, ctx, "PUT");
export const DELETE = (req: Request, ctx: Ctx) => forward(req, ctx, "DELETE");

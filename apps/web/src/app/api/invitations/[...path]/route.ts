import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Public invitation endpoints — activation preview and accept.
 *
 * `auth: false`, deliberately and unusually. The person following an
 * activation link has no account yet, so there is no token to attach; the
 * invitation token in the request body or query IS the credential, and the API
 * validates it. This is the only proxy in the app that forwards without one.
 */
// Next 15+: params is a Promise.
type Ctx = { params: Promise<{ path?: string[] }> };

async function forward(req: Request, ctx: Ctx, method: string) {
  const segments = ((await ctx.params).path ?? []).join("/");
  const search = new URL(req.url).search;

  let body: string | undefined;
  if (method !== "GET") {
    const raw = await req.text();
    body = raw.length > 0 ? raw : undefined;
  }

  const result = await apiFetch<unknown>(
    `/api/invitations/${segments}${search}`,
    { method, body, auth: false },
  );

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

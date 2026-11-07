import { NextResponse } from "next/server";
import type { UnsubscribePreview } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Unsubscribing from a pharmacy's campaign emails (security review R2-06).
 *
 * Public: the person has no account, and the signed token from the email is
 * the only credential. GET describes; POST unsubscribes. The page never posts
 * on load — mail scanners open every link, and that must not unsubscribe
 * anyone — it waits for the button.
 */
function tokenFrom(req: Request): string {
  return new URL(req.url).searchParams.get("token") ?? "";
}

async function forward(req: Request, method: "GET" | "POST") {
  const token = tokenFrom(req);
  const result = await apiFetch<UnsubscribePreview>(
    `/api/unsubscribe?token=${encodeURIComponent(token)}`,
    { method },
  );
  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "This unsubscribe link is not valid." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data, {
    headers: { "Cache-Control": "no-store" },
  });
}

export const GET = (req: Request) => forward(req, "GET");
export const POST = (req: Request) => forward(req, "POST");

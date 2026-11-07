import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Starts a password reset.
 *
 * Passes the API's deliberate vagueness through unchanged: the same answer
 * whether or not the address has an account, so this route cannot be used to
 * find out which addresses are registered. Do not "improve" this by reporting
 * a 404.
 *
 * **Except a rate-limit refusal, which is passed on.** The limit is per
 * connection, not per account, so saying "too many requests" reveals nothing
 * about any address — and hiding it was harmful: the page said "a link is on
 * its way" while nothing was sent, and people retried, which kept them limited.
 * That is indistinguishable from email being broken, and is how it was reported.
 */
export async function POST(req: Request) {
  const body = (await req.json().catch(() => ({}))) as { email?: string };

  const result = await apiFetch<{ message?: string }>("/api/auth/forgot-password", {
    method: "POST",
    body: JSON.stringify({ email: body.email ?? "" }),
  });

  if (result.status === 429) {
    console.warn("[forgot-password] the API refused the request: rate limited");
    return NextResponse.json(
      {
        error:
          "Too many reset requests from this connection. Wait 15 minutes, then try again — " +
          "or use the link from the last email if it arrived.",
      },
      { status: 429 },
    );
  }

  if (!result.ok) {
    // Logged here because the person is told nothing different: a 5xx only
    // happens for addresses that reached the send path, so surfacing it would
    // leak exactly what the uniform response hides.
    console.warn(`[forgot-password] the API answered ${result.status}: ${result.error ?? "no detail"}`);
  }

  return NextResponse.json(
    { message: result.data?.message ?? "If that address has an account, a reset link is on its way." },
    { status: 202 },
  );
}

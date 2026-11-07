import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/** Confirms an email address from the emailed link. */
export async function POST(req: Request) {
  const body = (await req.json()) as { token?: string };

  const result = await apiFetch<unknown>("/api/auth/verify-email", {
    method: "POST",
    body: JSON.stringify({ token: body.token ?? "" }),
  });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Could not confirm the address." },
      { status: result.status || 400 },
    );
  }

  return new NextResponse(null, { status: 204 });
}

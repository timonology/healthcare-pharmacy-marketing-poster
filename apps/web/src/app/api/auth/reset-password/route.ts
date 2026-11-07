import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/** Completes a password reset. Success signs the account out everywhere. */
export async function POST(req: Request) {
  const body = (await req.json()) as { token?: string; password?: string };

  const result = await apiFetch<unknown>("/api/auth/reset-password", {
    method: "POST",
    body: JSON.stringify({ token: body.token ?? "", password: body.password ?? "" }),
  });

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Could not reset the password." },
      { status: result.status || 400 },
    );
  }

  return new NextResponse(null, { status: 204 });
}

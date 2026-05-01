import { NextResponse } from "next/server";
import type { UserProfile } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const result = await apiFetch<UserProfile>("/api/me", { auth: true });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Not authenticated." },
      { status: result.status || 401 },
    );
  }
  return NextResponse.json(result.data);
}

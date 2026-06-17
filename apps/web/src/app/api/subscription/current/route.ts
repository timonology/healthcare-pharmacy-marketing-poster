import { NextResponse } from "next/server";
import type { CurrentSubscription } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const result = await apiFetch<CurrentSubscription>("/api/subscription/current", {
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load subscription." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

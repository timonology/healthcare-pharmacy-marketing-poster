import { NextResponse } from "next/server";
import type { CurrentSubscription, UpgradeRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(req: Request) {
  const body = (await req.json()) as UpgradeRequest;
  const result = await apiFetch<CurrentSubscription>("/api/subscription/upgrade", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Upgrade failed." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

import { NextResponse } from "next/server";
import type { BulkAddToGroupRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(req: Request) {
  const body = (await req.json()) as BulkAddToGroupRequest;
  const r = await apiFetch<{ added: number }>("/api/patients/bulk-add-to-group", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to update group." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data);
}

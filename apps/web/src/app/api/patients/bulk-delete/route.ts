import { NextResponse } from "next/server";
import type { BulkDeleteRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(req: Request) {
  const body = (await req.json()) as BulkDeleteRequest;
  const r = await apiFetch<unknown>("/api/patients/bulk-delete", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok) {
    return NextResponse.json(
      { error: r.error ?? "Failed to delete patients." },
      { status: r.status || 400 },
    );
  }
  return new NextResponse(null, { status: 204 });
}

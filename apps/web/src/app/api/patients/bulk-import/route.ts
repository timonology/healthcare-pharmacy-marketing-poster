import { NextResponse } from "next/server";
import type { BulkImportRequest, BulkImportResult } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(req: Request) {
  const body = (await req.json()) as BulkImportRequest;
  const r = await apiFetch<BulkImportResult>("/api/patients/bulk-import", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to import patients." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data);
}

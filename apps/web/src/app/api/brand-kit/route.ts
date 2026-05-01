import { NextResponse } from "next/server";
import type { BrandKit, UpsertBrandKitRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const result = await apiFetch<BrandKit>("/api/brand-kit", { auth: true });
  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load brand kit." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

export async function PUT(req: Request) {
  const body = (await req.json()) as UpsertBrandKitRequest;
  const result = await apiFetch<BrandKit>("/api/brand-kit", {
    method: "PUT",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to save brand kit." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data);
}

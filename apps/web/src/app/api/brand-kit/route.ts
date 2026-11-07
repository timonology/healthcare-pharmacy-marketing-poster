import { NextResponse } from "next/server";
import type { BrandKit, UpsertBrandKitRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET() {
  const result = await apiFetch<BrandKit>("/api/brand-kit", { auth: true });

  // No kit yet is the normal state of every new pharmacy, not an error: the
  // page prefills from the profile instead. Answered as 204 so it is not a red
  // "Failed to load resource" in the console of everybody who opens Brand Kit
  // before saving it. The API keeps its 404; this is the browser's contract.
  if (result.status === 404) return new NextResponse(null, { status: 204 });

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

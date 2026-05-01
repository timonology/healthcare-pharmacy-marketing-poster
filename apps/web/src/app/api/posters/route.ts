import { NextResponse } from "next/server";
import type {
  CreatePosterRequest,
  PagedResponse,
  Poster,
  PosterSummary,
} from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function GET(req: Request) {
  const url = new URL(req.url);
  const result = await apiFetch<PagedResponse<PosterSummary>>(
    `/api/posters${url.search}`,
    { auth: true },
  );
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to load posters." },
      { status: result.status || 500 },
    );
  }
  return NextResponse.json(result.data);
}

export async function POST(req: Request) {
  const body = (await req.json()) as CreatePosterRequest;
  const result = await apiFetch<Poster>("/api/posters", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!result.ok || !result.data) {
    return NextResponse.json(
      { error: result.error ?? "Failed to create poster." },
      { status: result.status || 400 },
    );
  }
  return NextResponse.json(result.data, { status: 201 });
}

import { NextResponse } from "next/server";
import type { CreateVideoRequest, Video } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import { listQuery } from "@/lib/server/list-query";

export async function GET(req: Request) {
  const r = await apiFetch<Video[]>(`/api/videos${listQuery(req)}`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load videos." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

export async function POST(req: Request) {
  const body = (await req.json()) as CreateVideoRequest;
  const r = await apiFetch<Video>("/api/videos", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to queue video." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data, { status: 202 });
}

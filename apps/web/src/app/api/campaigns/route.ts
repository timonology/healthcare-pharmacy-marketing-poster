import { NextResponse } from "next/server";
import type { Campaign, CreateCampaignRequest } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";
import { listQuery } from "@/lib/server/list-query";

export async function GET(req: Request) {
  const r = await apiFetch<Campaign[]>(`/api/campaigns${listQuery(req)}`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to load campaigns." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

export async function POST(req: Request) {
  const body = (await req.json()) as CreateCampaignRequest;
  const r = await apiFetch<Campaign>("/api/campaigns", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to send campaign." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data, { status: 201 });
}

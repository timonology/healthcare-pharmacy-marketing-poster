import { NextResponse } from "next/server";
import type {
  CampaignAudiencePreview,
  CampaignAudienceRequest,
} from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

export async function POST(req: Request) {
  const body = (await req.json()) as CampaignAudienceRequest;
  const r = await apiFetch<CampaignAudiencePreview>("/api/campaigns/audience-preview", {
    method: "POST",
    body: JSON.stringify(body),
    auth: true,
  });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to preview audience." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data);
}

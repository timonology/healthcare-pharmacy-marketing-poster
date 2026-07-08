import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

interface ShareBody {
  recipients: string[];
  subject?: string;
  message?: string;
}

export async function POST(req: Request, ctx: { params: Promise<{ id: string }> }) {
  const { id } = await ctx.params;
  const body = (await req.json()) as ShareBody;
  const r = await apiFetch<{ sent: number; failed: number; note?: string }>(
    `/api/posters/${id}/share-email`,
    {
      method: "POST",
      body: JSON.stringify(body),
      auth: true,
    },
  );
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Failed to share." },
      { status: r.status || 400 },
    );
  }
  return NextResponse.json(r.data);
}

import { NextResponse } from "next/server";
import { apiFetch } from "@/lib/server/api-proxy";

/** Approves or rejects one request. Approval creates the membership. */
export async function POST(
  req: Request,
  { params: paramsPromise }: { params: Promise<{ id: string }> },
) {
  const params = await paramsPromise;
  const body = (await req.json().catch(() => ({}))) as {
    approve?: boolean;
    note?: string;
  };

  const result = await apiFetch<unknown>(
    `/api/admin/access-requests/${encodeURIComponent(params.id)}/review`,
    {
      method: "POST",
      auth: true,
      body: JSON.stringify({ approve: body.approve ?? false, note: body.note ?? null }),
    },
  );

  if (!result.ok) {
    return NextResponse.json(
      { error: result.error ?? "Could not record that decision." },
      { status: result.status || 400 },
    );
  }

  return new NextResponse(null, { status: 204 });
}

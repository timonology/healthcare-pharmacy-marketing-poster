import { NextResponse } from "next/server";
import type { RefinePlan } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Asks what a requested change would do. Runs nothing.
 *
 * <b>200 even when the change cannot be made.</b> A plan that says "we could
 * not tell what you wanted changed" or "that needs a part of the service we
 * have not opened up yet" is a successful answer to the question asked — the
 * pharmacist needs to read the sentence, and an error status would have the
 * client showing "something went wrong" instead of the explanation. Only a
 * genuine transport or auth failure is an error here.
 */
export async function POST(req: Request, { params: paramsPromise }: { params: Promise<{ id: string }> }) {
  const params = await paramsPromise;
  const body = await req.json().catch(() => null);
  const instruction = typeof body?.instruction === "string" ? body.instruction : "";

  const r = await apiFetch<RefinePlan>(`/api/videos/${params.id}/refine/preview`, {
    method: "POST",
    auth: true,
    // Stringified: apiFetch takes a RequestInit, so this is BodyInit and an
    // object would reach the API as "[object Object]".
    body: JSON.stringify({ instruction }),
  });

  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Could not work out what that change would do." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

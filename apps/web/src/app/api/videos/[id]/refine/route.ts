import { NextResponse } from "next/server";
import type { Video } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Runs the change that was previewed and agreed to.
 *
 * <b>Errors here are real errors</b>, unlike the preview route next door. By
 * this point the pharmacist has read a plan and pressed a button, so anything
 * that stops the work happening is a failure of something they asked for — a
 * plan that has lapsed, an instruction that no longer matches the one we
 * described, a change already made. The client shows the message; it must not
 * be dressed up as a successful answer.
 *
 * 202, not 200: the video goes back to Pending and the work happens on the
 * worker. What comes back is the video as it now stands, not the recut.
 */
export async function POST(req: Request, { params: paramsPromise }: { params: Promise<{ id: string }> }) {
  const params = await paramsPromise;
  const body = await req.json().catch(() => null);
  const instruction = typeof body?.instruction === "string" ? body.instruction : "";

  const r = await apiFetch<Video>(`/api/videos/${params.id}/refine`, {
    method: "POST",
    auth: true,
    // Stringified: apiFetch takes a RequestInit, so this is BodyInit and an
    // object would reach the API as "[object Object]".
    body: JSON.stringify({ instruction }),
  });

  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Could not make that change." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data, { status: 202 });
}

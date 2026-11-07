import { NextResponse } from "next/server";
import type { Video, VideoShot } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/** Every shot of a finished video, for the refine dialog's shot picker. */
export async function GET(_req: Request, { params: paramsPromise }: { params: Promise<{ id: string }> }) {
  const params = await paramsPromise;
  const r = await apiFetch<VideoShot[]>(`/api/videos/${params.id}/shots`, { auth: true });
  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "This video's shots could not be loaded." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

/**
 * Re-cut with new footage at the shots the pharmacist tapped.
 *
 * The API's refusal is passed through as written — "That shot is no longer in
 * this video. Close the change and open it again." is a sentence for the
 * pharmacist, and a generic failure here would throw it away.
 */
export async function POST(req: Request, { params: paramsPromise }: { params: Promise<{ id: string }> }) {
  const params = await paramsPromise;
  const body = await req.json().catch(() => null);
  if (!body) {
    return NextResponse.json({ error: "Tap at least one shot to change." }, { status: 400 });
  }

  const r = await apiFetch<Video>(`/api/videos/${params.id}/shots`, {
    method: "POST",
    auth: true,
    // Stringified: apiFetch only labels a string body as JSON.
    body: JSON.stringify(body),
  });

  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Those shots could not be changed." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data, { status: 202 });
}

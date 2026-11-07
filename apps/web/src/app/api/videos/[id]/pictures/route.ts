import { NextResponse } from "next/server";
import type { Video } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Add the pharmacy's own pictures to a finished video and re-cut it.
 *
 * Carries blob keys rather than files: each picture is uploaded to
 * `/api/videos/images` as it is chosen, so a file that is too big or the wrong
 * format is refused while the pharmacist is still looking at it. By the time
 * this runs, every key already exists in storage.
 *
 * The API's message is passed through rather than replaced. It is written for
 * the pharmacist — "This video already has 3, so there is room for 1 more" —
 * and a generic failure here would throw that away.
 */
export async function POST(req: Request, { params: paramsPromise }: { params: Promise<{ id: string }> }) {
  const params = await paramsPromise;
  const body = await req.json().catch(() => null);
  if (!body) {
    return NextResponse.json({ error: "Choose at least one picture." }, { status: 400 });
  }

  const r = await apiFetch<Video>(`/api/videos/${params.id}/pictures`, {
    method: "POST",
    auth: true,
    // Stringified here: apiFetch only sets Content-Type when the body is a
    // string, so handing it an object would send the request unlabelled and
    // the API would refuse it as an empty payload.
    body: JSON.stringify(body),
  });

  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Those pictures could not be added." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data, { status: 202 });
}

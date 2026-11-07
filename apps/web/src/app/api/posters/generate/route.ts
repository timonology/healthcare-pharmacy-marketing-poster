import { NextResponse } from "next/server";
import type { GeneratedPoster } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Designs a poster from a written brief.
 *
 * <b>Status codes are passed through, not flattened.</b> A 403 from this
 * endpoint is an allowance refusal — "you've used all 3 AI designs on your Free
 * plan this month" — and the sentence is the whole point of it. Collapsing
 * every failure to 500 with "something went wrong" would turn a clear, priced,
 * actionable limit into a bug report.
 *
 * <b>Slower than any other proxied call.</b> A model is laying out a page, so
 * this can take the best part of a minute. `maxDuration` is raised for the
 * platforms that enforce a default well below that; without it the request is
 * cut off at the edge and the pharmacist sees a generic failure for a design
 * that was about to arrive — and the allowance has already been spent on it,
 * because the API records the generation when the model returns.
 */
// 300, not 120: a design with an AI photograph makes the picture and then the
// layout, and cutting the request off at the edge would lose a design that was
// about to arrive — after the allowance had been spent on it.
export const maxDuration = 300;

export async function POST(req: Request) {
  const body = await req.json().catch(() => null);

  const r = await apiFetch<GeneratedPoster>("/api/posters/generate", {
    method: "POST",
    auth: true,
    // Stringified: apiFetch takes a RequestInit, so this is BodyInit and an
    // object would reach the API as "[object Object]".
    body: JSON.stringify({
      prompt: typeof body?.prompt === "string" ? body.prompt : "",
      category: typeof body?.category === "string" ? body.category : null,
      palette: typeof body?.palette === "string" ? body.palette : null,
      photo: photoFrom(body?.photo),
    }),
  });

  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Could not design that poster." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

const PHOTO_MODES = ["None", "Stock", "Upload", "Ai"] as const;

/**
 * The photo choice, rebuilt field by field rather than forwarded as received.
 * Only these four fields exist on the API's record; anything else a client
 * adds is dropped here, and the API validates what remains.
 */
function photoFrom(raw: unknown) {
  if (!raw || typeof raw !== "object") return null;
  const p = raw as Record<string, unknown>;
  const mode = PHOTO_MODES.find((m) => m === p.mode);
  if (!mode) return null;
  const str = (v: unknown) => (typeof v === "string" && v.trim() ? v.trim() : null);
  return {
    mode,
    assetUrl: str(p.assetUrl),
    stockPhotoId: str(p.stockPhotoId),
    description: str(p.description),
  };
}

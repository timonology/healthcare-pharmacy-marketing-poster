import { NextResponse } from "next/server";
import type { NarrationOptions } from "@acme/shared-types";
import { apiFetch } from "@/lib/server/api-proxy";

/**
 * Which voices the create-video dialog may offer.
 *
 * The answer originates in ai-service — it owns `ELEVENLABS_API_KEY`, and that
 * key decides the narration provider. This is a relay, and deliberately not a
 * place that inspects voice ids: the dialog deciding for itself, from a
 * `11labs:` prefix, is exactly the bug being removed.
 *
 * **204 means "we could not find out".** The API answers that when ai-service
 * is unreachable, and it is passed through as `null` rather than replaced with
 * a guess — a guessed list is how a pharmacist ends up picking a voice they
 * will not get. The dialog then offers no voice choice and lets the server pick.
 */
export async function GET() {
  const r = await apiFetch<NarrationOptions>("/api/videos/narration", { auth: true });

  if (r.status === 204) return NextResponse.json(null);

  if (!r.ok || !r.data) {
    return NextResponse.json(
      { error: r.error ?? "Could not load narration options." },
      { status: r.status || 500 },
    );
  }
  return NextResponse.json(r.data);
}

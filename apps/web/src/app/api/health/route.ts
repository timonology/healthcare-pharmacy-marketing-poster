import { NextResponse } from "next/server";

/**
 * Liveness, and which build is serving.
 *
 * `build` is the deploy's SHA tag (BUILD_ID, baked in by scripts/deploy.sh), or
 * null locally. `pnpm smoke` compares it with the API's and the ai-service's so
 * a half-finished deploy is caught before a customer finds the seam.
 */
export const dynamic = "force-dynamic";

export function GET() {
  return NextResponse.json({ status: "ok", build: process.env.BUILD_ID || null });
}

import { NextResponse } from "next/server";

// Deprecated — /videos now takes a single CreateVideoRequest with a topic.
export async function POST() {
  return NextResponse.json(
    { error: "Retired. POST /api/videos with { topic, ... } instead." },
    { status: 410 },
  );
}

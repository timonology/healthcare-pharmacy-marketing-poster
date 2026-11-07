import { NextResponse } from "next/server";
import { proxyDownload } from "@/lib/server/download-proxy";

/**
 * One of the signed-in pharmacy's own photo uploads, for the "Design with AI"
 * preview (security review SEC-15). The API looks only in the caller's own
 * private area, so another pharmacy's file name is simply not found.
 */
export async function GET(
  _req: Request,
  { params }: { params: Promise<{ fileName: string }> },
) {
  const { fileName } = await params;
  if (!/^\d{8}-[0-9a-f]{32}\.(jpg|png|webp)$/.test(fileName)) {
    return new NextResponse(null, { status: 404 });
  }
  return proxyDownload(`/api/posters/photos/${fileName}`, "image/jpeg");
}

import { NextResponse } from "next/server";
import { MAX_VIDEO_IMAGE_BYTES, type VideoImage } from "@acme/shared-types";
import { API_BASE } from "@/lib/server/api-proxy";
import { readAccessToken } from "@/lib/server/cookies";
import { forwardingHeaders } from "@/lib/server/forwarding";

/**
 * Attach one of the pharmacy's own pictures to a video being created.
 *
 * Hand-written rather than left to the JSON proxy because this is multipart:
 * the body is a stream with a boundary in the content type, and reading it as
 * JSON or re-encoding it corrupts the file. Both are forwarded untouched — the
 * same shape as the brand-kit logo upload, and for the same reason.
 *
 * <b>Uploaded before the video exists.</b> The dialog stores the picture as
 * soon as it is chosen so a thumbnail appears and a wrong one can be removed
 * before a render is spent. The API keys it under the owner and attaches it to
 * the video at create.
 */
export async function POST(req: Request): Promise<Response> {
  const token = (await readAccessToken());
  if (!token) {
    return NextResponse.json({ error: "Not signed in." }, { status: 401 });
  }

  const contentType = req.headers.get("content-type");
  if (!contentType?.includes("multipart/form-data")) {
    return NextResponse.json(
      { error: "Send the image as a file." },
      { status: 400 },
    );
  }

  const upstream = await fetch(`${API_BASE}/api/videos/images`, {
    method: "POST",
    headers: {
      ...(await forwardingHeaders()),
      Authorization: `Bearer ${token}`,
      // Verbatim: it carries the multipart boundary, and a boundary that does
      // not match the body makes the upload unparseable upstream.
      "Content-Type": contentType,
    },
    body: await req.arrayBuffer(),
    cache: "no-store",
  });

  const text = await upstream.text();
  if (!upstream.ok) {
    // The API's message is written for the pharmacist — "Images must be under
    // 8MB", "Images must be JPEG, PNG or WebP" — so it is passed through rather
    // than replaced with a generic failure.
    //
    // <b>Except when there is no message at all.</b> The size limit is enforced
    // by `[RequestSizeLimit]`, which rejects the request in the framework before
    // the action runs — so an oversized file comes back as a bare 413 with an
    // empty body. Falling through to the generic sentence told a pharmacist who
    // had just waited for a 20MB upload only that it "could not be attached",
    // which is the one thing they already knew.
    //
    // The browser now refuses these before sending, so this is the second line
    // of defence rather than the first: it still matters for anything that
    // reaches the API another way.
    const limitMb = MAX_VIDEO_IMAGE_BYTES / 1024 / 1024;
    // A body past the controller's hard ceiling never reaches the action, so
    // there is no message of ours to pass on. It arrives either as a bare 413
    // or as ASP.NET's own validation envelope saying the form could not be
    // read — neither of which has a field we can show. Both mean one thing.
    const tooLargeForTheFramework =
      upstream.status === 413 || /request body too large/i.test(text);

    let message = tooLargeForTheFramework
      ? `Images must be under ${limitMb}MB. That file is too big to upload.`
      : "That image could not be attached.";
    try {
      message = (JSON.parse(text) as { error?: string }).error ?? message;
    } catch {
      /* upstream sent something that was not JSON; keep the message above */
    }
    return NextResponse.json({ error: message }, { status: upstream.status });
  }

  return NextResponse.json(JSON.parse(text) as VideoImage);
}

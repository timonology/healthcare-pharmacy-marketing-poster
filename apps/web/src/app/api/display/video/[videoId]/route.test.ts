import { afterEach, describe, expect, it } from "vitest";
import { GET } from "./route";

/**
 * The kiosk's video route: a thin, streaming pass-through to the API's signed
 * download. What matters is what it forwards and what it refuses to add.
 */

const realFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = realFetch;
});

function upstream(status: number, headers: Record<string, string>, body = "bytes") {
  const calls: { url: string; init: RequestInit }[] = [];
  globalThis.fetch = (async (url: string, init: RequestInit) => {
    calls.push({ url: String(url), init });
    return new Response(status === 204 || status === 304 ? null : body, { status, headers });
  }) as typeof fetch;
  return calls;
}

const call = (url: string, headers: Record<string, string> = {}, videoId = "v1") =>
  GET(new Request(`http://kiosk.test${url}`, { headers }), { params: Promise.resolve({ videoId }) });

describe("GET /api/display/video/[videoId]", () => {
  it("forwards the signature as the download token, and the Range header", async () => {
    const calls = upstream(206, {
      "content-type": "video/mp4",
      "content-range": "bytes 0-9/64",
      "accept-ranges": "bytes",
      "content-length": "10",
    });

    const res = await call("/api/display/video/v1?sig=a.b%2Bc", { range: "bytes=0-9" });

    expect(calls).toHaveLength(1);
    expect(calls[0]!.url).toMatch(/\/api\/videos\/v1\/public-download\?token=a\.b%2Bc$/);
    expect((calls[0]!.init.headers as Record<string, string>).range).toBe("bytes=0-9");

    // Safari will not play a video whose server ignores ranges.
    expect(res.status).toBe(206);
    expect(res.headers.get("content-range")).toBe("bytes 0-9/64");
    expect(res.headers.get("accept-ranges")).toBe("bytes");
    expect(res.headers.get("content-type")).toBe("video/mp4");
  });

  it("lets the screen's browser cache it privately, for the signing window", async () => {
    upstream(200, { "content-type": "video/mp4" });
    const res = await call("/api/display/video/v1?sig=s&v=abc");
    expect(res.headers.get("cache-control")).toBe("private, max-age=21600");
  });

  it("passes the validators a browser needs to cache ranged video", async () => {
    const calls = upstream(206, {
      "content-type": "video/mp4",
      "content-range": "bytes 0-9/64",
      etag: '"v1-abc"',
    });

    const res = await call("/api/display/video/v1?sig=s", {
      range: "bytes=0-9",
      "if-range": '"v1-abc"',
      "if-none-match": '"v1-abc"',
    });

    const sent = calls[0]!.init.headers as Record<string, string>;
    expect(sent["if-range"]).toBe('"v1-abc"');
    expect(sent["if-none-match"]).toBe('"v1-abc"');
    expect(res.headers.get("etag")).toBe('"v1-abc"');
  });

  it("answers a revalidation with 304 and keeps the entry fresh", async () => {
    upstream(304, { etag: '"v1-abc"' });
    const res = await call("/api/display/video/v1?sig=s", { "if-none-match": '"v1-abc"' });
    expect(res.status).toBe(304);
    expect(res.headers.get("etag")).toBe('"v1-abc"');
    expect(res.headers.get("cache-control")).toBe("private, max-age=21600");
  });

  it("refuses an id that is not one of ours, without asking the API", async () => {
    // ".." survives encodeURIComponent and would walk the upstream path.
    const calls = upstream(200, {});
    for (const id of ["..", "a/b", "", "x".repeat(65)]) {
      const res = await call("/api/display/video/x?sig=s", {}, id);
      expect(res.status).toBe(404);
    }
    expect(calls).toHaveLength(0);
  });

  it("stops the upstream transfer when the kiosk goes away", async () => {
    const calls = upstream(200, {});
    await call("/api/display/video/v1?sig=s");
    expect(calls[0]!.init.signal).toBeDefined();
  });

  it("answers 502 when the API cannot be reached", async () => {
    globalThis.fetch = (async () => { throw new TypeError("fetch failed"); }) as typeof fetch;
    const res = await call("/api/display/video/v1?sig=s");
    expect(res.status).toBe(502);
  });

  it("does not cache a refusal", async () => {
    upstream(403, {});
    const res = await call("/api/display/video/v1?sig=bad");
    expect(res.headers.get("cache-control")).toBeNull();
  });

  it("refuses without a signature and does not ask the API", async () => {
    const calls = upstream(200, {});
    const res = await call("/api/display/video/v1");
    expect(res.status).toBe(400);
    expect(calls).toHaveLength(0);
  });

  it("passes a refused or missing video through without a body", async () => {
    upstream(403, { "content-type": "application/json" }, '{"error":"Invalid token."}');
    const res = await call("/api/display/video/v1?sig=bad");
    expect(res.status).toBe(403);
    expect(await res.text()).toBe("");
  });

  it("does not send the screen token or cookies upstream", async () => {
    const calls = upstream(200, {});
    await call("/api/display/video/v1?sig=s", {
      "x-display-token": "screen-secret",
      cookie: "access_token=abc",
    });
    const sent = JSON.stringify(calls[0]!.init.headers ?? {});
    expect(sent).not.toContain("screen-secret");
    expect(sent).not.toContain("access_token");
  });
});

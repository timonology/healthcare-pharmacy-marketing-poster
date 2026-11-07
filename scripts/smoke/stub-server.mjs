#!/usr/bin/env node
/**
 * A fake deployment, for testing the smoke test itself.
 *
 * <b>Not part of `pnpm smoke`.</b> This exists so the runner can be exercised
 * end to end — every step, every branch, the report and the exit code — without
 * a real API, a real model bill, or a real email. A smoke test is a piece of
 * software like any other, and this one has already shipped two bugs of its own
 * (it crashed with a stack trace when a service was down, and it called
 * `/api/subscription` when the route is `/api/subscription/current`). Both were
 * found by running it against this.
 *
 *   node scripts/smoke/stub-server.mjs --port 4010 --scenario healthy
 *
 * Scenarios:
 *   healthy        everything works — the smoke should exit 0, all PASS
 *   video-stuck    the video never leaves Rendering, note frozen — must FAIL
 *   video-long     renders, but 27s for a 15s product — must WARN (exit 0)
 *   video-slow     still Rendering at the deadline but still reporting new
 *                  stages — must WARN (exit 0) and must NOT be deleted
 *   video-crash    the video Fails with a Python traceback in `note` — must FAIL
 *   video-outage   the video Fails with a human sentence — must WARN, exit 0
 *   image-dropped  the render is Ready but says it could not use the attached
 *                  picture — must FAIL, because the pharmacist's photograph is
 *                  silently not in a video that looks perfectly fine
 *   image-ignored  Ready, but the render says nothing at all about the attached
 *                  pictures — a worker that never looked. Must FAIL.
 *   image-mute     an oversized upload is refused with an empty body, the bare
 *                  413 the framework produces — must WARN
 *   route-missing  ai-service omits classify-refine — must FAIL
 *   narration-edge a hosted deployment with no ElevenLabs key — must FAIL
 *   narration-missing  the API cannot reach ai-service for voices — must FAIL
 *   poster-card-blank  the My Posters card is an empty image — must FAIL
 *   build-split    the web app is on a different build from the API — must FAIL
 *   client-ip-mismatch the web app signs browser addresses with a key the API
 *                  does not hold, so all users share one — must FAIL
 *   reset-rate-limited "forgot password" refused with 429 — must FAIL
 *   reset-not-sent "forgot password" says 202 but nothing reached the account — must FAIL
 *   reset-rejected the reset reached the account, the provider refused it — must FAIL
 *   reset-mocked   the API is in email mock mode — WARN locally (FAIL when hosted)
 *   campaign-misconfigured  the campaign fails with "Mailjet credentials missing" — must FAIL
 *   api-development a hosted API running as Development — must FAIL
 *   social-no-key  no credential encryption key: no social account can connect — must FAIL
 *   social-url-moved  the API builds links on another address; connecting is refused — must FAIL
 *   google-redirect-mismatch  Google does not allow the callback address — must FAIL
 *   youtube-invalid-scope  Google refuses the YouTube scopes on the consent page — must FAIL
 *   youtube-asks-for-ads   the YouTube sign-in asks for Google Ads access too — must FAIL
 *   x-no-media-scope   the X sign-in cannot upload pictures (no media.write) — must FAIL
 *   x-no-pkce          the X sign-in carries no PKCE challenge — must FAIL
 *   download-cut-once  the first video download drops mid-file — retried, exit 0
 *   readiness-healthy  every dependency checked from inside, all fine — exit 0
 *   readiness-db-down  the database is unreachable from the API — must FAIL
 *   readiness-url-moved  the API's link address is not the site under test — must FAIL
 */

import { createServer } from "node:http";

const args = process.argv.slice(2);
const opt = (name, dflt) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : dflt;
};
const PORT = Number(opt("port", 4010));
const SCENARIO = opt("scenario", "healthy");

const TOKEN = "stub-access-token";
// Posters carry a status, because the product does: a draft may not be sent to
// patients (PosterUsage.WhyCannotSend), and the smoke has to walk the same
// publish-then-send path a pharmacist does. Encoding the rule here means that
// if the publish step is ever dropped from the runner, the self-test notices.
const state = { videos: new Map(), campaigns: [], posters: new Map(), screens: new Set() };

// Sized like real exports of a designed poster, not like the smallest thing
// that carries the right magic bytes. The runner warns when a poster built from
// a template exports at about the size of an empty page, so a stub that emitted
// 2 KB would make the healthy scenario warn about a fault that isn't there.
const PNG = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  Buffer.alloc(48 * 1024, 7),
]);
const PDF = Buffer.concat([Buffer.from("%PDF-1.7\n"), Buffer.alloc(32 * 1024, 7)]);
/**
 * A minimal but genuinely parseable MP4 of a chosen length.
 *
 * `Buffer.alloc(64 * 1024)` was fine while the runner only checked the size,
 * but it now reads the duration out of the `mvhd` box — and a stub that cannot
 * be parsed would exercise only the "could not read duration" branch, leaving
 * the check that matters untested. ftyp + moov/mvhd is all the parser needs;
 * the mdat is padding so the file is a plausible size.
 */
function fakeMp4(seconds) {
  const box = (type, body) => {
    const head = Buffer.alloc(8);
    head.writeUInt32BE(8 + body.length, 0);
    head.write(type, 4, "latin1");
    return Buffer.concat([head, body]);
  };

  const ftyp = box("ftyp", Buffer.concat([
    Buffer.from("isom", "latin1"),
    Buffer.from([0, 0, 2, 0]),
    Buffer.from("isomiso2avc1mp41", "latin1"),
  ]));

  const timescale = 1000;
  const mvhdBody = Buffer.alloc(100);
  mvhdBody.writeUInt8(0, 0);                                   // version 0
  mvhdBody.writeUInt32BE(timescale, 12);                       // timescale
  mvhdBody.writeUInt32BE(Math.round(seconds * timescale), 16); // duration
  mvhdBody.writeUInt32BE(0x00010000, 20);                      // rate 1.0
  mvhdBody.writeUInt16BE(0x0100, 24);                          // volume 1.0

  return Buffer.concat([
    ftyp,
    box("moov", box("mvhd", mvhdBody)),
    box("mdat", Buffer.alloc(64 * 1024, 3)),
  ]);
}

// The healthy path renders the length the product sells; `video-long` is the
// scriptwriter overrunning its word budget, which must WARN and not block.
const MP4 = fakeMp4(SCENARIO === "video-long" ? 27.2 : 14.6);
// After a shot change. The same length unless the scenario is the one where the
// re-cut wrongly re-recorded the narration.
const MP4_AFTER_REFINE = SCENARIO === "refine-revoiced" ? fakeMp4(17.9) : MP4;
const JPEG = Buffer.concat([Buffer.from([0xff, 0xd8, 0xff, 0xe0]), Buffer.alloc(6 * 1024, 5)]);

/** The shots of a freshly rendered stub video; moment 0 carries the attached photo. */
const freshShots = () => [
  { index: 0, startSeconds: 0, endSeconds: 3.6, caption: "Extra weight strains the heart.", isPhoto: true, hasThumbnail: true, shows: null },
  { index: 1, startSeconds: 3.6, endSeconds: 7.3, caption: "Small changes add up.", isPhoto: false, hasThumbnail: true, shows: "fresh vegetables on a table" },
  { index: 2, startSeconds: 7.3, endSeconds: 10.9, caption: "Ask about a weight check.", isPhoto: false, hasThumbnail: true, shows: "a pharmacist at a counter" },
  { index: 3, startSeconds: 10.9, endSeconds: 14.6, caption: "Book today.", isPhoto: false, hasThumbnail: true, shows: "employees on a couch" },
];
let videosUsed = 1;
/** What the typed refine says when it names no shot — the worker's wording. */
const TAP = "Tap the shot you want to change in the list, or name it by position — " +
  "\"the first shot\", \"the last shot\", \"shot 3\". We don't pick a shot from a description of what is in it.";

const VIDEO_OUTCOME = {
  // The runner attaches a real PNG *and* a damaged file, so a working render
  // has something to say about the second one. Silence here would mean the
  // worker never looked — see `image-ignored`.
  healthy: {
    status: "Ready",
    note: null,
    diagnostic: "Attached images not used: image 1 (upload): could not be decoded",
  },
  "video-stuck": { status: "Rendering", note: "Rendering… 40%" },
  "video-crash": {
    status: "Failed",
    note: "expected string or bytes-like object, got 'list'",
    diagnostic: "TypeError: expected string or bytes-like object, got 'list'",
  },
  "video-outage": {
    // A human note with a technical diagnostic behind it — the shape a real
    // provider outage now produces. The runner must classify on the note (WARN)
    // while still printing the diagnostic, which is always technical by design.
    // Judging the diagnostic instead would make every outage a FAIL.
    "note": "Something went wrong while making this video. Try again — it won't use another video from your allowance.",
    status: "Failed",
    diagnostic: "NonRetriablePipelineError: ElevenLabs could not narrate this ← ElevenLabsError: ElevenLabs returned 401: Invalid API key",
  },
  // Ready, playable, and missing the one thing the pharmacist supplied. The
  // note stays empty because nothing went wrong for them to be told about —
  // which is precisely why only `diagnostic` can reveal it.
  "image-dropped": {
    status: "Ready",
    note: null,
    // Image 2 is the real PNG. A render that drops *that* has lost the
    // pharmacist's photograph, whatever it did with the damaged one.
    diagnostic: "Attached images not used: image 2 (upload): could not be decoded",
  },
  // Ready, playable, and completely silent about the attached pictures — the
  // shape an ai-service built before the feature produces. Indistinguishable
  // from success unless something was sent that a working render must complain
  // about, which is why the runner sends a damaged file too.
  "image-ignored": { status: "Ready", note: null },
}[SCENARIO] ?? {
  // Every scenario that is not about the render itself still has to look like a
  // *working* render, which now includes noticing the damaged file the runner
  // attaches. `image-ignored` is the one that deliberately does not.
  status: "Ready",
  note: null,
  diagnostic: "Attached images not used: image 1 (upload): could not be decoded",
};

const json = (res, code, body, headers = {}) => {
  const payload = JSON.stringify(body);
  res.writeHead(code, { "content-type": "application/json", ...headers });
  res.end(payload);
};
const binary = (res, type, buf) => {
  res.writeHead(200, { "content-type": type, "content-length": buf.length });
  res.end(buf);
};

const server = createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const p = url.pathname;
  let body = null;
  if (req.method === "POST" || req.method === "PUT" || req.method === "PATCH") {
    const chunks = [];
    for await (const c of req) chunks.push(c);
    try { body = JSON.parse(Buffer.concat(chunks).toString() || "{}"); } catch { body = {}; }
  }

  /* ---- health, shared by "api" and "ai" since one stub serves both ---- */
  if (p === "/health") return json(res, 200, { status: "ok", build: `stub-${SCENARIO}` });

  if (p === "/openapi.json") {
    const paths = {
      "/api/canvas/generate-template": { post: {} },
      "/api/canvas/image-options": { get: {} },
      "/api/canvas/stock-photos": { get: {} },
      "/api/canvas/stock-photo": { post: {} },
    };
    if (SCENARIO !== "route-missing") {
      paths["/api/video/classify-refine"] = { post: {} };
      paths["/api/video/narration"] = { get: {} };
    }
    return json(res, 200, { openapi: "3.1.0", paths });
  }

  // Answers only in Development. api-development: a hosted API left in it.
  if (p === "/api/diagnostics/jwt") {
    return SCENARIO === "api-development"
      ? json(res, 200, { secretFingerprint: "000000000000", issuer: "acme-api", audience: "acme-clients" })
      : json(res, 404, {});
  }

  // Readiness, from inside each service. readiness-db-down: the database is
  // unreachable. readiness-url-moved: the API builds its links on another
  // address than the site under test (22 October).
  if (p === "/api/health/ready" || p === "/health/ready") {
    if (req.headers["x-readiness-token"] !== "stub-readiness-token") return json(res, 404, {});
    const isApi = p === "/api/health/ready";
    const checks = isApi
      ? [
          { name: "database", status: SCENARIO === "readiness-db-down" ? "fail" : "ok",
            detail: SCENARIO === "readiness-db-down" ? "no answer within 10s" : "reachable, 12 account(s)", ms: 5 },
          { name: "storage", status: "ok", detail: "write, read and delete work", ms: 5 },
          { name: "email", status: "ok", detail: "sender info@example.test is verified on this account", ms: 5 },
        ]
      : [{ name: "pexels", status: "ok", detail: "search works", ms: 5 }];
    return json(res, 200, isApi
      ? { status: "ok", environment: "Production", build: `stub-${SCENARIO}`,
          webBaseUrl: SCENARIO === "readiness-url-moved" ? "https://old-address.example" : `http://127.0.0.1:${PORT}`,
          socialCallbacks: {}, checks }
      : { status: "ok", build: `stub-${SCENARIO}`, checks });
  }

  // Google's consent page. google-redirect-mismatch: Google does not allow the
  // callback, which it says before anyone signs in.
  if (p.startsWith("/o/oauth2/")) {
    // youtube-invalid-scope: the YouTube scopes are not on the consent screen.
    const youtube = (url.searchParams.get("scope") ?? "").includes("youtube");
    if (SCENARIO === "youtube-invalid-scope" && youtube) {
      res.writeHead(400, { "content-type": "text/html" });
      return res.end("<html><body>Error 400: invalid_scope</body></html>");
    }
    res.writeHead(SCENARIO === "google-redirect-mismatch" ? 400 : 200, { "content-type": "text/html" });
    return res.end(SCENARIO === "google-redirect-mismatch"
      ? "<html><body>Error 400: redirect_uri_mismatch</body></html>"
      : "<html><body>Sign in with Google</body></html>");
  }

  // LinkedIn's sign-in page. linkedin-redirect-mismatch: the callback is not
  // in the app's Authorized redirect URLs. linkedin-unauthorized-scope: the
  // app lacks a product for a scope it asks for, which LinkedIn answers by
  // sending the person straight back with an error.
  if (p === "/oauth/v2/authorization") {
    if (SCENARIO === "linkedin-unauthorized-scope") {
      const back = new URL(url.searchParams.get("redirect_uri") ?? "http://127.0.0.1/");
      back.searchParams.set("error", "unauthorized_scope_error");
      back.searchParams.set("error_description", "Scope \"w_organization_social\" is not authorized for your application");
      back.searchParams.set("state", url.searchParams.get("state") ?? "");
      res.writeHead(303, { location: back.toString() });
      return res.end();
    }
    res.writeHead(200, { "content-type": "text/html" });
    return res.end(SCENARIO === "linkedin-redirect-mismatch"
      ? "<html><body>Bummer, something went wrong. The redirect_uri does not match the registered value</body></html>"
      : "<html><body>Sign in to LinkedIn</body></html>");
  }

  // Connect, through the web app as the button does. social-url-moved: the
  // API builds its address on another host — the 22 October production failure.
  if (p === "/api/social/connections/start" && req.method === "POST") {
    if (!req.headers.cookie?.includes("acme_at=")) return json(res, 401, { error: "no cookie" });
    if (SCENARIO === "social-url-moved") {
      return json(res, 400, {
        error: "Connections must be started from https://old-address.example, and this page is open at " +
          `http://127.0.0.1:${PORT}. Open the app at the first address and try again.`,
      });
    }
    const redirect = encodeURIComponent(body?.redirectUri ?? "");
    const clientId = SCENARIO === "google-client-id-url"
      ? encodeURIComponent("http://123-stub.apps.googleusercontent.com")
      : "stub.apps.googleusercontent.com";
    const consent = `http://127.0.0.1:${PORT}/o/oauth2/v2/auth?client_id=${clientId}&redirect_uri=${redirect}`;
    const ytScopes = SCENARIO === "youtube-asks-for-ads"
      ? "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/adwords"
      : "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";
    const authorizationUrl = body?.kind === "Google"
      ? `${consent}&scope=${encodeURIComponent("https://www.googleapis.com/auth/adwords")}`
      : body?.kind === "YouTube"
        ? `${consent}&scope=${encodeURIComponent(ytScopes)}`
        : body?.kind === "TikTok"
          ? `https://www.tiktok.com/v2/auth/authorize/?client_key=stub-key&scope=${SCENARIO === "tiktok-no-publish-scope" ? "user.info.basic" : "user.info.basic,video.publish"}` +
            `&response_type=code&redirect_uri=${redirect}&state=stub-state`
        : body?.kind === "X"
          ? `https://x.com/i/oauth2/authorize?response_type=code&client_id=stub-x&redirect_uri=${redirect}&state=stub-state` +
            `&scope=${encodeURIComponent(SCENARIO === "x-no-media-scope" ? "tweet.read tweet.write users.read offline.access" : "tweet.read tweet.write users.read media.write offline.access")}` +
            (SCENARIO === "x-no-pkce" ? "" : "&code_challenge=stub-challenge&code_challenge_method=S256")
        : body?.kind === "LinkedIn"
          ? `http://127.0.0.1:${PORT}/oauth/v2/authorization?response_type=code&client_id=stub-li&redirect_uri=${redirect}` +
            `&state=stub-state&scope=${encodeURIComponent(SCENARIO === "linkedin-no-post-scope" ? "openid profile" : "openid profile w_member_social")}`
          : `https://www.facebook.com/v26.0/dialog/oauth?client_id=1234&redirect_uri=${redirect}`;
    return json(res, 200, { authorizationUrl, state: "stub-state" });
  }

  /* ---- web tier ---- */
  if (p === "/") { res.writeHead(200, { "content-type": "text/html" }); return res.end("<html></html>"); }

  // The web app's own build marker. build-split: the web app is on a different
  // build from the API — a half-finished deploy.
  if (p === "/api/health") {
    return json(res, 200, {
      status: "ok",
      build: SCENARIO === "build-split" ? "stub-older-web" : `stub-${SCENARIO}`,
    });
  }

  // Can the API tell browsers apart? client-ip-mismatch: the web app signs
  // the address with a key the API does not hold, so every user shares one.
  if (p === "/api/diagnostics/client-address") {
    if (!req.headers.cookie?.includes("acme_at=")) return json(res, 401, { error: "no cookie" });
    return json(res, 200, SCENARIO === "client-ip-mismatch"
      ? { verdict: "key_mismatch", keyConfigured: true, signature: "invalid", address: "10.0.0.x" }
      : { verdict: "ok", keyConfigured: true, signature: "valid", address: "127.0.0.x" });
  }

  // "Forgot password", through the web app, as a browser sends it.
  //   reset-rate-limited  refused with 429 — every user sharing one address
  //   reset-not-sent      202, but the request never reached the account
  //   reset-rejected      reached the account; the provider refused the email
  if (p === "/api/auth/forgot-password" && req.method === "POST") {
    if (SCENARIO === "reset-rate-limited") {
      return json(res, 429, { error: "Too many reset requests from this connection." });
    }
    if (SCENARIO !== "reset-not-sent" && body?.email) {
      state.lastReset = {
        requestedAtUtc: new Date().toISOString(),
        outcome: SCENARIO === "reset-rejected" ? "rejected"
          : SCENARIO === "reset-mocked" ? "mocked"
          : "accepted",
      };
    }
    return json(res, 202, { message: "If that address has an account, a reset link is on its way." });
  }
  if (p === "/api/auth/login" && req.method === "POST") {
    // The web BFF sets a session cookie; the API returns a bearer token.
    return json(res, 200, { accessToken: TOKEN, user: { id: "u1", email: body?.email } },
      { "set-cookie": "acme_at=stub; Path=/; HttpOnly" });
  }
  if (p === "/api/me/full") {
    if (!req.headers.cookie?.includes("acme_at=")) return json(res, 401, { error: "no cookie" });
    return json(res, 200, { id: "u1", email: "stub@example.test", profile: { pharmacyName: "Stub" } });
  }

  // The shot still is fetched the way a browser fetches it: through the web
  // app, with the session cookie rather than the API's bearer token.
  if (/^\/api\/videos\/[^/]+\/shots\/\d+\/thumbnail$/.test(p) && req.headers.cookie?.includes("acme_at=")) {
    return binary(res, "image/jpeg", JPEG);
  }

  // The kiosk: anonymous, a device token in a header — never the bearer.
  if (p === "/api/display/claim" && req.method === "POST") {
    return body?.code === "ABC123"
      ? json(res, 200, { token: "stub-device-token", screenId: "screen-1", screenName: "Smoke" })
      : json(res, 400, { error: "That code did not work." });
  }
  if (p === "/api/display/content") {
    if (req.headers["x-display-token"] !== "stub-device-token") return json(res, 401, {});
    const canPlay = /\bvideo\b/i.test(req.headers["x-display-capabilities"] ?? "");
    // screen-video-leak: a video sent to a kiosk that never said it could play
    // one — which an old kiosk draws as a broken picture.
    const sendVideo = canPlay || SCENARIO === "screen-video-leak";
    const videoId = state.playlistVideo;
    const slides = sendVideo && videoId
      ? [{ kind: "Video", imageUrl: "", name: "Smoke video", durationSeconds: 15,
           mediaUrl: `/api/display/video/${videoId}?sig=stub-sig` }]
      : [];
    return json(res, 200, { screenName: "Smoke", addressLine: "", slides, revision: "r1", pollSeconds: 30 });
  }
  if (/^\/api\/display\/video\/[^/]+$/.test(p)) {
    if (SCENARIO === "screen-video-broken" || url.searchParams.get("sig") !== "stub-sig") {
      res.writeHead(403); return res.end();
    }
    const file = Buffer.alloc(4096, 1);
    // screen-video-noetag: plays, but nothing a browser can cache ranged
    // video against.
    const validator = SCENARIO === "screen-video-noetag" ? {} : { etag: '"stub-video-1"' };
    const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range ?? "");
    // screen-video-norange: the whole file whatever was asked — Chrome copes,
    // Safari refuses to play.
    if (range && SCENARIO !== "screen-video-norange") {
      const start = Number(range[1]);
      const end = Math.min(range[2] ? Number(range[2]) : file.length - 1, file.length - 1);
      res.writeHead(206, {
        "content-type": "video/mp4",
        "content-range": `bytes ${start}-${end}/${file.length}`,
        "accept-ranges": "bytes",
        ...validator,
      });
      return res.end(file.subarray(start, end + 1));
    }
    return binary(res, "video/mp4", file);
  }

  /* ---- everything below needs the bearer ---- */
  const authed = req.headers.authorization === `Bearer ${TOKEN}`;
  if (p.startsWith("/api/") && !authed && p !== "/api/auth/login" && p !== "/api/me/full") {
    return json(res, 401, { error: "unauthenticated" });
  }

  // What became of this account's last reset email — the API's answer, read
  // with the bearer token after asking through the web app.
  if (p === "/api/auth/account-email/latest") {
    if (!state.lastReset) { res.writeHead(204); return res.end(); }
    return json(res, 200, {
      purpose: "PasswordReset",
      requestedAtUtc: state.lastReset.requestedAtUtc,
      outcome: state.lastReset.outcome,
      linkHost: "127.0.0.1",
    });
  }

  // The Social page's question. social-no-key: no credential encryption key —
  // what production reported on 21 October.
  if (p === "/api/social/availability") {
    const noKey = SCENARIO === "social-no-key";
    const reason = noKey
      ? "Social accounts are not set up on this server yet. An administrator needs to add the credential encryption key."
      : null;
    return json(res, 200, {
      planAllows: true, planReason: null,
      providers: ["Meta", "Google", "YouTube", "LinkedIn", "TikTok", "X"].map((kind) => ({ kind, available: !noKey, reason })),
    });
  }

  if (p === "/api/subscription/current") {
    return json(res, 200, {
      plan: { displayName: "Pro", maxPosters: -1, maxVideosPerMonth: 30, maxAiGenerationsPerMonth: -1 },
      usage: { posters: 2, videosThisMonth: videosUsed, aiGenerationsThisMonth: 4 },
    });
  }

  // One published template, so the runner builds its poster from a design the
  // way a pharmacist would.
  if (p === "/api/templates" && req.method === "GET") {
    return json(res, 200, {
      items: [{ id: "tmpl-1", name: "Flu clinic", category: "Seasonal" }],
      total: 1, skip: 0, take: 1,
    });
  }

  if (p === "/api/posters" && req.method === "POST") {
    const id = `poster-${state.posters.size + 1}`;
    // New posters are drafts. This is the whole point of the status field.
    state.posters.set(id, { id, name: body?.name ?? "Untitled", status: "Draft" });
    return json(res, 201, { id, name: body?.name ?? "Untitled", status: "Draft" });
  }
  if (p === "/api/posters/generate" && req.method === "POST") {
    const id = `poster-ai-${state.posters.size + 1}`;
    const canvas = houseCanvas();
    if (SCENARIO === "ai-design-off-style") {
      // The old designer's look: a hot-linked stock photo, and no booking QR.
      canvas.shapes[1].blobKey = "https://images.unsplash.com/photo-1559757175-5700dde675bc?w=1400";
      canvas.shapes.pop();
    }
    const poster = { id, name: "Walk-in Flu Clinic", status: "Draft", canvas };
    state.posters.set(id, poster);
    // The real shape: the poster, plus notes about its photo.
    const photoNotes = SCENARIO === "ai-design-photo-note"
      ? ["No stock photograph matched “flu clinic”, so this design is photo-free."]
      : [];
    return json(res, 200, { poster, photoNotes });
  }
  let m;
  if ((m = p.match(/^\/api\/posters\/([^/]+)\/status$/)) && req.method === "PATCH") {
    const poster = state.posters.get(m[1]);
    if (!poster) return json(res, 404, { error: "not found" });
    poster.status = body?.status ?? poster.status;
    return json(res, 200, { ...poster });
  }
  if ((m = p.match(/^\/api\/posters\/([^/]+)\/export\.pdf$/))) return binary(res, "application/pdf", PDF);
  if ((m = p.match(/^\/api\/posters\/([^/]+)\/export\.png$/))) return binary(res, "image/png", PNG);
  // The My Posters card. poster-card-blank: a 200 with nothing in it — the
  // browser's "?" card that published posters showed while the PDF was fine.
  if ((m = p.match(/^\/api\/posters\/([^/]+)\/card$/))) {
    const etag = `"stub-card-${m[1]}"`;
    if (req.headers["if-none-match"] === etag) { res.writeHead(304, { etag }); return res.end(); }
    const body = SCENARIO === "poster-card-blank" ? Buffer.alloc(0) : JPEG;
    res.writeHead(200, { "content-type": "image/jpeg", "content-length": body.length, etag });
    return res.end(body);
  }
  if ((m = p.match(/^\/api\/posters\/([^/]+)$/)) && req.method === "DELETE") {
    state.posters.delete(m[1]); res.writeHead(204); return res.end();
  }

  if (p === "/api/campaigns" && req.method === "POST") {
    // The send gate, in the stub's own words. A draft poster is refused with
    // the same shape the API uses — a 400 and a sentence for a person.
    const poster = state.posters.get(body?.posterId);
    if (poster && poster.status !== "Published") {
      return json(res, 400, {
        error: `'${poster.name}' is still a draft. Publish it before it can be sent in a campaign.`,
      });
    }
    const id = `campaign-${state.campaigns.length + 1}`;
    state.campaigns.push({ id, status: "Queued", note: null });
    // Becomes Sent on the next poll, like a worker would.
    // campaign-misconfigured: the API has no Mailjet keys — what production
    // reported on 15 October. A human sentence, but not a provider hiccup.
    setTimeout(() => {
      const c = state.campaigns.find((x) => x.id === id);
      if (!c) return;
      if (SCENARIO === "campaign-misconfigured") {
        c.status = "Failed";
        c.note = "1 recipient(s) failed. Mailjet credentials missing. Set Messaging__Mailjet__ApiKey and __SecretKey.";
      } else {
        c.status = "Sent";
      }
    }, 1500);
    return json(res, 201, { id, status: "Queued" });
  }
  if (p === "/api/campaigns" && req.method === "GET") return json(res, 200, state.campaigns);

  // Narration options. `narration-edge` pretends to be a hosted deployment
  // with no ElevenLabs key — the exact production misconfiguration that made
  // every render die at the voice stage.
  if (p === "/api/videos/narration" && req.method === "GET") {
    if (SCENARIO === "narration-missing") { res.writeHead(204); return res.end(); }
    if (SCENARIO === "narration-edge") {
      return json(res, 200, {
        provider: "edge",
        voices: [{ id: "en-US-JennyNeural", label: "Jenny · Warm US female" }],
        defaultVoiceId: "en-US-JennyNeural",
        estimateUsdPerVideo: 0,
      });
    }
    return json(res, 200, {
      provider: "elevenlabs",
      voices: [
        { id: "11labs:21m00Tcm4TlvDq8ikWAM", label: "Rachel · Editorial calm" },
        { id: "11labs:AZnzlk1XvdvUeBnXmlld", label: "Domi · Bright energetic" },
      ],
      defaultVoiceId: "11labs:21m00Tcm4TlvDq8ikWAM",
      estimateUsdPerVideo: 0.05,
    });
  }

  // Attaching one of the pharmacy's own pictures.
  //
  // The size check is what the real API does in the framework rather than in an
  // action, which is why the refusal below can carry no body: that is the exact
  // shape that told a pharmacist nothing about a 20MB photograph.
  if (p === "/api/videos/images" && req.method === "POST") {
    const length = Number(req.headers["content-length"] || 0);
    if (length > 8 * 1024 * 1024) {
      if (SCENARIO === "image-mute") {
        res.writeHead(413); // no body at all, exactly as the framework answers
        return res.end();
      }
      return json(res, 400, {
        error: `Images must be under 8MB. That one is ${(length / 1024 / 1024).toFixed(1)}MB.`,
      });
    }
    return json(res, 200, {
      blobKey: `videos/smoke-owner/attachments/${state.videos.size + 1}.png`,
      source: "upload",
    });
  }

  if (p === "/api/videos" && req.method === "POST") {
    const id = `video-${state.videos.size + 1}`;
    state.videos.set(id, { id, status: "Pending", note: null, createdAt: Date.now() });
    if (SCENARIO !== "video-slow") {
      setTimeout(() => state.videos.set(id, { id, ...VIDEO_OUTCOME }), 1500);
    }
    return json(res, 202, { id, status: "Pending" });
  }
  if ((m = p.match(/^\/api\/videos\/([^/]+)\/download$/))) {
    // download-cut-once: the connection drops half-way through the first
    // download, as a flaky home connection does. Must not FAIL the release.
    if (SCENARIO === "download-cut-once" && !state.cutOnce) {
      state.cutOnce = true;
      res.writeHead(200, { "content-type": "video/mp4", "content-length": "100000" });
      res.write(Buffer.alloc(1000));
      return setTimeout(() => res.socket?.destroy(), 50);
    }
    return binary(res, "video/mp4", state.videos.get(m[1])?.recut ? MP4_AFTER_REFINE : MP4);
  }

  /* ---- refining a finished video ---- */
  if ((m = p.match(/^\/api\/videos\/([^/]+)\/shots$/)) && req.method === "GET") {
    const v = state.videos.get(m[1]);
    if (!v) return json(res, 404, { error: "not found" });
    // An ai-service built before the picker records no timeline.
    if (SCENARIO === "refine-no-shots") return json(res, 200, []);
    // The timeline lost between the editor and the worker: tiles built from
    // the script's plan, none with a still.
    if (SCENARIO === "refine-no-timeline") {
      return json(res, 200, (v.shots ?? (v.shots = freshShots())).map((s) => ({ ...s, hasThumbnail: false })));
    }
    return json(res, 200, v.shots ?? (v.shots = freshShots()));
  }
  if ((m = p.match(/^\/api\/videos\/([^/]+)\/shots\/(\d+)\/thumbnail$/))) {
    return binary(res, "image/jpeg", JPEG);
  }
  if ((m = p.match(/^\/api\/videos\/([^/]+)\/shots$/)) && req.method === "POST") {
    const v = state.videos.get(m[1]);
    if (!v) return json(res, 404, { error: "not found" });
    const shots = v.shots ?? (v.shots = freshShots());
    const picks = Array.isArray(body?.shots) ? body.shots : [];
    if (!picks.length || picks.some((c) => !shots.some((s) => s.index === c.index))) {
      return json(res, 400, { error: "That shot is no longer in this video. Close the change and open it again." });
    }
    Object.assign(v, { status: "Pending", note: "Queued for changes…" });
    setTimeout(() => {
      if (SCENARIO === "refine-not-made") {
        // The failure a pharmacist reported: back to Ready, unchanged, with a reason.
        Object.assign(v, { status: "Ready", note: "We could not find a moment in that to change." });
        return;
      }
      for (const c of picks) {
        const tapped = SCENARIO === "refine-wrong-shot" ? (c.index === 1 ? 2 : 1) : c.index;
        const shot = shots.find((s) => s.index === tapped);
        if (shot) shot.shows = `${c.show ?? "on topic"} (new footage)`;
      }
      if (SCENARIO === "refine-recovery-miss") {
        // A kept clip that would not download again and was re-searched: the
        // tapped shot changed as asked, and one other shot changed with it.
        const other = shots.find((s) => s.index === 1);
        if (other) other.shows = "a different clip after a failed re-download";
      }
      if (SCENARIO === "refine-charged") videosUsed += 1;
      Object.assign(v, { status: "Ready", note: null, recut: true });
    }, 1200);
    return json(res, 202, { id: v.id, status: "Pending" });
  }
  if ((m = p.match(/^\/api\/videos\/([^/]+)\/refine\/preview$/)) && req.method === "POST") {
    return json(res, 200, { canRun: true, stage: "Visuals", summary: "Find different footage…", reason: "", free: true });
  }
  if ((m = p.match(/^\/api\/videos\/([^/]+)\/refine$/)) && req.method === "POST") {
    const v = state.videos.get(m[1]);
    if (!v) return json(res, 404, { error: "not found" });
    Object.assign(v, { status: "Pending", note: "Queued for changes…" });
    setTimeout(() => {
      // A typed request naming no shot: refused — unless the scenario is the
      // old behaviour, which guessed a shot and carried it out.
      Object.assign(v, SCENARIO === "refine-guesses"
        ? { status: "Ready", note: null }
        : { status: "Ready", note: TAP });
    }, 800);
    return json(res, 202, { id: v.id, status: "Pending" });
  }
  if ((m = p.match(/^\/api\/videos\/([^/]+)$/)) && req.method === "GET") {
    const v = state.videos.get(m[1]);
    if (!v) return json(res, 404, { error: "not found" });

    // A render that is slow but alive: the note changes on every poll, which is
    // exactly what a working pipeline looks like when it is going to take
    // longer than the caller's clock. The runner must tell this apart from
    // video-stuck, whose note never changes.
    if (SCENARIO === "video-slow") {
      const age = Date.now() - (v.createdAt ?? Date.now());
      return json(res, 200, {
        ...v,
        status: "Rendering",
        note: `Editing… ${Math.min(95, 40 + Math.floor(age / 1000) * 5)}%`,
      });
    }
    return json(res, 200, v);
  }
  if ((m = p.match(/^\/api\/videos\/([^/]+)$/)) && req.method === "DELETE") {
    // Logged, not just applied: the self-test asserts that a still-rendering
    // video is NOT deleted, and "no DELETE arrived" is the only honest way to
    // check that from outside.
    process.stdout.write(`STUB deleted video ${m[1]}\n`);
    state.videos.delete(m[1]); res.writeHead(204); return res.end();
  }

  if (p === "/api/displays/screens" && req.method === "POST") {
    const id = `screen-${state.screens.size + 1}`;
    state.screens.add(id);
    return json(res, 201, { id, name: body?.name });
  }
  if ((m = p.match(/^\/api\/displays\/screens\/([^/]+)\/pairing-code$/))) {
    return json(res, 200, { pairingCode: "ABC123" });
  }
  if (p === "/api/displays/playlists" && req.method === "POST") {
    const video = body?.items?.find?.((i) => i.kind === "Video");
    if (video) {
      const v = state.videos.get(video.refId);
      if (!v || v.status !== "Ready") {
        return json(res, 400, { error: "That video is not ready yet. Only finished videos can go on a screen." });
      }
      state.playlistVideo = video.refId;
    }
    return json(res, 201, { id: "playlist-1", name: body?.name, items: body?.items ?? [] });
  }
  if ((m = p.match(/^\/api\/displays\/playlists\/([^/]+)$/)) && req.method === "DELETE") {
    res.writeHead(204); return res.end();
  }
  if ((m = p.match(/^\/api\/displays\/screens\/([^/]+)\/playlist$/)) && req.method === "PUT") {
    return json(res, 200, { id: m[1], playlistId: body?.playlistId ?? null });
  }
  if ((m = p.match(/^\/api\/displays\/screens\/([^/]+)$/)) && req.method === "DELETE") {
    state.screens.delete(m[1]); res.writeHead(204); return res.end();
  }

  json(res, 404, { error: `stub has no route for ${req.method} ${p}` });
});

server.listen(PORT, () => {
  process.stdout.write(`stub listening on ${PORT} (scenario=${SCENARIO})\n`);
});


/** A minimal canvas that meets the house-style rules the smoke run checks. */
function houseCanvas() {
  const base = { rotation: 0, scale: { x: 1, y: 1 }, opacity: 1, draggable: true };
  return {
    width: 1240, height: 1754, background: "#0b2e59",
    shapes: [
      { ...base, id: "headline", kind: "text", position: { x: 90, y: 120 }, zIndex: 1,
        text: "Walk-in flu clinic", fontSize: 112, fill: "#ffffff", width: 960 },
      { ...base, id: "photo", kind: "image", position: { x: 570, y: 530 }, zIndex: 2,
        blobKey: "/api/assets/templates/0123456789abcdef0123456789abcdef.png", width: 600, height: 600 },
      { ...base, id: "pharmacy-booking-qr", kind: "image", position: { x: 700, y: 1365 }, zIndex: 3,
        blobKey: "/api/brand-kit/booking-qr", width: 170, height: 170 },
    ],
  };
}

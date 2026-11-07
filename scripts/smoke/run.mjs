#!/usr/bin/env node
/**
 * Pre-production smoke: one cheap call through every paid door, against a
 * deployment that is actually running.
 *
 * <b>Why this exists.</b> The unit, integration and browser suites all passed
 * while production was broken, repeatedly and in ways that were invisible to
 * them by construction:
 *
 *   · a five-day-old container served a 404 for an endpoint that had shipped
 *     days earlier — the tests run against source, the deployment runs an image;
 *   · Anthropic began rejecting `temperature`, and Tavily changed its return
 *     shape — the suites mock both providers, correctly, so a provider changing
 *     its contract cannot fail a test;
 *   · a `$push` onto an explicitly-null field failed — the .NET side and the
 *     Python worker share a document and are tested separately, never together;
 *   · a raw `TypeError` was rendered onto a pharmacist's screen.
 *
 * Every one of those is caught here, because this talks to the running web, API
 * and ai-service over HTTP and reads what a customer would read.
 *
 * <b>It is deliberately not Playwright.</b> One command, no browser, no display
 * server — so it runs on a laptop and in CI the same way, and so a failure
 * points at an API rather than at a selector. The one place a browser would
 * matter is the session cookie, and that is covered by driving the web app's
 * own BFF login instead.
 *
 *   pnpm smoke
 *
 * Environment (same names the e2e script uses):
 *
 *   API_URL   default http://localhost:$API_HOST_PORT, else :5050
 *   AI_URL    default http://localhost:$AI_HOST_PORT,  else :8000
 *   WEB_URL   default http://localhost:$WEB_HOST_PORT, else :3000
 *             (compose publishes web on 9090; `pnpm dev:web` serves 3000)
 *   SMOKE_EMAIL / SMOKE_PASSWORD   the account to sign in as; falls back to
 *                                  SUPERADMIN_EMAIL / SUPERADMIN_PASSWORD
 *   SMOKE_EMAIL_TO                 where the one campaign email is sent
 *   SMOKE_VIDEO_TIMEOUT_MS         default 300000 (five minutes)
 *   SMOKE_REQUEST_TIMEOUT_MS       default 120000, per HTTP call
 *   SMOKE_KEEP=1                   keep what it created, for inspection
 *   SMOKE_READINESS_TOKEN          the API's Readiness__Token / ai-service's
 *                                  READINESS_TOKEN: checks every dependency from
 *                                  inside (database, storage, Stripe, Mailjet,
 *                                  Meta, Google, Anthropic, Pexels, ElevenLabs…)
 *   SMOKE_RESET_EMAIL=0            skip the password-reset check (it sends one
 *                                  real reset email to SMOKE_EMAIL)
 *
 * Those are read from the repo-root `.env` when it exists, so `pnpm smoke`
 * works with no arguments. Anything already in the real environment wins over
 * the file, so `API_URL=https://… pnpm smoke` still points somewhere else for
 * one run.
 *
 * Exit code is 0 unless something FAILED. A WARN is a thing worth reading that
 * is not a reason to hold a release — see `classifyFailure` for the one
 * judgement call in here.
 */

import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";

/* ── env file ────────────────────────────────────────────────────────── */

/**
 * Reads the repo-root `.env` into `process.env`, without a dependency.
 *
 * <b>Why by hand.</b> The root package.json has exactly one devDependency and
 * no `dotenv`. Adding a package so a test script can read a file it could read
 * itself is a poor trade: the parsing below is twenty lines, and a smoke test
 * that fails because an install is stale would be its own kind of joke.
 *
 * <b>Why the real environment wins.</b> A developer's `.env` points at their
 * laptop. `API_URL=https://…-test.azurewebsites.net pnpm smoke` must reach the
 * deployment they named, not silently run against localhost and report that
 * the release is fine — the failure mode there is a green line about the wrong
 * machine, which is worse than a red one. Same reason the self-test passes its
 * stub URLs explicitly: it must not depend on what is in anybody's `.env`.
 *
 * Deliberately simple: `KEY=value`, one per line, `#` comments, optional
 * quotes, optional `export` prefix. No interpolation, no multi-line values. If
 * a value needs more than this, pass it on the command line.
 */
function loadEnvFile() {
  if (process.env.SMOKE_NO_ENV_FILE === "1") return null;

  const here = dirname(fileURLToPath(import.meta.url));
  const path = process.env.SMOKE_ENV_FILE
    ? resolve(process.env.SMOKE_ENV_FILE)
    : join(here, "..", "..", ".env");

  let text;
  try {
    text = readFileSync(path, "utf8");
  } catch {
    // No .env is a normal way to run this — against a deployment you pass the
    // URLs yourself. Not finding one is not worth a warning.
    return null;
  }

  let applied = 0;
  for (const raw of text.split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;

    const eq = line.indexOf("=");
    if (eq <= 0) continue;

    const key = line.slice(0, eq).replace(/^export\s+/, "").trim();
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) continue;

    let value = line.slice(eq + 1).trim();
    if (
      (value.startsWith('"') && value.endsWith('"') && value.length > 1) ||
      (value.startsWith("'") && value.endsWith("'") && value.length > 1)
    ) {
      value = value.slice(1, -1);
    } else {
      // Trailing comment, only when there is whitespace before the #, so a
      // value that legitimately contains one (a password, a connection string)
      // survives.
      value = value.replace(/\s+#.*$/, "");
    }

    // Set, never overwrite: the real environment is the more specific
    // instruction and the caller typed it on purpose.
    if (process.env[key] === undefined) {
      process.env[key] = value;
      applied++;
    }
  }
  return { path, applied };
}

const ENV_FILE = loadEnvFile();

/**
 * Where a service is, when nobody said.
 *
 * The host port falls back to the compose variable rather than a literal,
 * because those two disagree and the disagreement is not obvious: compose
 * publishes the web app on `WEB_HOST_PORT`, which defaults to **9090**, while
 * `pnpm dev:web` and the Playwright config use **3000**. A hard-coded 3000
 * here means a compose stack fails the BFF step with "connection refused" and
 * the operator goes looking for a bug in the web app. Reading the same variable
 * compose reads keeps one answer to the question.
 */
const url = (explicit, portVar, fallbackPort) =>
  (explicit || `http://localhost:${process.env[portVar] || fallbackPort}`).replace(/\/$/, "");

const API = url(process.env.API_URL, "API_HOST_PORT", 5050);
const AI = url(process.env.AI_URL, "AI_HOST_PORT", 8000);
const WEB = url(process.env.WEB_URL, "WEB_HOST_PORT", 3000);

const EMAIL =
  process.env.SMOKE_EMAIL || process.env.SUPERADMIN_EMAIL || "superadmin@sonar.local";
const PASSWORD =
  process.env.SMOKE_PASSWORD || process.env.SUPERADMIN_PASSWORD || "ChangeMe-Dev-Only-123";
const EMAIL_TO = process.env.SMOKE_EMAIL_TO || "timothy.babalola009@gmail.com";

/**
 * Is this a real deployment rather than somebody's laptop?
 *
 * One rule depends on it: Edge-TTS narration is correct locally and fatal when
 * hosted, because Microsoft's consumer speech service is not served to
 * datacentre networks. Inferred from the API address, which is right in every
 * real case — and overridable with `SMOKE_HOSTED=1`, because the self-test has
 * to exercise the hosted branch against a stub on loopback.
 */
const HOSTED =
  process.env.SMOKE_HOSTED === "1" ||
  !/localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0/.test(API);

// Five minutes, not three.
//
// A real local render measured 3m28s end to end — 208 seconds against a 180
// second clock — so the default was reporting a working pipeline as broken. The
// encode alone is 91s of that at 1080x1920, and a slower machine or a longer
// script makes it worse. A timeout wants headroom over the realistic case, not
// over the best one.
const VIDEO_TIMEOUT_MS = Number(process.env.SMOKE_VIDEO_TIMEOUT_MS || 300_000);

// What the product says a video is: 15 seconds, in the create dialog, in
// CreateVideoRequest's default and in the domain entity. The smoke sends no
// duration, so this is what it is asking for.
const ORDERED_VIDEO_SECONDS = Number(process.env.SMOKE_VIDEO_SECONDS || 15);
const CAMPAIGN_TIMEOUT_MS = Number(process.env.SMOKE_CAMPAIGN_TIMEOUT_MS || 90_000);
const KEEP = process.env.SMOKE_KEEP === "1";

const tty = process.stdout.isTTY;
const C = tty
  ? { r: "\x1b[31m", g: "\x1b[32m", y: "\x1b[33m", b: "\x1b[1m", d: "\x1b[2m", n: "\x1b[0m" }
  : { r: "", g: "", y: "", b: "", d: "", n: "" };

/* ── results ─────────────────────────────────────────────────────────── */

const results = [];
let token = null;
const cookies = new Map();
const created = { posterIds: [], videoIds: [], screenIds: [], playlistIds: [] };

/**
 * The video the render step finished, for the refine step to change:
 * `{ id, seconds }`, or null if there was none. The refine needs a video that is
 * Ready and whose length was measured, so it can prove the voice was reused.
 */
let readyVideo = null;

function record(status, name, detail = "") {
  results.push({ status, name, detail });
  const tag =
    status === "PASS" ? `${C.g}PASS${C.n}`
    : status === "WARN" ? `${C.y}WARN${C.n}`
    : status === "SKIP" ? `${C.d}SKIP${C.n}`
    : `${C.r}FAIL${C.n}`;
  process.stdout.write(`  ${tag}  ${name}${detail ? `\n        ${C.d}${detail}${C.n}` : ""}\n`);
}
const pass = (n, d) => record("PASS", n, d);
const warn = (n, d) => record("WARN", n, d);
const fail = (n, d) => record("FAIL", n, d);
const skip = (n, d) => record("SKIP", n, d);

/**
 * "Still here, waiting on something slow."
 *
 * An AI design takes tens of seconds and printed nothing while it ran, so the
 * script looked hung and got ctrl-C'd — which kills the report and loses every
 * result already gathered. Saying what it is waiting for costs one line.
 */
const working = (text) => process.stdout.write(`  ${C.d}··    ${text}${C.n}\n`);

function section(title) {
  process.stdout.write(`\n${C.b}── ${title} ${"─".repeat(Math.max(0, 58 - title.length))}${C.n}\n`);
}

/**
 * Does this message look like it escaped from our code rather than being
 * written for a person?
 *
 * This is the rule that turns "the video failed" into a release decision. A
 * provider having a bad afternoon is not a deploy blocker; a Python traceback
 * on a pharmacist's screen is, because it means an exception reached the
 * customer unhandled — which is exactly what happened with
 * "expected string or bytes-like object, got 'list'".
 */
const TECHNICAL = [
  /Traceback \(most recent call last\)/i,
  /\b(TypeError|ValueError|AttributeError|KeyError|IndexError|RuntimeError)\b/,
  /\bNullReferenceException\b|\bSystem\.[A-Za-z]+Exception\b/,
  /\bat [A-Za-z_][\w.]*\(.*\) in .*:line \d+/,
  /File "\/[^"]+", line \d+/,
  /object has no attribute/i,
  /expected string or bytes-like object/i,
  /\bunhandled exception\b/i,
];
const looksTechnical = (text) =>
  typeof text === "string" && TECHNICAL.some((re) => re.test(text));

/* ── http ────────────────────────────────────────────────────────────── */

function rememberCookies(res) {
  const raw = res.headers.getSetCookie?.() ?? [];
  for (const line of raw) {
    const [pair] = line.split(";");
    const idx = pair.indexOf("=");
    if (idx > 0) cookies.set(pair.slice(0, idx).trim(), pair.slice(idx + 1).trim());
  }
}
const cookieHeader = () =>
  [...cookies].map(([k, v]) => `${k}=${v}`).join("; ");

/**
 * Every call is on a clock.
 *
 * Node's fetch will wait about five minutes before giving up, and a script that
 * sits silently on one request for five minutes is indistinguishable from a
 * hung script — so the operator reaches for ctrl-C, and the run produces no
 * report at all. A door that does not answer *is* the finding here, so it needs
 * to become a FAIL with a sentence, quickly, rather than a frozen terminal.
 *
 * The AI calls are the slow ones (a design is tens of seconds), hence a default
 * generous enough for them and a per-call override for the rest.
 */
const REQUEST_TIMEOUT_MS = Number(process.env.SMOKE_REQUEST_TIMEOUT_MS || 120_000);

/**
 * Connection failures that say nothing about the deployment: the network
 * between this machine and the service dropped or never connected.
 * 6 November: ECONNRESET on one call, a connect timeout on another, and a
 * video download cut off mid-body ("terminated") — three FAILs and "Not safe
 * to ship" for a deployment whose every feature worked.
 */
const NETWORK_BLIP = /ECONNRESET|UND_ERR_CONNECT_TIMEOUT|UND_ERR_SOCKET|ETIMEDOUT|EAI_AGAIN|ENOTFOUND|ECONNREFUSED|EPIPE|terminated|other side closed|socket hang up/i;

/** Failed before anything reached the server, so any method may be sent again. */
const NOT_SENT = /UND_ERR_CONNECT_TIMEOUT|EAI_AGAIN|ENOTFOUND|ECONNREFUSED/i;

/** Requests that only succeeded on a second try; reported once at the end. */
const networkRetries = [];

/**
 * One request, retried once after a network blip when that is safe: any GET,
 * or any method when the connection never opened. A POST that was cut off
 * after it was sent is not repeated — it may have done its work.
 */
async function http(url, options = {}) {
  const first = await httpOnce(url, options);
  const method = options.method ?? "GET";
  if (!first.transportError || first.timedOut || !NETWORK_BLIP.test(first.transportError)) return first;
  if (method !== "GET" && !NOT_SENT.test(first.transportError)) return first;

  await sleep(3_000);
  const second = await httpOnce(url, options);
  if (!second.transportError) networkRetries.push(`${method} ${new URL(url).pathname} (${first.transportError})`);
  return second;
}

async function httpOnce(
  url,
  {
    method = "GET", body, auth = false, useCookies = false, accept,
    headers: extraHeaders = {}, timeoutMs = REQUEST_TIMEOUT_MS,
  } = {},
) {
  const headers = { ...extraHeaders };
  // FormData sets its own Content-Type, and it has to: the header carries the
  // multipart boundary, and one that does not match the body is unparseable
  // upstream. Naming it here would overwrite the boundary fetch generates.
  const isForm = typeof FormData !== "undefined" && body instanceof FormData;
  if (body !== undefined && !isForm) headers["Content-Type"] = "application/json";
  if (auth && token) headers.Authorization = `Bearer ${token}`;
  if (useCookies && cookies.size) headers.Cookie = cookieHeader();
  if (accept) headers.Accept = accept;

  let res;
  try {
    res = await fetch(url, {
      method,
      headers,
      body: body === undefined ? undefined : isForm ? body : JSON.stringify(body),
      redirect: "manual",
      signal: AbortSignal.timeout(timeoutMs),
    });
  } catch (e) {
    // A service being down is an ordinary outcome for a smoke test, not an
    // exception. Returning it as a result keeps every caller on one path and
    // — the point — keeps a stack trace off the operator's screen, which is
    // the same fault this script exists to catch in the product.
    const timedOut = e.name === "TimeoutError" || e.name === "AbortError";
    return {
      status: 0,
      ok: false,
      type: "",
      payload: null,
      bytes: null,
      transportError: timedOut
        ? `no answer within ${Math.round(timeoutMs / 1000)}s`
        : e.cause?.code || e.message || "connection failed",
      timedOut,
      headers: new Headers(),
    };
  }
  rememberCookies(res);

  const type = res.headers.get("content-type") || "";
  let payload = null;
  let bytes = null;
  // `application/problem+json` too, which does NOT contain "application/json"
  // as a substring — ASP.NET answers validation and size failures with it, and
  // treating those as plain text put a truncated JSON blob in the report where
  // an error message should have been.
  if (type.includes("application/json") || type.includes("+json")) {
    payload = await res.json().catch(() => null);
  } else if (type.startsWith("image/") || type.includes("pdf") || type.includes("video")) {
    try {
      bytes = Buffer.from(await res.arrayBuffer());
    } catch (e) {
      // The answer started and the connection dropped mid-file. Reported as
      // a transport failure like any other, not thrown: thrown, it ended the
      // whole step as "unexpected error: terminated".
      return {
        status: 0, ok: false, type, payload: null, bytes: null,
        transportError: e.cause?.code || e.message || "connection dropped mid-download",
        timedOut: e.name === "TimeoutError" || e.name === "AbortError",
        headers: res.headers,
      };
    }
  } else {
    payload = await res.text().catch(() => null);
  }
  return { status: res.status, ok: res.ok, type, payload, bytes, headers: res.headers };
}

const errorOf = (r) =>
  (r.transportError ? `could not reach it (${r.transportError})` : null) ||
  (r.payload && typeof r.payload === "object" && r.payload.error) ||
  (typeof r.payload === "string" ? r.payload.slice(0, 200) : "") ||
  `HTTP ${r.status}`;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Failure notes that mean "this deployment is not set up to send", not "the provider hiccuped". */
const MISCONFIGURED = /credentials (are )?missing|UseMock|no from-address|not configured|sender .*not (validated|verified)/i;

/**
 * .NET writes seven fractional digits and, for a DateTime of unspecified kind,
 * no zone. Trimmed to milliseconds and read as UTC either way, so a timestamp
 * is never misread as local time by however many hours this machine is off.
 */
const parseUtc = (value) => {
  if (typeof value !== "string") return NaN;
  const trimmed = value.replace(/(\.\d{3})\d+/, "$1");
  return Date.parse(/[zZ]|[+-]\d{2}:?\d{2}$/.test(trimmed) ? trimmed : `${trimmed}Z`);
};

/**
 * Turns a terminal failure into PASS / WARN / FAIL.
 *
 * Ready is a pass. Failed with a sentence a person could act on is a warning —
 * providers do fall over, and blocking every release on a third party's bad
 * minute would train everyone to ignore this script. Failed with a traceback is
 * a failure, because that is our bug reaching a customer. Still running when
 * the clock runs out is also a failure: "stuck" was the symptom of the
 * usageAttempts bug, where the failure path could not even record the failure.
 */
function classifyFailure(kind, note) {
  if (looksTechnical(note)) {
    return ["FAIL", `${kind} failed with a raw error — this reaches the customer: ${note}`];
  }
  return ["WARN", `${kind} failed, with a human message: ${note || "(no note)"}`];
}

/**
 * How long a downloaded MP4 actually is, in seconds.
 *
 * <b>Why the smoke parses this itself.</b> "4.2 MB" cannot answer "is the video
 * the length we sell?" — and that is now the question, because the scriptwriter
 * decides the duration (the clip is exactly as long as its narration) and a
 * prompt bug had it producing 27 seconds for a 15-second product. Size is a
 * proxy that moves with bitrate, content and codec; duration is the fact.
 *
 * Reads the `mvhd` box out of `moov`: ~25 lines, no dependency, and the bytes
 * are already in hand from the download step. Returns null rather than throwing
 * — a duration we cannot read must never be the reason a smoke run dies.
 */
/**
 * A real PNG, built here rather than committed as a fixture.
 *
 * <b>It has to actually decode.</b> The whole point of the attach step is to
 * find out whether a picture a pharmacist supplies reaches the finished video,
 * and the worker's answer to a file ffmpeg cannot read is to skip that beat and
 * carry on — by design. A fake PNG would therefore be *correctly* dropped, the
 * diagnostic would fill with a genuine reason, and the check would fail for a
 * reason that has nothing to do with the feature.
 *
 * So: real IHDR, real zlib-compressed scanlines, real CRCs. Built rather than
 * committed because a binary fixture in the repository is a thing nobody can
 * review and everybody assumes somebody checked.
 */
function pngBytes(width = 64, height = 64) {
  const crcTable = [];
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crcTable[n] = c >>> 0;
  }
  const crc32 = (buf) => {
    let c = 0xffffffff;
    for (const byte of buf) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, data) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "latin1"), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([len, body, crc]);
  };

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;  // bit depth
  ihdr[9] = 2;  // colour type: truecolour RGB
  // 10–12: compression, filter, interlace — all zero, all the only valid value.

  // One filter byte per row, then RGB triples. A flat teal, because what it
  // looks like does not matter and a gradient would only make this longer.
  const raw = Buffer.alloc(height * (1 + width * 3));
  for (let y = 0; y < height; y++) {
    const row = y * (1 + width * 3);
    raw[row] = 0; // filter: none
    for (let x = 0; x < width; x++) {
      const at = row + 1 + x * 3;
      raw[at] = 0x0d;
      raw[at + 1] = 0x94;
      raw[at + 2] = 0x88;
    }
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", zlib.deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

/**
 * Uploads one of "the pharmacy's own" pictures, and checks an oversized one is
 * refused with a sentence that says so.
 *
 * Returns `{ good, broken }` blob keys, or nulls when the upload did not
 * happen — the caller then creates an ordinary video, because a render that
 * fails for want of a picture would report the wrong problem.
 *
 * <b>Two pictures, and the second one is deliberately not a picture.</b>
 * Checking that a good one survives cannot be done by looking for silence: a
 * worker that ignores attachments entirely is silent too, and that is exactly
 * what an ai-service built before this feature does. Silence read as success is
 * how a smoke run passes on a deployment where the feature does not exist.
 *
 * So one file is a real PNG and one is garbage wearing a PNG content type. A
 * working render says it skipped the second and nothing about the first. A
 * render that says nothing at all never looked.
 *
 * <b>The refusal case is the one that shipped broken.</b> The size cap is
 * enforced by `[RequestSizeLimit]`, which rejects the request in the framework
 * before the action runs, so the API answers a 20MB photograph with a bare 413
 * and an empty body. The browser now refuses these before sending — but this
 * asks the API directly, which is the only way to find out whether the door
 * itself still explains itself.
 */
async function attachOwnPicture() {
  const uploadForm = (bytes, name) => {
    const form = new FormData();
    form.append("file", new Blob([bytes], { type: "image/png" }), name);
    return form;
  };

  // <b>The good upload goes first, and the order is the point.</b>
  //
  // A deployment can refuse an oversized body two ways: a clean 413, or by
  // resetting the connection mid-upload — Kestrel does the second often enough
  // that treating a transport error as "the API is broken" would warn on every
  // run against a real deployment. Warning every run is how a script teaches
  // people to stop reading it.
  //
  // Doing a small upload first settles that: if it worked, the endpoint and the
  // connection are fine, so whatever happens to the oversized one is the cap
  // doing its job.
  const upload = await http(`${API}/api/videos/images`, {
    method: "POST",
    auth: true,
    body: uploadForm(pngBytes(64, 64), "smoke-shopfront.png"),
  });

  const key = upload.payload?.blobKey;
  if (!upload.ok || !key) {
    warn(
      "attach own picture",
      `${errorOf(upload)} — the video will be made without one, so the rest of ` +
        `this section still runs.`,
    );
    // No cap probe. With the endpoint not answering, its result would say
    // nothing about the cap and everything about the endpoint, which the line
    // above has already said.
    return { good: null, broken: null };
  }

  // Passes every check the upload endpoint makes — non-empty, under the cap,
  // declared image/png — and is not a picture. Only ffmpeg can tell, and only
  // at render time, which is the whole point.
  const damaged = await http(`${API}/api/videos/images`, {
    method: "POST",
    auth: true,
    body: uploadForm(Buffer.from("not a picture".repeat(64)), "smoke-damaged.png"),
  });
  const brokenKey = damaged.payload?.blobKey ?? null;
  pass(
    "attach own picture",
    `${key} (a real 64×64 PNG)` +
      (brokenKey ? ", plus one damaged file the render must notice" : ""),
  );

  // Just over the cap rather than comfortably over: it tests the boundary, and
  // it is a megabyte less to push up a home uplink on every run.
  const oversize = await http(`${API}/api/videos/images`, {
    method: "POST",
    auth: true,
    body: uploadForm(Buffer.alloc(8 * 1024 * 1024 + 64 * 1024), "too-big.png"),
  });

  if (oversize.ok) {
    fail(
      "oversized image refused",
      "the API accepted an upload over the 8MB cap — the limit is not being applied",
    );
  } else {
    const said = errorOf(oversize) || "";
    // "Too big" without a size leaves the pharmacist guessing how much to
    // shrink it by, and that is exactly what a bare 413 gives them.
    //
    // A digit immediately before the unit, not `\bMB\b`: that was the first
    // attempt and it never matched, because `8MB` has no word boundary between
    // the digit and the M. Every correct message was reported as saying
    // nothing — a check that fails on exactly the input it was written to
    // accept, which is the shape a self-test exists to catch.
    if (/\d\s*MB/i.test(said)) {
      pass("oversized image refused", said);
    } else if (oversize.transportError) {
      // Refused by hanging up. The cap held — the small upload above proves the
      // connection is fine — but nothing came back to explain it.
      warn(
        "oversized image refused",
        `the connection was dropped rather than answered (${oversize.transportError}). ` +
          "The cap held, but a caller is told nothing about why.",
      );
    } else {
      warn(
        "oversized image refused",
        `refused with ${oversize.status}, but the message does not mention a size: ` +
          `${said || "(no message at all)"}. A pharmacist is told only that it failed.`,
      );
    }
  }

  return { good: key, broken: brokenKey };
}

function mp4DurationSeconds(buf) {
  const readBoxes = (start, end) => {
    let at = start;
    const boxes = [];
    while (at + 8 <= end) {
      let size = buf.readUInt32BE(at);
      const type = buf.toString("latin1", at + 4, at + 8);
      let header = 8;
      if (size === 1) {
        if (at + 16 > end) break;
        // 64-bit largesize. Beyond 2^53 is not a video anybody rendered.
        size = Number(buf.readBigUInt64BE(at + 8));
        header = 16;
      } else if (size === 0) {
        size = end - at;
      }
      if (size < header || at + size > end) break;
      boxes.push({ type, body: at + header, end: at + size });
      at += size;
    }
    return boxes;
  };

  try {
    const moov = readBoxes(0, buf.length).find((b) => b.type === "moov");
    if (!moov) return null;

    const mvhd = readBoxes(moov.body, moov.end).find((b) => b.type === "mvhd");
    if (!mvhd) return null;

    const version = buf.readUInt8(mvhd.body);
    // version 0: [ver+flags 4][created 4][modified 4][timescale 4][duration 4]
    // version 1: the two dates and the duration widen to 8 bytes.
    const timescale = version === 1
      ? buf.readUInt32BE(mvhd.body + 20)
      : buf.readUInt32BE(mvhd.body + 12);
    const duration = version === 1
      ? Number(buf.readBigUInt64BE(mvhd.body + 24))
      : buf.readUInt32BE(mvhd.body + 16);

    if (!timescale || !duration) return null;
    return duration / timescale;
  } catch {
    return null;
  }
}

/* ── steps ───────────────────────────────────────────────────────────── */

/** Set by reachability(); null until it has run. */
let aiReachable = null;

async function reachability() {
  section("Reachability");

  for (const [name, url] of [["api", `${API}/health`], ["ai-service", `${AI}/health`]]) {
    const r = await http(url);
    if (name === "ai-service") aiReachable = r.ok;
    if (!r.ok) {
      const local = /^https?:\/\/(localhost|127\.0\.0\.1|\[::1\])(:|\/|$)/.test(AI);
      fail(
        `${name} /health`,
        `${errorOf(r)} at ${url}` +
          (name === "ai-service" && local
            ? ". If you just rebuilt it, it is probably crash-looping: docker logs --tail 50 acme-ai"
            : ""),
      );
      continue;
    }

    // The build id is the answer to "am I looking at the code I just pushed?".
    // A container that boots, answers /health and is three releases behind has
    // already cost this project a day.
    const build = r.payload && typeof r.payload === "object" ? r.payload.build : null;
    builds[name] = build || null;
    pass(`${name} /health`, build ? `build=${build}` : url);
    if (name === "ai-service" && build) aiBuildFreshness(build);
  }

  if (HOSTED && builds.api !== undefined) await apiEnvironment();

  // 200 or a redirect to /login both mean the web tier is serving.
  let web = await http(`${WEB}/`, { accept: "text/html", timeoutMs: 15_000 });
  // An idle Azure App Service (no Always On) takes 30–90s to wake a Next.js
  // container. Give a hosted web one longer try before calling it down, and
  // say so — a first visitor waits exactly that long.
  let slow = false;
  // Timeouts only: a wrong hostname or refused connection fails at once and
  // would fail the same way after 90s.
  if (HOSTED && web.timedOut) {
    console.log("  ··    web did not answer in 15s — waiting up to 90s in case it is waking up");
    web = await http(`${WEB}/`, { accept: "text/html", timeoutMs: 90_000 });
    slow = true;
  }
  if (web.status >= 200 && web.status < 400) {
    pass("web responds", `${WEB} -> ${web.status}`);
    if (slow) {
      warn(
        "web wakes slowly",
        "the first request took over 15s, so the app was asleep. Turn on Always On " +
          "(App Service → Configuration → General settings) so pharmacists don't wait for it.",
      );
    }
    const health = await http(`${WEB}/api/health`, { timeoutMs: 15_000 });
    builds.web = health.ok && health.payload && typeof health.payload === "object"
      ? health.payload.build || null
      : undefined; // undefined: the web app has no /api/health at all
    buildConsistency();
    return;
  }

  // Nothing there. Before blaming the deployment, check the *other* port this
  // repo uses, because the two disagree and always have: docker compose
  // publishes the web app on WEB_HOST_PORT (9090) while `pnpm dev:web` serves
  // 3000. "Connection refused" sends people looking for a broken container when
  // the app is running perfectly one port over, so if it is, say so and say
  // exactly what to change.
  const elsewhere = await probeOtherWebPort();
  fail(
    "web responds",
    elsewhere
      ? `${errorOf(web)} at ${WEB} — but the web app is answering at ${elsewhere}. ` +
          `Set WEB_URL=${elsewhere} in .env. (compose serves 9090; pnpm dev:web serves 3000.)`
      : `${errorOf(web)} at ${WEB}. Start the web app, or set WEB_URL to where it is.`,
  );
}

/**
 * Is the running ai-service built from the code in this checkout?
 *
 * The route list below catches an image missing an endpoint; it cannot catch
 * an image whose endpoints all exist but behave as they did a week ago. That
 * is how a 29 September image answered every route and still returned
 * pre-house-style posters (hot-linked photo, no booking QR, translucent
 * overlay) on 5 October — a failure that reads as "the model ignored the
 * rules" when the rules simply were not in the image.
 *
 * A build without BUILD_ID reports `local-<newest .py mtime>-<file count>f`
 * (app/main.py `_source_stamp`). The same two numbers are computed here from
 * apps/ai-service/app. The container prints its local time and Docker keeps
 * file mtimes, so the time is compared with a 15-hour allowance for time
 * zones; the file count is compared exactly. Only applies when the checkout
 * is present and the build is a local one — a deployment with a real BUILD_ID
 * (a commit) is not judged against whatever happens to be on this disk.
 */
function aiBuildFreshness(build) {
  const m = /^local-(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})-(\d+)f$/.exec(build);
  if (!m) return;

  // Only an ai-service on this machine was built from this disk. A remote one
  // built without BUILD_ID also says "local-…", and judging it against a
  // checkout you are still editing would fail a deployment that is fine.
  let host = "";
  try { host = new URL(AI).hostname; } catch { return; }
  if (!["localhost", "127.0.0.1", "::1", "[::1]"].includes(host)) return;
  if (process.env.SMOKE_SKIP_BUILD_CHECK === "1") {
    skip("ai-service is this checkout", "SMOKE_SKIP_BUILD_CHECK=1");
    return;
  }

  const appDir = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "apps", "ai-service", "app");
  let newest = 0;
  let count = 0;
  try {
    for (const entry of readdirSync(appDir, { recursive: true, withFileTypes: true })) {
      if (!entry.isFile() || !entry.name.endsWith(".py")) continue;
      const dir = entry.parentPath ?? entry.path;
      if (dir.includes("__pycache__")) continue;
      count += 1;
      newest = Math.max(newest, statSync(join(dir, entry.name)).mtimeMs);
    }
  } catch {
    return; // no checkout here — nothing to compare with
  }
  if (!count) return;

  const [, y, mo, d, h, mi, s, files] = m;
  const builtAt = Date.UTC(+y, +mo - 1, +d, +h, +mi, +s);
  const behindHours = (newest - builtAt) / 3_600_000;
  const fileDelta = count - Number(files);

  if (behindHours > 15 || fileDelta !== 0) {
    const local = new Date(newest).toISOString().slice(0, 16).replace("T", " ");
    fail(
      "ai-service is this checkout",
      `the running image is older than the code: built ${y}-${mo}-${d} ${h}:${mi} with ${files} file(s); ` +
        `the checkout was last changed ${local} UTC and has ${count}. ` +
        "Every later step tests the old image. Rebuild: docker compose up -d --build ai-service",
    );
  } else {
    pass("ai-service is this checkout", `${count} file(s), newest change within the build`);
  }
}

/**
 * Is a deployed API running as Development?
 *
 * /api/diagnostics/jwt answers only in Development (404 everywhere else), so
 * it says which mode the API is in without needing any credential.
 *
 * It matters more than it looks. In Development the API loads
 * appsettings.Development.json — development Mailjet, Meta and Google keys,
 * relaxed rate limits, the published smoke password — and enables the
 * Development-only diagnostics. A production that "worked" in that mode was
 * leaning on development credentials, and stops sending email the moment the
 * file is no longer in the image (security review R3-04 removed it). The
 * failure then looks like "Mailjet credentials missing", far from its cause.
 */
async function apiEnvironment() {
  const r = await http(`${API}/api/diagnostics/jwt`, { timeoutMs: 15_000 });
  if (r.status === 200) {
    fail(
      "api runs as Production",
      "the API is running with ASPNETCORE_ENVIRONMENT=Development. It then expects its secrets " +
        "from appsettings.Development.json, which is no longer shipped — so anything not set as " +
        "an App Service setting (Mailjet keys, Edge__ClientIpKey…) is simply missing. Set " +
        "ASPNETCORE_ENVIRONMENT=Production and every setting in docs/PRODUCTION_CHECKLIST.md.",
    );
  } else if (r.status === 404) {
    pass("api runs as Production", "Development-only diagnostics are off");
  }
  // Anything else (a 5xx, a timeout) is reported by /health already.
}

/** Build ids from each service's health endpoint: a string, null (none set), or undefined (no endpoint). */
const builds = {};

/**
 * Are the services from the same deploy?
 *
 * Each service can be healthy on its own while the deployment is broken where
 * they meet: an API restarted onto the new build behind a web app still on the
 * old one (or the reverse) passes every /health and fails on the first request
 * whose shape changed. That is the "some bits work, some bits don't" a partial
 * ship produces, and nothing else in this script would name it.
 *
 * API and web ship together and share contracts, so a difference there fails.
 * The ai-service is often skipped on purpose (SKIP_AI=1), so a difference there
 * warns. A deployment without build ids at all predates the markers and warns
 * once, with the fix.
 */
function buildConsistency() {
  const { api, web } = builds;
  const ai = builds["ai-service"];

  if (web === undefined) {
    if (HOSTED) {
      warn("services on one build", "the web app has no /api/health — it is older than the build markers. Redeploy with pnpm ship.");
    }
    return;
  }
  if (!api || !web) {
    if (HOSTED) {
      warn(
        "services on one build",
        `build ids missing (api=${api ?? "none"}, web=${web ?? "none"}) — deployed without BUILD_ID. ` +
          "Ship with scripts/deploy.sh so each image says which build it is.",
      );
    }
    return;
  }
  if (api !== web) {
    fail(
      "services on one build",
      `the API is on ${api} and the web app on ${web}. A half-finished deploy: requests whose ` +
        "shape changed between them will fail. Redeploy both (pnpm ship:<env>).",
    );
    return;
  }
  if (ai && ai !== api) {
    warn("services on one build", `API and web on ${api}; the ai-service is on ${ai}. Fine if it was skipped on purpose.`);
    return;
  }
  pass("services on one build", api);
}

/** Is the web app on the port we are not looking at? */
async function probeOtherWebPort() {
  const candidates = ["9090", "3000"].filter((p) => !WEB.endsWith(`:${p}`));
  for (const port of candidates) {
    const guess = WEB.replace(/:\d+$/, `:${port}`);
    if (guess === WEB) continue;
    const r = await http(`${guess}/`, { accept: "text/html", timeoutMs: 4_000 });
    if (r.status >= 200 && r.status < 400) return guess;
  }
  return null;
}

/**
 * The routes this deployment must serve, read from ai-service's own schema.
 *
 * Directly implements "fail if health 404s a route we need". A stale image is
 * the specific thing being caught: it boots, answers /health, and is missing an
 * endpoint that shipped days ago. The .NET side can only report that as "we
 * could not work out what to change just now", which reads like a model
 * problem and is not one.
 */
async function aiRoutes() {
  section("ai-service routes");

  const required = [
    "/api/video/classify-refine",
    "/api/canvas/generate-template",
    // Added when the voice list moved here from the browser. Without it in this
    // list, an ai-service older than that change fails the narration step with
    // "the API could not reach ai-service" — which points at networking or at
    // AiService__BaseUrl, when the truth is a stale image. That misdirection is
    // the exact cost this check exists to avoid.
    "/api/video/narration",
    // The pharmacy photo modes (None / Stock / Upload / AI). An image older
    // than them answers the AI design step with a pre-house-style poster and
    // the photo picker with 404 / 403, none of which says "stale image".
    "/api/canvas/image-options",
    "/api/canvas/stock-photos",
    "/api/canvas/stock-photo",
  ];
  let schema;
  try {
    const r = await http(`${AI}/openapi.json`);
    schema = r.ok ? r.payload : null;
  } catch { schema = null; }

  if (!schema || typeof schema !== "object" || !schema.paths) {
    fail("ai-service exposes its routes", "could not read /openapi.json");
    return;
  }
  const served = Object.keys(schema.paths);
  for (const path of required) {
    if (served.includes(path)) pass(`route ${path}`);
    else {
      fail(
        `route ${path}`,
        "not served by the running image. This is what a stale container looks " +
          "like — rebuild: docker compose up -d --build ai-service",
      );
    }
  }
}

async function signIn() {
  section("Sign in");

  const r = await http(`${API}/api/auth/login`, {
    method: "POST",
    body: { email: EMAIL, password: PASSWORD },
  });
  if (!r.ok || !r.payload?.accessToken) {
    fail("api login", `${errorOf(r)} (as ${EMAIL})`);
    return false;
  }
  token = r.payload.accessToken;
  pass("api login", EMAIL);

  // One root cause should produce one finding. If the web tier is not serving
  // at all, reachability has already said so by name; repeating it here as two
  // more failures buries the real list under the same fact three times.
  if (results.some((x) => x.status === "FAIL" && x.name === "web responds")) {
    skip("web BFF login", "the web tier is not answering — see Reachability above");
    return true;
  }

  // The same sign-in through the web app's own BFF, which is what a browser
  // does. Proves the web tier can reach the API and that the session cookie is
  // set — the half of the stack an API-only smoke would never touch.
  const web = await http(`${WEB}/api/auth/login`, {
    method: "POST",
    body: { email: EMAIL, password: PASSWORD },
    useCookies: true,
  });
  if (!web.ok) {
    fail("web BFF login", errorOf(web));
  } else {
    const me = await http(`${WEB}/api/me/full`, { useCookies: true });
    if (me.ok && me.payload?.profile) pass("web BFF session", `/api/me/full as ${me.payload.email ?? EMAIL}`);
    else fail("web BFF session", `/api/me/full -> ${me.status}. The cookie or the proxy is wrong.`);
  }
  return true;
}

/**
 * Account email: can somebody who forgot their password get back in?
 *
 * Reported from production: "reset emails are not arriving", tried repeatedly.
 * Campaign email worked, so the provider was fine — the failure was somewhere
 * between the forgot-password page and the send, where every outcome looks the
 * same from outside on purpose (the page cannot say "no such account"). This
 * walks that path as a browser does and then asks the API, as the account
 * itself, what actually happened.
 *
 * Two checks:
 *
 *   1. **Can the API tell browsers apart?** If the web app and the API disagree
 *      about the client-address signing key, every user arrives from the web
 *      app's address, and the per-address limits apply to everybody at once:
 *      five reset requests in fifteen minutes, from anyone, then nothing.
 *
 *   2. **Does a reset reach the provider?** A real request, through the web
 *      app, for this account; then the account's own view of the outcome.
 *      Sends one real email to SMOKE_EMAIL. SMOKE_RESET_EMAIL=0 skips it.
 */
async function accountEmail() {
  section("Account email");

  if (results.some((x) => x.status === "FAIL" && x.name === "web responds")) {
    skip("account email", "the web tier is not answering — see Reachability above");
    return;
  }

  // 1. Client address.
  const addr = await http(`${WEB}/api/diagnostics/client-address`, { useCookies: true });
  if (addr.status === 404) {
    warn("users are told apart", "the web app has no /api/diagnostics/client-address — it predates this check. Redeploy.");
  } else if (!addr.ok) {
    warn("users are told apart", `could not ask: ${errorOf(addr)}`);
  } else {
    const v = addr.payload?.verdict;
    if (v === "ok") {
      pass("users are told apart", `the API sees this browser as ${addr.payload.address}`);
    } else if (v === "key_mismatch") {
      fail(
        "users are told apart",
        "the web app signs browser addresses with a key the API does not hold, so every user " +
          "shares one address. Sign-in and password-reset limits then apply to everybody at once " +
          "— a few reset requests from anyone and nobody gets an email. Set CLIENT_IP_SIGNING_KEY " +
          "(web) and Edge__ClientIpKey (API) to the same value.",
      );
    } else if (v === "web_not_signing") {
      (HOSTED ? fail : warn)(
        "users are told apart",
        "the API has a signing key but the web app sends no signature, so every user shares the " +
          "web app's address for rate limits. Set CLIENT_IP_SIGNING_KEY on the web app to the " +
          "API's Edge__ClientIpKey.",
      );
    } else if (v === "api_key_missing") {
      (HOSTED ? warn : skip)(
        "users are told apart",
        "Edge__ClientIpKey is not set on the API: users are told apart, but the address can be " +
          "spoofed by calling the API directly. Set it, and CLIENT_IP_SIGNING_KEY on the web app.",
      );
    } else {
      warn("users are told apart", `unexpected answer: ${JSON.stringify(addr.payload)}`);
    }
  }

  // 2. A real reset.
  if (process.env.SMOKE_RESET_EMAIL === "0") {
    skip("password reset email", "SMOKE_RESET_EMAIL=0");
    return;
  }

  const since = Date.now() - 5_000;
  const ask = await http(`${WEB}/api/auth/forgot-password`, { method: "POST", body: { email: EMAIL } });
  if (ask.status === 429) {
    fail(
      "password reset email",
      "the reset request was refused as too frequent. If you have not been requesting resets, " +
        "every user is sharing one address — see the check above.",
    );
    return;
  }
  if (!ask.ok) {
    fail("password reset email", `the forgot-password page's request failed: ${errorOf(ask)}`);
    return;
  }

  // The send happens inside the request, so the outcome is there by the time
  // it answers; a short poll covers a slow provider round trip.
  let latest = null;
  for (let i = 0; i < 10; i++) {
    const r = await http(`${API}/api/auth/account-email/latest?purpose=PasswordReset`, { auth: true });
    if (r.status === 404) {
      warn("password reset email", "the API has no /api/auth/account-email/latest — it predates this check. Redeploy.");
      return;
    }
    if (r.ok && r.payload && parseUtc(r.payload.requestedAtUtc) >= since) {
      latest = r.payload;
      if (latest.outcome !== "unknown") break;
    }
    await sleep(1_000);
  }

  if (!latest) {
    fail(
      "password reset email",
      `the web app answered ${ask.status}, but no reset reached ${EMAIL}. Nothing was sent. ` +
        "Check the API log for \"Rate limit … refused\" and for \"Password reset requested for …\".",
    );
    return;
  }
  if (latest.outcome === "unknown") {
    // The reset was issued but nothing recorded what the provider said — the
    // send threw before answering, or recording failed. Not proof of failure,
    // but not proof of delivery either.
    warn(
      "password reset email",
      "a reset was issued for this account but its delivery was not recorded. The API log says " +
        "what happened (\"Password reset email …\").",
    );
    return;
  }
  if (latest.outcome === "mocked") {
    // Normal on a laptop; on a real deployment it means nobody gets any email.
    (HOSTED ? fail : warn)(
      "password reset email",
      "the API is in email mock mode (Messaging__UseMock is not \"false\"): the reset was " +
        "logged, not sent. No account email leaves this deployment.",
    );
    return;
  }
  if (latest.outcome !== "accepted") {
    fail(
      "password reset email",
      `the reset reached the account but the email was not accepted (${latest.outcome}). The usual ` +
        "cause is the API's own configuration — no Messaging__Mailjet__ApiKey / SecretKey on the " +
        "App Service that is actually serving, or a from-address Mailjet has not verified — rather " +
        "than Mailjet itself. The API log names it; /api/admin/diagnostics/email lists problems.",
    );
    return;
  }

  // The link must point at the site people actually use.
  let webHost = "";
  try { webHost = new URL(WEB).hostname; } catch { /* ignore */ }
  const linkHost = latest.linkHost ?? "";
  if (HOSTED && (/^(localhost|127\.0\.0\.1)$/.test(linkHost) || (webHost && linkHost !== webHost))) {
    fail(
      "password reset email",
      `accepted, but its link points at ${linkHost || "nothing"} rather than ${webHost}. ` +
        (linkHost && !/^(localhost|127\.0\.0\.1)$/.test(linkHost)
          ? `If ${linkHost} is the address people use, the API is right and WEB_URL is not: ` +
            `re-run with WEB_URL=https://${linkHost}. Otherwise set Public__WebBaseUrl on the API.`
          : "Set Public__WebBaseUrl on the API to the public web address."),
    );
    return;
  }
  pass("password reset email", `accepted by the provider for ${EMAIL}; link goes to ${linkHost}`);
}

/**
 * Social & Ads: can a pharmacy connect Facebook, Instagram and Google here?
 *
 * Reported from production on 21 October: "Social accounts are not set up on
 * this server yet" on a deployment where Facebook had been connected for weeks.
 * The encryption key for stored social tokens had only ever come from the
 * development settings file; once production ran as Production, there was no
 * key — so nobody could connect, and every existing connection was unreadable.
 * Asked of the API exactly as the Social page asks it.
 */
async function socialAvailability() {
  section("Social & Ads");

  const r = await http(`${API}/api/social/availability`, { auth: true });
  if (r.status === 404) { skip("social availability", "this API has no social endpoints"); return; }
  if (!r.ok || !r.payload) { fail("social availability", errorOf(r)); return; }

  if (r.payload.planAllows === false) {
    skip("social availability", r.payload.planReason || "this plan has no social features");
    return;
  }

  for (const p of r.payload.providers ?? []) {
    const name = `${p.kind} can be connected`;
    if (p.available) { await startConnection(p.kind, name); continue; }
    const reason = p.reason || "unavailable";
    if (/encryption key/i.test(reason)) {
      // Not one provider: none of them, and every connection already made.
      fail(
        name,
        `${reason} Set Social__Encryption__ActiveVersion and Social__Encryption__Keys__0__Version / ` +
          "__Key on the API. Use the key the existing connections were saved with, or every " +
          "pharmacy has to reconnect.",
      );
    } else {
      (HOSTED ? warn : skip)(name, reason);
    }
  }
}

/**
 * Starts a real connection to a provider, the way the Connect button does,
 * and stops at the provider's door.
 *
 * From the 22 October production report: the site moved to marketing25.co.uk,
 * the API still built its addresses on the azurewebsites.net one, and every
 * Facebook connection was refused with "Connections must be started from …".
 * "Is Facebook configured" said yes throughout. Only starting one, from the
 * address people actually use, says no.
 *
 * Through the web app, with the browser's own redirect address
 * (`${WEB}/social/callback/<kind>`), so a mismatch between the site and the
 * API's configured address fails here exactly as it fails for a pharmacist.
 * Then the address the provider is sent must carry that same redirect. For
 * Google the consent page itself is fetched: Google checks the client and the
 * redirect before any sign-in, so `redirect_uri_mismatch` or `invalid_client`
 * is visible without an account. Meta checks the redirect only after sign-in,
 * so for Meta the report names the address that must be on its allow-list.
 *
 * Leaves one short-lived OAuth session row behind, which expires on its own.
 */
async function startConnection(kind, name) {
  // Through the web app, so it needs the web tier and its session cookie. A
  // web that did not answer in Reachability (an Azure cold start answers late)
  // would otherwise reach here cookieless and report "Not authenticated" — a
  // second, misleading finding for the same root cause.
  if (results.some((x) => x.status === "FAIL" && x.name === "web responds")) {
    skip(name, "the web tier is not answering — see Reachability above");
    return;
  }

  const callback = `${WEB}/social/callback/${String(kind).toLowerCase()}`;
  const start = await http(`${WEB}/api/social/connections/start`, {
    method: "POST",
    useCookies: true,
    body: { kind, redirectUri: callback },
  });

  if (!start.ok || !start.payload?.authorizationUrl) {
    const message = errorOf(start);
    // On a laptop the web app may be on 3000 or 9090 while the API's address
    // names one of them; that mismatch is real only on a deployment.
    const moved = /must be started from/i.test(message);
    // Older API builds end the address with a full stop, newer with a comma.
    const configured = moved
      ? (message.match(/must be started from (https?:\/\/[^\s,]+)/i)?.[1] ?? "").replace(/[.\/]+$/, "")
      : "";
    (moved && !HOSTED ? warn : fail)(
      name,
      moved
        ? `The API only starts connections from ${configured || "its configured address"}, and this ` +
            `run used WEB_URL=${WEB}. If ${configured || "that"} is the address people use, the API ` +
            `is right: re-run with WEB_URL=${configured || "<that address>"}. Otherwise set ` +
            "Public__WebBaseUrl on the API to the address people use, and add its /social/callback/… " +
            "to the provider's allowed redirect URIs."
        : `starting a connection failed: ${message}`,
    );
    return;
  }

  let auth;
  try { auth = new URL(start.payload.authorizationUrl); } catch {
    fail(name, `the provider address is not a URL: ${start.payload.authorizationUrl}`);
    return;
  }
  const redirect = auth.searchParams.get("redirect_uri");
  if (redirect !== callback) {
    fail(name, `the provider would send people back to ${redirect ?? "nowhere"}, not ${callback}`);
    return;
  }
  // TikTok calls it client_key; everyone else client_id.
  if (!auth.searchParams.get("client_id") && !auth.searchParams.get("client_key")) {
    fail(name, "the provider address carries no client_id (or TikTok client_key)");
    return;
  }

  if (kind === "TikTok") {
    const scopes = (auth.searchParams.get("scope") ?? "").split(/[\s,]+/).filter(Boolean);
    if (!scopes.includes("video.publish")) {
      fail(name, "the TikTok sign-in does not ask for permission to post (video.publish)");
      return;
    }
    // TikTok's sign-in page is drawn by script: a bad client key or an
    // unregistered redirect shows only in a browser.
    pass(name, `starts and returns to ${callback}. TikTok checks the client key and that address only on its ` +
      "own page, so after setting it up connect once by hand (QA script 10a.12). Until TikTok audits the app, " +
      "posts are private and only private TikTok accounts can be posted to.");
    return;
  }

  if (kind === "X") {
    const scopes = (auth.searchParams.get("scope") ?? "").split(/\s+/).filter(Boolean);
    const missing = ["tweet.write", "media.write", "offline.access"].filter((s) => !scopes.includes(s));
    if (missing.length > 0) {
      fail(name, `the X sign-in does not ask for ${missing.join(", ")}: posts with pictures and scheduled posts need them`);
      return;
    }
    if (!auth.searchParams.get("code_challenge") || auth.searchParams.get("code_challenge_method") !== "S256") {
      fail(name, "the X sign-in carries no PKCE challenge (S256); X refuses it");
      return;
    }
    // X's sign-in page is drawn by script: a wrong client id or an
    // unregistered callback shows only in a browser.
    pass(name, `starts and returns to ${callback}. X checks the client id and that address only on its own page, ` +
      "so after setting it up connect once by hand (QA script 10a.18). The readiness check above confirms the client secret.");
    return;
  }

  // YouTube is its own Google consent: upload permission, and nothing for Ads.
  if (kind === "YouTube") {
    const scope = auth.searchParams.get("scope") ?? "";
    if (!scope.includes("youtube.upload")) {
      fail(name, "the YouTube sign-in does not ask for upload permission (youtube.upload)");
      return;
    }
    if (/adwords/.test(scope)) {
      fail(name, "the YouTube sign-in also asks for Google Ads access; it should ask for YouTube only");
      return;
    }
  }

  if (kind === "LinkedIn") {
    await linkedInConsent(name, auth, callback);
    return;
  }

  // Google's consent endpoint (the stub serves the same path on loopback).
  if (/google/i.test(auth.hostname) || auth.pathname.startsWith("/o/oauth2/")) {
    // 2 November: Social__Google__ClientId was "http://…apps.googleusercontent.com".
    // Read from the address itself, so it fails even when Google's error page
    // is drawn by script and says nothing a fetch can read.
    const clientId = auth.searchParams.get("client_id") ?? "";
    if (/:\/\//.test(clientId) || !/\.apps\.googleusercontent\.com$/i.test(clientId) || /\s/.test(clientId)) {
      fail(name, `Social__Google__ClientId is "${clientId}" — it must be just the id from Google Cloud ` +
        "→ Credentials, ending .apps.googleusercontent.com, with no http:// and no spaces");
      return;
    }
    // Google answers a bad client or redirect on the consent page itself.
    const page = await http(auth.toString(), { accept: "text/html", timeoutMs: 15_000 });
    const body = typeof page.payload === "string" ? page.payload : "";
    if (/redirect_uri_mismatch/i.test(body)) {
      fail(name, `Google does not allow ${callback} — add it to the OAuth client's Authorised redirect URIs`);
      return;
    }
    if (page.status === 401 || /invalid_client|OAuth client was not found/i.test(body)) {
      fail(name, "Google does not recognise the OAuth client (Social__Google__ClientId)");
      return;
    }
    if (/invalid_scope/i.test(body)) {
      fail(name, kind === "YouTube"
        ? "Google refused the YouTube scopes. Add youtube.upload and youtube.readonly on the OAuth consent screen."
        : "Google refused the scopes this sign-in asks for. Check the OAuth consent screen.");
      return;
    }
    // Google draws its error pages with script, so the words above may not
    // be in what a fetch sees — but the status is. 2 November: a missing
    // YouTube redirect passed here and failed for the pharmacist.
    if (page.status === 400) {
      fail(name, `Google refused the sign-in before anyone logged in (400). Usually ${callback} is not in the ` +
        "OAuth client's Authorised redirect URIs" + (kind === "YouTube" ? ", or the YouTube scopes are not on the consent screen." : "."));
      return;
    }
    pass(name, kind === "YouTube"
      ? `consent page opens and returns to ${callback}. Uploads also need the YouTube Data API v3 ` +
        "enabled in the Google Cloud project, and the signing-in account listed as a test user until the app is verified."
      : `consent page opens and returns to ${callback}`);
    return;
  }

  pass(
    name,
    `starts and returns to ${callback}. Meta checks that address only after sign-in, so it must be ` +
      "in the app's Facebook Login → Valid OAuth Redirect URIs.",
  );
}

/**
 * LinkedIn's sign-in page, opened without signing in.
 *
 * LinkedIn answers some problems before anyone logs in: a scope the app has
 * no product for comes straight back to the callback as
 * `error=unauthorized_scope_error` — the usual cause being Pages switched on
 * (Social__LinkedIn__PagesEnabled) before LinkedIn approved Community
 * Management. A redirect address missing from the app may be reported on
 * the page, or only after sign-in; the pass says which address must be there.
 */
async function linkedInConsent(name, auth, callback) {
  const scopes = (auth.searchParams.get("scope") ?? "").split(/[\s,]+/).filter(Boolean);
  if (!scopes.includes("w_member_social") && !scopes.includes("w_organization_social")) {
    fail(name, "the LinkedIn sign-in asks for no permission to post (w_member_social or w_organization_social)");
    return;
  }
  const pages = scopes.includes("w_organization_social");

  const page = await http(auth.toString(), { accept: "text/html", timeoutMs: 15_000 });
  if (page.status === 0) {
    warn(name, `the connection starts, but LinkedIn's sign-in page did not open (${page.transportError})`);
    return;
  }

  let back = null;
  try { back = page.headers.get("location") ? new URL(page.headers.get("location"), auth) : null; } catch { /* not a redirect */ }
  const returned = back && back.href.startsWith(callback) ? back : null;
  const error = returned?.searchParams.get("error");
  const why = returned?.searchParams.get("error_description");
  const body = typeof page.payload === "string" ? page.payload : "";

  if (error === "unauthorized_scope_error" || /unauthorized_scope_error/.test(body)) {
    fail(name, `LinkedIn refused the permissions asked for${why ? ` (${why})` : ""}. The app needs the ` +
      "\"Sign In with LinkedIn using OpenID Connect\" and \"Share on LinkedIn\" products" +
      (pages ? "; Pages also need Community Management approval — until then set Social__LinkedIn__PagesEnabled=false." : "."));
    return;
  }
  if (error) {
    fail(name, `LinkedIn refused the sign-in before anyone logged in: ${error}${why ? ` (${why})` : ""}`);
    return;
  }
  if (/redirect_uri does not match/i.test(body)) {
    fail(name, `LinkedIn does not allow ${callback} — add it to the app's Auth → Authorized redirect URLs`);
    return;
  }
  if (/invalid_client|application (could not be|was not) found/i.test(body)) {
    fail(name, "LinkedIn does not recognise the app (Social__LinkedIn__ClientId)");
    return;
  }

  pass(name, `sign-in page opens and returns to ${callback} (it must be in the app's Authorized redirect URLs). ` +
    (pages
      ? "Asks for profile and Page posting."
      : "Profile posting only; Pages switch on with Social__LinkedIn__PagesEnabled once LinkedIn approves Community Management."));
}

/**
 * Every dependency, asked from inside each service.
 *
 * Needs SMOKE_READINESS_TOKEN — the same value as the API's Readiness__Token
 * and the ai-service's READINESS_TOKEN. Each service checks its own
 * dependencies with free reads (a database ping, a probe blob, Mailjet's sender
 * list, Stripe's prices, Meta's app token, Google's token endpoint, Anthropic's
 * model list, a one-result Pexels search, ElevenLabs' account…) and reports
 * verdicts and setting names, never values.
 *
 * Also compares the address the API builds links on with the address this run
 * is using for the web app: they must be the same site, or password-reset
 * links, invitations and every social connection point somewhere else.
 */
async function readiness() {
  section("Readiness (from inside each service)");

  const token = process.env.SMOKE_READINESS_TOKEN;
  if (!token) {
    (HOSTED ? warn : skip)(
      "readiness",
      "SMOKE_READINESS_TOKEN is not set, so database, storage, payments and provider keys were not " +
        "checked from inside. Set Readiness__Token (API) and READINESS_TOKEN (ai-service) to one " +
        "long random value, and pass it here.",
    );
    return;
  }

  const record = (service, c) => {
    const name = `${service}: ${c.name.replace(/_/g, " ")}`;
    const status = String(c.status).toUpperCase();
    if (status === "OK") pass(name, c.detail);
    else if (status === "WARN") warn(name, c.detail);
    else if (status === "SKIP") skip(name, c.detail);
    else fail(name, c.detail);
  };

  const api = await http(`${API}/api/health/ready`, { headers: { "X-Readiness-Token": token }, timeoutMs: 60_000 });
  if (api.status === 404) {
    fail("api readiness", "404 — Readiness__Token is not set on the API, differs from SMOKE_READINESS_TOKEN, or the API predates the endpoint");
  } else if (!api.ok || !api.payload?.checks) {
    fail("api readiness", errorOf(api));
  } else {
    for (const c of api.payload.checks) record("api", c);

    // The address everything is built on, against the address in use.
    let built = "", using = "";
    try { built = new URL(api.payload.webBaseUrl).host; } catch { /* reported below */ }
    try { using = new URL(WEB).host; } catch { /* ignore */ }
    if (HOSTED && built && using && built !== using) {
      fail(
        "api: links point at this site",
        `the API builds links and social callbacks on ${built}, but the site under test is ${using}. ` +
          "Password-reset and invitation links go to the other address, and social connections are " +
          "refused. Set Public__WebBaseUrl to the address people use.",
      );
    } else if (built) {
      pass("api: links point at this site", built);
    }
  }

  const ai = await http(`${AI}/health/ready`, { headers: { "X-Readiness-Token": token }, timeoutMs: 60_000 });
  if (ai.status === 404) {
    fail("ai-service readiness", "404 — READINESS_TOKEN is not set on the ai-service, differs, or the image predates the endpoint");
  } else if (!ai.ok || !ai.payload?.checks) {
    fail("ai-service readiness", errorOf(ai));
  } else {
    for (const c of ai.payload.checks) record("ai-service", c);
  }
}

async function planAndUsage() {
  section("Plan");
  const r = await http(`${API}/api/subscription/current`, { auth: true });
  if (!r.ok) { fail("subscription", errorOf(r)); return null; }

  const plan = r.payload?.plan ?? {};
  const usage = r.payload?.usage ?? {};

  // Plans.Unlimited is -1 on the wire. Printing "0/-1" made a working Pro plan
  // look like a corrupted one — a report nobody can read at a glance is a
  // report people stop reading.
  const cap = (n) => (n === -1 ? "unlimited" : n);
  pass(
    "subscription",
    `${plan.displayName} — posters ${usage.posters}/${cap(plan.maxPosters)}, ` +
      `videos ${usage.videosThisMonth}/${cap(plan.maxVideosPerMonth)}, ` +
      `AI designs ${usage.aiGenerationsThisMonth}/${cap(plan.maxAiGenerationsPerMonth)}`,
  );
  return { plan, usage };
}

async function posters() {
  section("Posters");

  // Build from a real template, not from nothing.
  //
  // The first version created an empty poster, and its PDF came back at 1 KB —
  // a valid, correctly-signed, entirely blank page. It passed the magic-byte
  // check, which is exactly the hole: a renderer that had stopped drawing
  // anything would have passed too. A poster carrying a template's design
  // exercises fonts, images and layout, and gives the size floor below
  // something to mean.
  const list = await http(`${API}/api/templates?take=1`, { auth: true });
  const items = Array.isArray(list.payload?.items) ? list.payload.items : [];
  const templateId = items[0]?.id ?? null;
  if (!templateId) {
    // Not a failure — a deployment can legitimately have none — but the export
    // checks below are much weaker, so say so rather than letting the PASS
    // imply more than it proves.
    warn("template available", "no templates published, so the exports below render a blank poster");
  }

  const create = await http(`${API}/api/posters`, {
    method: "POST",
    auth: true,
    body: {
      name: `Smoke ${new Date().toISOString()}`,
      ...(templateId ? { fromTemplateId: templateId } : {}),
    },
  });
  if (!create.ok || !create.payload?.id) {
    fail("create poster", errorOf(create));
    return null;
  }
  const id = create.payload.id;
  created.posterIds.push(id);
  pass("create poster", id);

  // A new poster is a **draft**, and drafts may not be sent to patients —
  // `PosterUsage.WhyCannotSend` refuses a campaign carrying one. That rule is
  // the product working correctly, so the smoke has to walk the same path a
  // pharmacist does: publish, then send. The first version skipped this and the
  // campaign step failed with "is still a draft", which reads like a broken
  // deployment and is nothing of the kind. Publishing is also a door in its own
  // right — the status transition is what every send gate reads.
  const publish = await http(`${API}/api/posters/${id}/status`, {
    method: "PATCH",
    auth: true,
    body: { status: "Published" },
  });
  if (publish.ok && String(publish.payload?.status) === "Published") {
    pass("publish poster", "Draft → Published");
  } else {
    fail("publish poster", publish.ok ? `status is ${publish.payload?.status}` : errorOf(publish));
  }

  // The exports are where a broken renderer, a missing font or an unresolvable
  // brand asset actually shows up. Magic bytes, not status codes: a 200 with an
  // HTML error page in it has happened before.
  //
  // Size is checked as well as signature, but only when the poster came from a
  // template — with a known design on it, a file this small means the renderer
  // produced a blank page, and a blank page is a real bug that a magic-byte
  // check waves straight through. A WARN rather than a FAIL: these floors are
  // judgement, and a judgement call should not block a release on its own.
  const MIN_PDF_BYTES = 4_000;
  const MIN_PNG_BYTES = 10_000;

  const size = (r) => `${(r.bytes.length / 1024).toFixed(0)} KB`;
  const checkSize = (name, r, floor) => {
    if (!templateId || r.bytes.length >= floor) {
      pass(name, size(r));
      return;
    }
    warn(
      name,
      `only ${size(r)} for a poster built from a template — that is about the size of ` +
        `an empty page. Open it: the renderer may be drawing nothing.`,
    );
  };

  const pdf = await http(`${API}/api/posters/${id}/export.pdf`, { auth: true, accept: "application/pdf" });
  if (pdf.ok && pdf.bytes?.subarray(0, 4).toString("latin1") === "%PDF") {
    checkSize("export.pdf", pdf, MIN_PDF_BYTES);
  } else {
    fail("export.pdf", pdf.ok ? `not a PDF (${pdf.type})` : errorOf(pdf));
  }

  const png = await http(`${API}/api/posters/${id}/export.png`, { auth: true, accept: "image/png" });
  const isPng =
    png.bytes && png.bytes.length > 8 &&
    png.bytes.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
  if (png.ok && isPng) checkSize("export.png", png, MIN_PNG_BYTES);
  else fail("export.png", png.ok ? `not a PNG (${png.type})` : errorOf(png));

  await posterCard(id, templateId);

  return id;
}

/**
 * The My Posters card: what a pharmacist actually sees for every poster.
 *
 * From an incident: published posters showed a blank card with the browser's
 * "?" while the PDF was fine, so the export checks above passed straight over
 * it. The card is now drawn by the same renderer and served same-origin with
 * an ETag. Checked for a real JPEG of the card width, and for the revalidation
 * that keeps a page of fifty cards from re-downloading all fifty.
 */
async function posterCard(id, templateId) {
  const MIN_CARD_BYTES = 3_000;
  const card = await http(`${API}/api/posters/${id}/card`, { auth: true, accept: "image/jpeg" });
  const isJpeg = card.bytes?.length > 3 && card.bytes[0] === 0xff && card.bytes[1] === 0xd8 && card.bytes[2] === 0xff;
  if (!card.ok || !isJpeg) {
    fail("poster card", card.ok ? `not a JPEG (${card.type}, ${card.bytes?.length ?? 0} bytes)` : errorOf(card));
    return;
  }
  if (templateId && card.bytes.length < MIN_CARD_BYTES) {
    warn("poster card", `only ${card.bytes.length} bytes for a poster built from a template — open it, it may be blank`);
  } else {
    pass("poster card", `${(card.bytes.length / 1024).toFixed(0)} KB JPEG`);
  }

  const etag = card.headers.get("etag");
  if (!etag) { warn("poster card revalidates", "no ETag, so every My Posters visit re-downloads every card"); return; }
  const again = await http(`${API}/api/posters/${id}/card`, {
    auth: true, accept: "image/jpeg", headers: { "If-None-Match": etag },
  });
  if (again.status === 304) pass("poster card revalidates", "304 for an unchanged card");
  else warn("poster card revalidates", `HTTP ${again.status} for an unchanged card, expected 304`);
}

async function campaign(posterId) {
  section("Campaign email");
  if (!posterId) { skip("send campaign", "no poster to send"); return; }

  const create = await http(`${API}/api/campaigns`, {
    method: "POST",
    auth: true,
    body: {
      posterId,
      name: `Smoke ${new Date().toISOString()}`,
      channel: "Email",
      recipients: [EMAIL_TO],
    },
  });
  if (!create.ok || !create.payload?.id) {
    fail("create campaign", errorOf(create));
    return;
  }
  const id = create.payload.id;
  pass("create campaign", `one recipient: ${EMAIL_TO}`);

  // Dispatch is queued and handled by a worker, so the interesting answer
  // arrives later. There is no GET by id, so the list is filtered.
  working(`waiting for the worker to dispatch it (up to ${CAMPAIGN_TIMEOUT_MS / 1000}s)`);
  const deadline = Date.now() + CAMPAIGN_TIMEOUT_MS;
  let last = null;
  while (Date.now() < deadline) {
    await sleep(3000);
    const list = await http(`${API}/api/campaigns`, { auth: true });
    const mine = Array.isArray(list.payload) ? list.payload.find((c) => c.id === id) : null;
    if (!mine) continue;
    last = mine;
    if (["Sent", "Failed", "Stopped"].includes(String(mine.status))) break;
  }

  if (!last) { fail("campaign dispatch", "the campaign never appeared in the list"); return; }
  if (String(last.status) === "Sent") {
    pass("campaign dispatch", `Sent — check ${EMAIL_TO}`);
  } else if (String(last.status) === "Failed") {
    const note = last.note ?? last.error ?? "";
    if (MISCONFIGURED.test(note)) {
      // Not a provider having a bad minute — this deployment cannot send any
      // email at all, and it reads as a human sentence, which is why it was
      // only a warning before.
      fail("campaign dispatch", `this deployment cannot send email: ${note}`);
      return;
    }
    const [status, detail] = classifyFailure("Campaign", note);
    record(status, "campaign dispatch", detail);
  } else {
    fail("campaign dispatch", `stuck on ${last.status} after ${CAMPAIGN_TIMEOUT_MS / 1000}s`);
  }
}

async function video() {
  section("Video");

  // Which voices this deployment offers — and therefore whether narration is
  // configured at all.
  //
  // Worth a call of its own because it fails *early* and cheaply. Without an
  // ElevenLabs key a hosted deployment renders happily to the voice stage and
  // dies there, which costs a research call, a script call and three minutes
  // before anyone finds out. This says it in 200ms. It is also the door that
  // went wrong in the opposite direction: the browser offering Edge voices that
  // the render would silently swap.
  // Set when narration is configured in a way that *cannot* render, which is
  // the one case where starting a video is certainly wasted money.
  let narrationIsFatal = false;

  const narration = await http(`${API}/api/videos/narration`, { auth: true, timeoutMs: 20_000 });
  if (narration.status === 204) {
    fail(
      "narration options",
      "the API could not reach ai-service for the voice list. Check AiService__BaseUrl. " +
        "The video dialog will offer no voice choice.",
    );
  } else if (!narration.ok || !Array.isArray(narration.payload?.voices)) {
    fail("narration options", errorOf(narration));
  } else {
    const { provider, voices, defaultVoiceId, estimateUsdPerVideo } = narration.payload;
    const detail =
      `${provider} — ${voices.length} voice(s), default ${defaultVoiceId}, ` +
      `~$${Number(estimateUsdPerVideo ?? 0).toFixed(3)}/video`;

    if (voices.length === 0) {
      fail("narration options", "no voices offered — the dialog would open empty");
    } else if (provider === "edge" && HOSTED) {
      // The production failure, caught before it costs a render. Edge-TTS is
      // not served to datacentre networks, so a hosted deployment reporting
      // "edge" will fail every video at the voice stage.
      narrationIsFatal = true;
      fail(
        "narration options",
        `${detail}. This is a hosted deployment with no ELEVENLABS_API_KEY set — ` +
          "Edge-TTS is refused from datacentre networks, so every render will fail " +
          "at the voice stage.",
      );
    } else {
      pass("narration options", detail);
    }
  }

  // Don't spend a render we already know will fail.
  //
  // Only for the certain case. A 204 means the API could not read the voice
  // list over HTTP, which says nothing about whether a render would work —
  // rendering is dispatched through Mongo, not over that link — so that one
  // still goes ahead and finds out.
  if (narrationIsFatal) {
    skip(
      "create video",
      "narration cannot work on this deployment (see above) — not spending a render " +
        "to watch it fail at the voice stage. Set ELEVENLABS_API_KEY and run again.",
    );
    return;
  }

  const attached = await attachOwnPicture();


  const create = await http(`${API}/api/videos`, {
    method: "POST",
    auth: true,
    body: {
      topic: "flu jab walk-in clinic this week",
      name: `Smoke ${new Date().toISOString()}`,
      scriptSource: "Ai",
      visualStyle: "Realistic",
      // Only when the upload worked. Sending a key that does not exist would
      // test the skip path instead of the feature, and would look identical in
      // the report.
      ...(attached.good
        ? {
            images: [
              // <b>The damaged one goes first, and that is not cosmetic.</b>
              // The opening beat is the only one guaranteed to be pinned — a
              // short script may have room for just one picture — so the file
              // whose treatment this test reads has to be in that position. Put
              // the good one first and a one-beat script would never touch the
              // damaged file, say nothing about it, and be reported as a render
              // that never looked at attachments at all.
              ...(attached.broken ? [{ blobKey: attached.broken, source: "upload" }] : []),
              { blobKey: attached.good, source: "upload" },
            ],
          }
        : {}),
    },
  });
  if (!create.ok || !create.payload?.id) {
    // A plan with no video allowance is a legitimate answer, not a failure.
    if (create.status === 403) { skip("create video", errorOf(create)); return; }
    fail("create video", errorOf(create));
    return;
  }
  const id = create.payload.id;
  created.videoIds.push(id);
  pass("create video", id);

  // The render worker lives in ai-service. With it down, the video sits
  // Pending until the deadline and the report blames the render; say what is
  // actually wrong and save the five minutes.
  if (aiReachable === false) {
    fail("video render", "not waited for: ai-service is not reachable (see Reachability), and it runs the render worker — the video would stay Pending");
    return;
  }

  working(`rendering — polling until Ready or Failed, up to ${VIDEO_TIMEOUT_MS / 1000}s`);
  // When the render actually started, so the report can say how long it took.
  // "Ready in under 300s" was true of a 40-second render and a 290-second one,
  // which is no use at all when the question is "why do videos take so long".
  const renderStartedAt = Date.now();
  const deadline = Date.now() + VIDEO_TIMEOUT_MS;
  let last = create.payload;
  let lastNote = "";
  // The last thing it said while it was still working.
  //
  // When a render fails, the note is replaced by a sentence written for a
  // pharmacist — deliberately, since that is what reaches the customer. But the
  // operator reading this report is then told only that something went wrong,
  // and the one fact that would tell them *where* has just been overwritten.
  // The progress notes name the stage ("Voicing… 45%"), so keep the last one:
  // "failed after Voicing" turns a shrug into a place to look.
  let lastStage = null;
  // When the render last said anything new. "Stuck" means *this* stopped
  // moving — not that the clock ran out. See the classification below.
  let lastProgressAt = Date.now();
  while (Date.now() < deadline) {
    await sleep(5000);
    const r = await http(`${API}/api/videos/${id}`, { auth: true });
    if (!r.ok) continue;
    last = r.payload;
    if (last.note && last.note !== lastNote) {
      lastNote = last.note;
      lastProgressAt = Date.now();
      if (String(last.status) === "Rendering") lastStage = last.note;
      process.stdout.write(`        ${C.d}${last.status}: ${last.note}${C.n}\n`);
    }
    if (["Ready", "Failed"].includes(String(last.status))) break;
  }

  const status = String(last.status);
  if (status === "Ready") {
    const renderSeconds = (Date.now() - renderStartedAt) / 1000;
    pass(
      "video render",
      `Ready in ${renderSeconds.toFixed(0)}s` +
        `${lastStage ? ` (last stage: ${lastStage})` : ""}`,
    );

    // <b>A finished video is not the same as a video that used the picture.</b>
    //
    // A picture that cannot be downloaded or decoded gives up its beat to stock
    // footage and the render carries on — deliberately, because one bad JPEG
    // must not cost a pharmacist the video and the allowance it spent. That
    // tolerance is exactly what makes this invisible: the render is Ready, the
    // MP4 plays, and the only trace is a sentence in `diagnostic`.
    //
    // So the picture uploaded above was built to decode — verified against the
    // same ffmpeg filter the pipeline uses — and anything in `diagnostic` about
    // images therefore means the feature is broken, not that the file was bad.
    if (attached.good) {
      const note = String(last.diagnostic || "");
      // Numbered by position in the request, and the damaged file is first.
      const namesBroken = /image 1\b/.test(note);
      const namesGood = /image 2\b/.test(note);

      if (namesGood) {
        fail(
          "attached picture used",
          `the render dropped the good picture: ${note}. The video is fine; the ` +
            `pharmacist's photograph is simply not in it, and nothing on screen ` +
            `says so.`,
        );
      } else if (attached.broken && !namesBroken) {
        // The case that matters most, and the one silence hides. A worker
        // built before attachments existed ignores them and says nothing —
        // identical, from out here, to one that used them perfectly. The
        // damaged file is the difference: any render that is actually looking
        // at attached pictures must have something to say about that one.
        fail(
          "attached picture used",
          "the render said nothing about a file that cannot be decoded, so it " +
            "never looked at the attached pictures at all. The usual cause is " +
            "an ai-service image built before this feature — check the build " +
            "marker in the Reachability section above.",
        );
      } else {
        pass(
          "attached picture used",
          attached.broken
            ? "the good one was used; the damaged one was skipped, as it should be"
            : "no images were skipped",
        );
      }
    }

    // What the API says this video was ordered as, rather than what this script
    // assumes. The smoke sends no duration, so the server's default decides —
    // and a default that moves would otherwise leave this comparing against a
    // number that was true when it was written.
    const ordered = Number(last.durationSeconds || create.payload?.durationSeconds)
      || ORDERED_VIDEO_SECONDS;

    const dl = await http(`${API}/api/videos/${id}/download`, { auth: true, accept: "video/mp4" });
    if (dl.ok && dl.bytes && dl.bytes.length > 10_000) {
      const mb = `${(dl.bytes.length / 1024 / 1024).toFixed(1)} MB`;
      const seconds = mp4DurationSeconds(dl.bytes);
      readyVideo = { id, seconds };

      if (seconds === null) {
        // Not a failure: the file downloaded and is plausibly a video. But say
        // so, because the length is the one property nobody could see before.
        pass("video download", `${mb} (could not read duration from the MP4)`);
      } else {
        const detail = `${mb}, ${seconds.toFixed(1)}s`;

        // The clip is exactly as long as its narration, and the narration is
        // written by a model against a word budget. That is a soft constraint,
        // so this is the check that tells you whether it held. 27 seconds for a
        // 15-second product was invisible for weeks — the only symptom anybody
        // noticed was "videos take a while".
        if (seconds > ordered * 1.5) {
          // <b>Says what it measured, not why.</b> This used to state that the
          // script had overrun its word budget. It cannot know that: a clip is
          // this long either because the writer wrote too many words or because
          // the words-per-minute the budget assumes is wrong for the voice, and
          // a duration cannot tell those apart. Naming the wrong one sends
          // whoever reads it to rewrite a prompt that was behaving. The
          // ai-service log prints the word count and the observed rate at this
          // point, which does settle it.
          warn(
            "video length",
            `${seconds.toFixed(1)}s for a ${ordered}s clip ` +
              `(${Math.round((seconds / ordered - 1) * 100)}% over). ` +
              `Everything downstream scales with this: b-roll, compositing, ` +
              `encoding, storage — and the render time above. ` +
              `For the cause, grep the ai-service log for "Script overran": it ` +
              `prints the word count against the budget and the observed wpm.`,
          );
          pass("video download", detail);
        } else {
          // Not "within the Ns the product sells": the warning threshold is
          // 1.5x, so everything from 1.0x to 1.5x lands here — and a 17.7s clip
          // for a 15s product was being reported as within 15 seconds, which is
          // plainly untrue to anyone reading the two numbers on the same line.
          pass(
            "video download",
            seconds > ordered
              ? `${detail} — ordered ${ordered}s, so ${Math.round((seconds / ordered - 1) * 100)}% over, under the warning threshold`
              : `${detail} — within the ${ordered}s the product sells`,
          );
        }
      }
    } else {
      fail("video download", dl.ok ? `suspiciously small (${dl.bytes?.length ?? 0} bytes)` : errorOf(dl));
    }
  } else if (status === "Failed") {
    // Classified on `note` — the customer's sentence — and never on
    // `diagnostic`. The diagnostic is *always* technical by design; judging it
    // with the traceback rule would turn every provider outage into a FAIL and
    // make the one signal that matters meaningless.
    const [s, detail] = classifyFailure("Video", last.note ?? "");
    const reason = last.diagnostic
      ? `Reason: ${last.diagnostic}`
      : `No diagnostic stored — check the ai-service log for this id. ` +
        `(An older worker build does not write one.)`;
    record(
      s,
      "video render",
      `${detail}\n        Last stage reached: ${lastStage ?? "none reported"}. Video id ${id}.` +
        `\n        ${reason}`,
    );
  } else {
    // Slow and stuck are different things, and calling one the other is a lie
    // in whichever direction it goes.
    //
    // This reported FAIL — "a render that cannot finish *or* fail" — for a
    // render that finished perfectly 28 seconds after the clock ran out. The
    // wording was about the usageAttempts freeze, where nothing moved at all;
    // applying it to "took longer than my timeout" turns a performance
    // observation into a release blocker and, worse, teaches everyone that the
    // stuck message doesn't mean anything.
    //
    // The distinction is whether *progress* is still arriving. The worker
    // writes a new note at every stage, so a note that changed recently means a
    // live render, and one that hasn't changed in a while means a dead one.
    const stalledFor = Date.now() - lastProgressAt;
    const stallLimit = Math.min(60_000, Math.max(3_000, VIDEO_TIMEOUT_MS / 3));
    const elapsed = Math.round(VIDEO_TIMEOUT_MS / 1000);

    if (stalledFor < stallLimit) {
      // Leave it alone — it is going to finish.
      //
      // Cleanup would otherwise delete a row the worker is still writing to,
      // and the render would then upload its MP4 against a video that no longer
      // exists: an orphaned blob, paid for monthly, with nothing pointing at it
      // and nothing to clean it up. The WARN path is new, and it created that
      // leak the moment it stopped being a FAIL — FAILs skip cleanup entirely,
      // which is what hid it.
      created.videoIds = created.videoIds.filter((v) => v !== id);
      warn(
        "video render",
        `still ${status} after ${elapsed}s, but it is still moving — last update ` +
          `${Math.round(stalledFor / 1000)}s ago at "${lastStage ?? "?"}". Slow, not stuck. ` +
          `Left in place to finish (video ${id}); delete it yourself if you don't want it. ` +
          `Raise SMOKE_VIDEO_TIMEOUT_MS if this deployment is reliably slower than ${elapsed}s.`,
      );
    } else {
      fail(
        "video render",
        `still ${status} after ${elapsed}s with no progress for ${Math.round(stalledFor / 1000)}s ` +
          `(last: "${lastStage ?? "nothing reported"}"). A render that cannot finish *or* fail ` +
          `is the worst state — the customer watches a progress bar for ever. ` +
          `Check the ai-service log for video ${id}.`,
      );
    }
  }
}

/* ── video refine ────────────────────────────────────────────────────── */

/** Poll a video until it is no longer Pending/Rendering. */
async function settle(id, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    await sleep(4000);
    const r = await http(`${API}/api/videos/${id}`, { auth: true });
    if (!r.ok) continue;
    last = r.payload;
    if (!["Pending", "Rendering"].includes(String(last.status))) return last;
  }
  return last;
}

const videosUsed = async () => {
  const r = await http(`${API}/api/subscription/current`, { auth: true });
  return r.ok ? Number(r.payload?.usage?.videosThisMonth ?? NaN) : NaN;
};

/**
 * Changing a shot of a finished video, the way a pharmacist does it: pick the
 * tile, say what goes there.
 *
 * Every check here is a failure that reached a pharmacist before this existed:
 * a change refused on the strength of a stock title ("no moment shows
 * medicine"), a change that reloaded the same video in silence, a shot list
 * that did not match the shots on screen, a typed request answered by
 * guessing. Each is a FAIL, because each is the feature not working — unlike a
 * slow render or a provider's bad minute, none of them is somebody else's fault.
 */
/**
 * A real upload of the smoke video to YouTube, when asked for.
 *
 * Off by default: it puts a video on a real channel and spends YouTube quota.
 * Runs only when the smoke account has a YouTube channel connected and
 * SMOKE_YOUTUBE_PUBLISH=1. Set Social__Google__YouTubePrivacy=unlisted (or
 * private) on the API first. Goes through the same queue and publish worker as
 * a pharmacy's post, so it also proves the worker is running.
 */
async function youtubePost() {
  section("YouTube");
  const name = "youtube post";

  const conns = await http(`${API}/api/social/connections`, { auth: true });
  if (!conns.ok || !Array.isArray(conns.payload)) {
    skip(name, "could not list the smoke account's connections");
    return;
  }
  const channel = conns.payload.find((c) => c.platform === "YouTubeChannel" && c.status !== "Revoked");
  if (!channel) {
    skip(name, "no YouTube channel connected to the smoke account. Connect one in Social & Ads to test real uploads.");
    return;
  }
  if (channel.status === "NeedsReauth") {
    fail(name, `the smoke account's channel "${channel.displayName}" needs reconnecting`);
    return;
  }
  if (!readyVideo) {
    skip(name, "no finished video to upload; see the Video section above");
    return;
  }
  if (process.env.SMOKE_YOUTUBE_PUBLISH !== "1") {
    skip(name, `"${channel.displayName}" is connected. Set SMOKE_YOUTUBE_PUBLISH=1 to upload the smoke video ` +
      "(set Social__Google__YouTubePrivacy=unlisted on the API first).");
    return;
  }

  const created = await http(`${API}/api/social/posts`, {
    method: "POST",
    auth: true,
    body: {
      sourceKind: "Video",
      sourceId: readyVideo.id,
      caption: "Smoke test upload, safe to delete\nPosted by the Marketing25 smoke test.",
      connectionIds: [channel.id],
      scheduledForUtc: null,
    },
  });
  if (!created.ok || !created.payload?.id) {
    fail(name, `could not queue the upload: ${errorOf(created)}`);
    return;
  }

  working("waiting for the publish worker to upload it (up to 5 min)");
  const deadline = Date.now() + 300_000;
  let post = created.payload;
  while (Date.now() < deadline) {
    if (["Published", "PartiallyPublished", "Failed", "Cancelled"].includes(post.status)) break;
    await new Promise((r) => setTimeout(r, 5_000));
    const r = await http(`${API}/api/social/posts/${created.payload.id}`, { auth: true });
    if (r.ok && r.payload) post = r.payload;
  }

  const target = (post.targets ?? [])[0];
  if (post.status === "Published") {
    pass(name, `uploaded to "${channel.displayName}"` +
      (target?.permalink ? `: ${target.permalink}` : " (YouTube returned no link; find it in YouTube Studio)") +
      ". Delete it in YouTube Studio when done. Until Google audits the project it will be private whatever privacy is set.");
  } else if (post.status === "Cancelled") {
    fail(name, "the upload was cancelled before it went out (someone cancelled it on the Scheduled tab?)");
  } else if (target?.status === "Uncertain") {
    warn(name, `the upload may have gone through but did not confirm: ${target.error}. Check the channel.`);
  } else if (["Failed", "PartiallyPublished"].includes(post.status)) {
    fail(name, `YouTube upload failed: ${target?.error ?? post.status}`);
  } else {
    fail(name, `still ${post.status} after 5 minutes. Is the publish worker running (Social__Worker__Enabled)?`);
  }
}

/**
 * A real LinkedIn post, when asked for.
 *
 * Off by default: LinkedIn has no private or unlisted post, so this puts the
 * smoke poster (or video) in the connected profile's or Page's feed for real
 * — delete it afterwards. Runs only with a LinkedIn account connected to the
 * smoke account and SMOKE_LINKEDIN_PUBLISH=1 (a poster) or =video (the smoke
 * video). Prefers a Page over a profile when both are connected. Same queue
 * and publish worker as a pharmacy's post.
 *
 * Even when not posting, says when the connection's 60-day sign-in is about
 * to run out, because that is how LinkedIn posting stops without anyone
 * changing anything.
 */
async function linkedInPost(posterId) {
  section("LinkedIn");
  const name = "linkedin post";

  const conns = await http(`${API}/api/social/connections`, { auth: true });
  if (!conns.ok || !Array.isArray(conns.payload)) {
    skip(name, "could not list the smoke account's connections");
    return;
  }
  const linked = conns.payload.filter(
    (c) => (c.platform === "LinkedInPage" || c.platform === "LinkedInProfile") && c.status !== "Revoked");
  const account = linked.find((c) => c.platform === "LinkedInPage") ?? linked[0];
  if (!account) {
    skip(name, "no LinkedIn account connected to the smoke account. Connect one in Social & Ads to test real posts.");
    return;
  }
  if (account.status === "NeedsReauth") {
    fail(name, `the smoke account's LinkedIn "${account.displayName}" needs reconnecting: ${account.lastError ?? "sign-in no longer accepted"}`);
    return;
  }

  const expires = parseUtc(account.expiresAtUtc);
  if (Number.isFinite(expires)) {
    const days = Math.floor((expires - Date.now()) / 86_400_000);
    if (days < 0) {
      fail(name, `the LinkedIn sign-in for "${account.displayName}" has expired. Reconnect it in Social & Ads.`);
      return;
    }
    if (days <= 14) warn("linkedin sign-in", `"${account.displayName}" must be reconnected within ${days} day(s) — LinkedIn sign-ins last 60 days`);
    else pass("linkedin sign-in", `"${account.displayName}" is good for another ${days} days`);
  }

  const mode = process.env.SMOKE_LINKEDIN_PUBLISH;
  if (mode !== "1" && mode !== "video") {
    skip(name, `"${account.displayName}" is connected. Set SMOKE_LINKEDIN_PUBLISH=1 to post the smoke poster ` +
      "(or =video for the smoke video). LinkedIn posts are public — delete it afterwards.");
    return;
  }
  const source = mode === "video"
    ? (readyVideo ? { sourceKind: "Video", sourceId: readyVideo.id } : null)
    : (posterId ? { sourceKind: "Poster", sourceId: posterId } : null);
  if (!source) {
    skip(name, mode === "video"
      ? "no finished video to post; see the Video section above"
      : "no smoke poster to post; see the Posters section above");
    return;
  }

  const created = await http(`${API}/api/social/posts`, {
    method: "POST",
    auth: true,
    body: {
      ...source,
      caption: "Smoke test post (safe to delete) #Marketing25",
      connectionIds: [account.id],
      scheduledForUtc: null,
    },
  });
  if (!created.ok || !created.payload?.id) {
    fail(name, `could not queue the post: ${errorOf(created)}`);
    return;
  }

  working("waiting for the publish worker to post it (up to 5 min)");
  const deadline = Date.now() + 300_000;
  let post = created.payload;
  while (Date.now() < deadline) {
    if (["Published", "PartiallyPublished", "Failed", "Cancelled"].includes(post.status)) break;
    await sleep(5_000);
    const r = await http(`${API}/api/social/posts/${created.payload.id}`, { auth: true });
    if (r.ok && r.payload) post = r.payload;
  }

  const target = (post.targets ?? [])[0];
  if (post.status === "Published") {
    pass(name, `posted to "${account.displayName}"` +
      (target?.permalink ? `: ${target.permalink}` : " (LinkedIn returned no link)") +
      ". It is public: delete it on LinkedIn when done.");
  } else if (post.status === "Cancelled") {
    fail(name, "the post was cancelled before it went out (someone cancelled it on the Scheduled tab?)");
  } else if (target?.status === "Uncertain") {
    warn(name, `the post may have gone through but did not confirm: ${target.error}. Check the feed.`);
  } else if (["Failed", "PartiallyPublished"].includes(post.status)) {
    fail(name, `LinkedIn post failed: ${target?.error ?? post.status}`);
  } else {
    fail(name, `still ${post.status} after 5 minutes. Is the publish worker running (Social__Worker__Enabled)?`);
  }
}

/**
 * A real X post, when asked for.
 *
 * Off by default: X posts are public and each one uses the developer
 * account's pay-per-use credits. Runs with an X account connected to the
 * smoke account and SMOKE_X_PUBLISH=1 (the smoke poster) or =video (the
 * smoke video). The caption carries the time, because X refuses a post
 * identical to an earlier one. Delete it on X afterwards.
 */
async function xPost(posterId) {
  section("X");
  const name = "x post";

  const conns = await http(`${API}/api/social/connections`, { auth: true });
  if (!conns.ok || !Array.isArray(conns.payload)) {
    skip(name, "could not list the smoke account's connections");
    return;
  }
  const account = conns.payload.find((c) => c.platform === "XAccount" && c.status !== "Revoked");
  if (!account) {
    skip(name, "no X account connected to the smoke account. Connect one in Social & Ads to test real posts.");
    return;
  }
  if (account.status === "NeedsReauth") {
    fail(name, `the smoke account's X "${account.displayName}" needs reconnecting: ${account.lastError ?? "sign-in no longer accepted"}`);
    return;
  }

  const mode = process.env.SMOKE_X_PUBLISH;
  if (mode !== "1" && mode !== "video") {
    skip(name, `"${account.displayName}" is connected. Set SMOKE_X_PUBLISH=1 to post the smoke poster ` +
      "(or =video for the smoke video). X posts are public and use API credits — delete it afterwards.");
    return;
  }
  const source = mode === "video"
    ? (readyVideo ? { sourceKind: "Video", sourceId: readyVideo.id } : null)
    : (posterId ? { sourceKind: "Poster", sourceId: posterId } : null);
  if (!source) {
    skip(name, mode === "video"
      ? "no finished video to post; see the Video section above"
      : "no smoke poster to post; see the Posters section above");
    return;
  }

  const created = await http(`${API}/api/social/posts`, {
    method: "POST",
    auth: true,
    body: {
      ...source,
      caption: `Smoke test post ${new Date().toISOString().slice(0, 16)} (safe to delete) #Marketing25`,
      connectionIds: [account.id],
      scheduledForUtc: null,
    },
  });
  if (!created.ok || !created.payload?.id) {
    fail(name, `could not queue the post: ${errorOf(created)}`);
    return;
  }

  working("waiting for the publish worker to post it (up to 5 min)");
  const deadline = Date.now() + 300_000;
  let post = created.payload;
  while (Date.now() < deadline) {
    if (["Published", "PartiallyPublished", "Failed", "Cancelled"].includes(post.status)) break;
    await sleep(5_000);
    const r = await http(`${API}/api/social/posts/${created.payload.id}`, { auth: true });
    if (r.ok && r.payload) post = r.payload;
  }

  const target = (post.targets ?? [])[0];
  if (post.status === "Published") {
    pass(name, `posted to "${account.displayName}"` +
      (target?.permalink ? `: ${target.permalink}` : "") + ". It is public: delete it on X when done.");
  } else if (post.status === "Cancelled") {
    fail(name, "the post was cancelled before it went out (someone cancelled it on the Scheduled tab?)");
  } else if (target?.status === "Uncertain") {
    warn(name, `the post may have gone through but did not confirm: ${target.error}. Check the account.`);
  } else if (["Failed", "PartiallyPublished"].includes(post.status)) {
    fail(name, `X post failed: ${target?.error ?? post.status}` +
      (/credits/i.test(target?.error ?? "") ? " — top up the X developer account's credits" : ""));
  } else {
    fail(name, `still ${post.status} after 5 minutes. Is the publish worker running (Social__Worker__Enabled)?`);
  }
}

/**
 * A real TikTok post of the smoke video, when asked for.
 *
 * Off by default. Runs with a TikTok account connected to the smoke account
 * and SMOKE_TIKTOK_PUBLISH=1. Reads the account's live creator info through
 * the API, as the share screen does, and posts as "Only me" (SELF_ONLY) —
 * the only audience an unaudited app may use — with comments, Duet and
 * Stitch off. Delete it in the TikTok app afterwards.
 */
async function tikTokPost() {
  section("TikTok");
  const name = "tiktok post";

  const conns = await http(`${API}/api/social/connections`, { auth: true });
  if (!conns.ok || !Array.isArray(conns.payload)) {
    skip(name, "could not list the smoke account's connections");
    return;
  }
  const account = conns.payload.find((c) => c.platform === "TikTokAccount" && c.status !== "Revoked");
  if (!account) {
    skip(name, "no TikTok account connected to the smoke account. Connect one in Social & Ads to test real posts.");
    return;
  }
  if (account.status === "NeedsReauth") {
    fail(name, `the smoke account's TikTok "${account.displayName}" needs reconnecting: ${account.lastError ?? "sign-in no longer accepted"}`);
    return;
  }

  const creator = await http(`${API}/api/social/connections/${account.id}/tiktok-creator`, { auth: true });
  if (!creator.ok || !creator.payload) {
    fail("tiktok creator info", `the share screen could not read the account's TikTok settings: ${errorOf(creator)}`);
    return;
  }
  pass("tiktok creator info", `@${creator.payload.username} offers ${(creator.payload.privacyLevelOptions ?? []).join(", ") || "no audiences"}`);

  if (process.env.SMOKE_TIKTOK_PUBLISH !== "1") {
    skip(name, `"${account.displayName}" is connected. Set SMOKE_TIKTOK_PUBLISH=1 to post the smoke video as "Only me".`);
    return;
  }
  if (!readyVideo) {
    skip(name, "no finished video to post; see the Video section above");
    return;
  }
  if (!(creator.payload.privacyLevelOptions ?? []).includes("SELF_ONLY")) {
    fail(name, 'TikTok does not offer "Only me" for this account, so the smoke will not post to it');
    return;
  }

  const created = await http(`${API}/api/social/posts`, {
    method: "POST",
    auth: true,
    body: {
      sourceKind: "Video",
      sourceId: readyVideo.id,
      caption: "Smoke test post (safe to delete)",
      connectionIds: [account.id],
      scheduledForUtc: null,
      tikTokOptions: {
        privacyLevel: "SELF_ONLY",
        allowComments: false,
        allowDuet: false,
        allowStitch: false,
        promotesOwnBusiness: false,
        isPaidPartnership: false,
      },
    },
  });
  if (!created.ok || !created.payload?.id) {
    fail(name, `could not queue the post: ${errorOf(created)}`);
    return;
  }

  working("waiting for the publish worker and TikTok's processing (up to 6 min)");
  const deadline = Date.now() + 360_000;
  let post = created.payload;
  while (Date.now() < deadline) {
    if (["Published", "PartiallyPublished", "Failed", "Cancelled"].includes(post.status)) break;
    await sleep(5_000);
    const r = await http(`${API}/api/social/posts/${created.payload.id}`, { auth: true });
    if (r.ok && r.payload) post = r.payload;
  }

  const target = (post.targets ?? [])[0];
  if (post.status === "Published") {
    pass(name, `posted to "${account.displayName}" as Only me` +
      (target?.permalink ? `: ${target.permalink}` : " (private posts have no public link; see the TikTok app)") +
      ". Delete it in the TikTok app when done.");
  } else if (post.status === "Cancelled") {
    fail(name, "the post was cancelled before it went out");
  } else if (target?.status === "Uncertain") {
    warn(name, `TikTok may still publish it: ${target.error}. Check the TikTok app.`);
  } else if (["Failed", "PartiallyPublished"].includes(post.status)) {
    fail(name, `TikTok post failed: ${target?.error ?? post.status}`);
  } else {
    fail(name, `still ${post.status} after 6 minutes. Is the publish worker running (Social__Worker__Enabled)?`);
  }
}

async function videoRefine() {
  section("Video refine");
  if (!readyVideo) {
    skip("video refine", "no finished video to change — see the Video section above");
    return;
  }
  const { id, seconds: before } = readyVideo;

  // 1. The picker's tiles.
  const listed = await http(`${API}/api/videos/${id}/shots`, { auth: true });
  if (!listed.ok || !Array.isArray(listed.payload)) {
    fail("shot list", errorOf(listed));
    return;
  }
  const shots = listed.payload;
  if (shots.length === 0) {
    fail("shot list", "no shots on record for a video this build just made. The worker " +
      "writes the shot timeline after every render — an ai-service built before the " +
      "shot picker does not. Check the build marker in Reachability.");
    return;
  }
  const withStill = shots.filter((s) => s.hasThumbnail).length;
  if (withStill === 0) {
    // Not one still on a video this build just rendered is not a flaky
    // upload: the worker never received the shot timeline, so these tiles are
    // the script's *plan* rather than the shots on screen — the exact mismatch
    // the picker exists to end. It happened: `shot_timeline` was missing from
    // the graph's state schema and LangGraph dropped it without a word.
    fail(
      "shot list",
      `${shots.length} tile(s), none with a still — no rendered timeline reached the worker, ` +
        "so the tiles are the plan, not the shots on screen. Check ai-service logs for " +
        '"Thumbnail" lines; if there are none, the timeline never arrived.',
    );
  } else {
    (withStill === shots.length ? pass : warn)(
      "shot list",
      `${shots.length} tile(s), ${withStill} with a still` +
        (withStill === shots.length ? "" : " — the rest show as numbers; the worker could not keep their stills"),
    );
  }

  // A 15-second video is four shots (beats.shot_count). Fewer is the regression
  // reported in October — the writer returned three moments and, with this
  // run's attached picture taking one, two stock shots were left.
  const moments = new Set(shots.map((s) => s.index)).size;
  if (moments < 4) {
    warn("shot count", `${moments} shot(s); a ${ORDERED_VIDEO_SECONDS}s video should have at least 4. ` +
      "The ai-service may predate the shot-count fix — check its build in Reachability.");
  } else {
    pass("shot count", `${moments} shots`);
  }

  // The tile to change: the last one that is stock footage (the attached
  // picture takes the opening moment, and replacing it is a different check).
  const target = [...shots].reverse().find((s) => !s.isPhoto) ?? shots[shots.length - 1];

  // 2. The still loads the way the browser loads it: through the web app.
  if (target.hasThumbnail) {
    const still = await http(`${WEB}/api/videos/${id}/shots/${target.index}/thumbnail`, {
      useCookies: true, accept: "image/jpeg",
    });
    if (still.ok && still.bytes?.length > 500 && still.type.startsWith("image/")) {
      pass("shot still", `shot ${target.index + 1}: ${(still.bytes.length / 1024).toFixed(0)} KB via the web app`);
    } else {
      fail("shot still", `the tile would show a broken image: ${errorOf(still)}`);
    }
  }

  // 3. A shot that is not in the video is refused, not guessed at.
  const bogus = await http(`${API}/api/videos/${id}/shots`, {
    method: "POST", auth: true, body: { shots: [{ index: 999, show: "anything" }] },
  });
  if (bogus.status === 400) pass("shot not in the video refused", errorOf(bogus));
  else fail("shot not in the video refused", `expected 400, got ${bogus.status}: ${errorOf(bogus)}`);

  // 4. A typed request that names no shot must be refused — the model that
  //    guessed shots from descriptions is gone, and this proves it stays gone.
  const typed = "replace the shot with medicine with a salad";
  const preview = await http(`${API}/api/videos/${id}/refine/preview`, {
    method: "POST", auth: true, body: { instruction: typed },
  });
  if (!preview.ok || !preview.payload?.canRun || String(preview.payload?.stage).toLowerCase() !== "visuals") {
    warn("typed shot request", `routed as ${preview.payload?.stage ?? "?"} (canRun=${preview.payload?.canRun}), ` +
      `so the no-guessing rule was not exercised: ${preview.ok ? preview.payload?.reason ?? "" : errorOf(preview)}`);
  } else {
    const confirm = await http(`${API}/api/videos/${id}/refine`, {
      method: "POST", auth: true, body: { instruction: typed },
    });
    if (!confirm.ok) {
      fail("typed shot request", `could not queue it: ${errorOf(confirm)}`);
    } else {
      working("a typed request with no shot position — expecting a refusal, not a guess");
      const after = await settle(id, Math.min(VIDEO_TIMEOUT_MS, 120_000));
      const note = String(after?.note ?? "");
      if (after && String(after.status) === "Ready" && /tap the shot/i.test(note)) {
        pass("typed shot request", "no position named, so it asked to tap the shot instead of guessing");
      } else if (after && String(after.status) === "Ready" && !note) {
        fail("typed shot request", "a request naming no shot was carried out anyway — a shot was " +
          "guessed from its description, the behaviour that refused real requests before");
      } else {
        fail("typed shot request", `ended ${after?.status ?? "unknown"}: ${note || "(no note)"}`);
      }
    }
  }

  // 5. Tap the shot, say what goes there.
  const usedBefore = await videosUsed();
  const show = "a person stepping on a bathroom scale";
  const change = await http(`${API}/api/videos/${id}/shots`, {
    method: "POST", auth: true, body: { shots: [{ index: target.index, show }] },
  });
  if (change.status !== 202) {
    fail("change a shot", `expected 202, got ${change.status}: ${errorOf(change)}`);
    return;
  }
  pass("change a shot", `shot ${target.index + 1} → "${show}"`);

  working(`re-cutting — polling until Ready, up to ${VIDEO_TIMEOUT_MS / 1000}s`);
  const startedAt = Date.now();
  const done = await settle(id, VIDEO_TIMEOUT_MS);
  const status = String(done?.status ?? "unknown");
  if (status !== "Ready") {
    const [s, detail] = status === "Failed"
      ? classifyFailure("Refine", done?.note ?? "")
      : ["FAIL", `still ${status} after ${VIDEO_TIMEOUT_MS / 1000}s`];
    record(s, "refine render", detail);
    return;
  }
  if (done.note) {
    // Ready *with* a reason is the change not being made — the exact failure a
    // pharmacist reported as "it loaded but nothing changed".
    fail("refine render", `Ready, but the change was not made: ${done.note}`);
    return;
  }
  pass("refine render", `Ready in ${((Date.now() - startedAt) / 1000).toFixed(0)}s`);

  // 6. Proof the change is the one asked for, and nothing else.
  const relisted = await http(`${API}/api/videos/${id}/shots`, { auth: true });
  const afterShots = relisted.ok && Array.isArray(relisted.payload) ? relisted.payload : [];
  const byIndex = new Map(afterShots.map((s) => [s.index, s]));
  const changed = byIndex.get(target.index);
  if (!changed) {
    fail("the tapped shot changed", "the tapped shot is missing from the list after the change");
  } else if (target.shows && changed.shows && target.shows === changed.shows) {
    fail("the tapped shot changed", `it still shows "${changed.shows}" — the re-cut kept the old footage`);
  } else if (!target.shows || !changed.shows) {
    warn("the tapped shot changed", "cannot compare: the stock library gave no description for it");
  } else {
    pass("the tapped shot changed", `"${target.shows}" → "${changed.shows}"`);
  }

  const others = shots.filter((s) => s.index !== target.index && !s.isPhoto && s.shows);
  const moved = others.filter((s) => byIndex.get(s.index)?.shows !== s.shows);
  const tappedKept = Boolean(target.shows && changed?.shows && target.shows === changed.shows);
  if (others.length === 0) {
    skip("other shots kept", "no other stock shot to compare");
  } else if (moved.length && tappedKept) {
    // The change landed on the wrong shot: the one tapped is untouched and
    // another is not. That is the feature not working.
    fail("other shots kept", `shot(s) ${moved.map((s) => s.index + 1).join(", ")} changed instead of ` +
      `shot ${target.index + 1} — only the tapped shot may change`);
  } else if (moved.length) {
    // The tapped shot changed *and* another did. By design a refine fetches
    // each kept shot again, and one that will not download is searched afresh
    // rather than failing the change — a CDN hiccup, not a bug, so a warning.
    warn("other shots kept", `shot(s) ${moved.map((s) => s.index + 1).join(", ")} changed as well. ` +
      `Usually a kept clip that would not download again and was re-searched — ` +
      `grep the ai-service log for "recovered" and video ${id}.`);
  } else {
    pass("other shots kept", `${others.length} other shot(s) unchanged`);
  }

  // Same narration, so the same length to within an encoder frame.
  const dl = await http(`${API}/api/videos/${id}/download`, { auth: true, accept: "video/mp4" });
  const now = dl.ok && dl.bytes ? mp4DurationSeconds(dl.bytes) : null;
  if (before == null || now == null) {
    skip("voice kept", "could not read both durations");
  } else if (Math.abs(now - before) <= 0.3) {
    pass("voice kept", `${now.toFixed(1)}s before and after — the narration was reused, not re-recorded`);
  } else {
    fail("voice kept", `${before.toFixed(1)}s before, ${now.toFixed(1)}s after — a shot change must not ` +
      "re-record the voice (it is billed and changes the video)");
  }

  const usedAfter = await videosUsed();
  if (Number.isNaN(usedBefore) || Number.isNaN(usedAfter)) {
    skip("refine is free", "could not read the allowance");
  } else if (usedAfter === usedBefore) {
    pass("refine is free", `allowance unchanged at ${usedAfter}`);
  } else {
    fail("refine is free", `allowance went ${usedBefore} → ${usedAfter}; changing a shot must not use a video`);
  }
}

async function aiPosterDesign(planInfo) {
  section("AI poster design");

  const cap = planInfo?.plan?.maxAiGenerationsPerMonth;
  const used = planInfo?.usage?.aiGenerationsThisMonth ?? 0;
  if (cap === 0) { skip("design with AI", "this plan includes no AI designs"); return; }
  if (cap > 0 && used >= cap) { skip("design with AI", `allowance used (${used}/${cap})`); return; }

  working("asking the model for a design — this one is slow, up to a minute");
  const r = await http(`${API}/api/posters/generate`, {
    method: "POST",
    auth: true,
    body: {
      prompt: "walk-in flu clinic, bold: no appointment needed, open until 7pm on weekdays",
      // The default a pharmacy gets: a stock photo, chosen by the designer.
      photo: { mode: "Stock" },
    },
  });

  // { poster, photoNotes } — the poster, plus anything to say about its photo.
  const poster = r.payload?.poster;
  if (r.ok && poster?.id) {
    created.posterIds.push(poster.id);
    const notes = Array.isArray(r.payload.photoNotes) ? r.payload.photoNotes : [];
    const problems = houseStyleProblems(poster.canvas);
    if (problems.length) {
      fail("design with AI", `"${poster.name}" is not in the house style: ${problems.join("; ")}`);
    } else if (notes.length) {
      // Designed, but the photo is not what was asked for (no stock match,
      // stock unavailable). Said out loud to the pharmacist; worth a look here.
      warn("design with AI", `"${poster.name}" designed, with a photo note: ${notes.join(" ")}`);
    } else {
      pass("design with AI", `"${poster.name}" in the house style${photoOf(poster.canvas)}`);
    }
    return;
  }
  if (r.status === 403) { skip("design with AI", errorOf(r)); return; }

  const message = errorOf(r);
  if (looksTechnical(message)) {
    fail("design with AI", `raw error reached the caller: ${message}`);
  } else {
    warn("design with AI", `refused with a human message: ${message}`);
  }
}

/** The canvas as a document, whichever shape the API sent it in. */
function canvasOf(canvas) {
  if (!canvas) return null;
  if (typeof canvas === "string") {
    try { return JSON.parse(canvas); } catch { return null; }
  }
  return canvas;
}

/**
 * What a generated poster must be, whatever the model wrote: the house style's
 * hard rules, checked on the real response rather than trusted.
 */
function houseStyleProblems(raw) {
  const doc = canvasOf(raw);
  if (!doc || !Array.isArray(doc.shapes)) return ["no canvas in the response"];
  const out = [];
  const shapes = doc.shapes;
  const texts = shapes.filter((s) => s.kind === "text");
  if (doc.width !== 1240 || doc.height !== 1754) out.push(`page ${doc.width}x${doc.height}, want A4 1240x1754`);
  if (!texts.some((t) => t.fontSize >= 60)) out.push("no headline of 60px or more");
  const qr = shapes.filter((s) => s.id === "pharmacy-booking-qr");
  if (qr.length !== 1) out.push(`${qr.length} booking QR slots, want 1`);
  if (texts.some((t) => /\{\{\s*\w+\s*\}\}/.test(t.text))) out.push("a {{token}} was left unfilled");
  if (texts.some((t) => /[\u2713\u2714\u2605]/.test(t.text))) out.push("a tick or star glyph the PDF cannot draw");
  for (const s of shapes) {
    if (s.kind !== "image" || s.id === "pharmacy-booking-qr" || s.id === "pharmacy-logo") continue;
    if (/^https?:/i.test(s.blobKey ?? "")) out.push(`a hot-linked photo: ${s.blobKey}`);
    else if (!String(s.blobKey ?? "").startsWith("/api/assets/templates/")) out.push(`a photo not in our storage: ${s.blobKey}`);
  }
  if (shapes.some((s) => s.kind === "rect" && (s.opacity ?? 1) < 1)) out.push("a translucent rect (fades are baked into photos)");
  return out;
}

function photoOf(raw) {
  const doc = canvasOf(raw);
  const photo = doc?.shapes?.find((s) => s.kind === "image" && String(s.blobKey ?? "").startsWith("/api/assets/templates/"));
  return photo ? `, photo stored at ${photo.blobKey}` : ", no photo";
}

async function displays() {
  section("Display screens");

  const create = await http(`${API}/api/displays/screens`, {
    method: "POST",
    auth: true,
    body: { name: `Smoke ${new Date().toISOString()}`, location: null, addressLine: null },
  });
  if (create.status === 403) { skip("register screen", errorOf(create)); return; }
  if (!create.ok || !create.payload?.id) { fail("register screen", errorOf(create)); return; }

  const id = create.payload.id;
  created.screenIds.push(id);
  pass("register screen", id);

  const code = await http(`${API}/api/displays/screens/${id}/pairing-code`, { method: "POST", auth: true });
  const value = code.payload?.pairingCode ?? code.payload?.code;
  if (code.ok && value) pass("pairing code", `${value}`);
  else { fail("pairing code", errorOf(code)); return; }

  await screenVideo(id, value);
}

/**
 * One screen, one short video — the whole kiosk path, through the web app.
 *
 * Uses the video the Video step finished; with none (the step skipped, or it
 * failed on a provider) this skips rather than rendering another, because a
 * render is the slow, costly part and it is already covered above.
 *
 * Pairs as a device would, reads the content as the current kiosk does
 * (declaring it can play video) and as an old one does (not declaring it), and
 * asks the signed link for the first kilobyte — a 206 is what proves Safari
 * will play it.
 */
async function screenVideo(screenId, pairingCode) {
  if (process.env.SMOKE_DISPLAY_VIDEO === "0") {
    skip("screen video", "SMOKE_DISPLAY_VIDEO=0");
    return;
  }
  if (!readyVideo) {
    skip("screen video", "no finished video from the Video step to put on a screen");
    return;
  }

  const playlist = await http(`${API}/api/displays/playlists`, {
    method: "POST",
    auth: true,
    body: {
      name: `Smoke ${new Date().toISOString()}`,
      items: [{ kind: "Video", refId: readyVideo.id, durationSeconds: 10 }],
    },
  });
  if (playlist.status === 400) {
    // The refine step can leave the video mid re-cut; not ready is the right
    // answer then, and it is the rule working rather than a fault.
    warn("playlist with a video", errorOf(playlist));
    return;
  }
  if (!playlist.ok || !playlist.payload?.id) { fail("playlist with a video", errorOf(playlist)); return; }
  created.playlistIds.push(playlist.payload.id);
  pass("playlist with a video", `${playlist.payload.id}`);

  const assigned = await http(`${API}/api/displays/screens/${screenId}/playlist`, {
    method: "PUT", auth: true, body: { playlistId: playlist.payload.id },
  });
  if (!assigned.ok) { fail("assign playlist", errorOf(assigned)); return; }

  const claim = await http(`${WEB}/api/display/claim`, {
    method: "POST", body: { code: pairingCode },
  });
  const deviceToken = claim.payload?.token;
  if (!claim.ok || !deviceToken) { fail("pair device", errorOf(claim)); return; }
  pass("pair device");

  const content = await http(`${WEB}/api/display/content`, {
    headers: { "X-Display-Token": deviceToken, "X-Display-Capabilities": "video" },
  });
  const slide = content.payload?.slides?.find?.((s) => s.kind === "Video");
  if (!content.ok || !slide?.mediaUrl) {
    fail("kiosk content lists the video",
      content.ok ? "no video slide with a mediaUrl" : errorOf(content));
    return;
  }
  if (!slide.mediaUrl.startsWith("/api/display/video/") || slide.mediaUrl.includes(deviceToken)) {
    fail("kiosk content lists the video", `unexpected media path: ${slide.mediaUrl.slice(0, 80)}`);
    return;
  }
  pass("kiosk content lists the video", `${slide.durationSeconds}s`);

  const legacy = await http(`${WEB}/api/display/content`, {
    headers: { "X-Display-Token": deviceToken },
  });
  if (legacy.ok && !legacy.payload?.slides?.some?.((s) => s.kind === "Video")) {
    pass("old kiosk is sent no video");
  } else {
    fail("old kiosk is sent no video",
      legacy.ok ? "a kiosk that did not declare video was sent one" : errorOf(legacy));
  }

  const media = await http(`${WEB}${slide.mediaUrl}`, { headers: { Range: "bytes=0-1023" } });
  if (media.status === 206 && media.type.includes("video") && media.bytes?.length) {
    pass("signed video link streams a range", `${media.bytes.length} bytes, ${media.headers.get("content-range")}`);

    // Without a strong validator the screen's browser will not cache ranged
    // video, and every loop of every screen downloads every MP4 again. It
    // still plays, so this warns rather than blocks.
    const etag = media.headers.get("etag");
    if (etag && !etag.startsWith("W/")) pass("video link is cacheable", etag);
    else {
      warn("video link is cacheable",
        "no strong ETag on the video — screens will re-download each video on every loop");
    }
  } else if (media.status === 200 && media.type.includes("video")) {
    warn("signed video link streams a range",
      "answered 200 with the whole file instead of 206 — plays in Chrome, not in Safari");
  } else {
    fail("signed video link streams a range", `${media.status} ${media.type} ${errorOf(media)}`);
  }
}

async function cleanup() {
  section("Cleanup");

  if (KEEP) { skip("cleanup", "SMOKE_KEEP=1 — leaving everything in place"); return; }

  // Nothing is deleted after a failure.
  //
  // The first version cleaned up unconditionally, which meant a run that
  // reported "video stuck in Rendering" then deleted the stuck video — throwing
  // away the single record anybody would want to look at. Tidiness is worth
  // less than evidence.
  if (results.some((r) => r.status === "FAIL")) {
    skip(
      "cleanup",
      `something failed — leaving ${created.posterIds.length} poster(s), ` +
        `${created.videoIds.length} video(s), ${created.playlistIds.length} playlist(s) ` +
        `and ${created.screenIds.length} screen(s) ` +
        "in place so they can be inspected. Delete them by hand when you are done.",
    );
    return;
  }

  let removed = 0;
  for (const id of created.posterIds) {
    const r = await http(`${API}/api/posters/${id}`, { method: "DELETE", auth: true });
    if (r.ok || r.status === 204) removed++;
  }
  for (const id of created.videoIds) {
    const r = await http(`${API}/api/videos/${id}`, { method: "DELETE", auth: true });
    if (r.ok || r.status === 204) removed++;
  }
  for (const id of created.playlistIds) {
    const r = await http(`${API}/api/displays/playlists/${id}`, { method: "DELETE", auth: true });
    if (r.ok || r.status === 204) removed++;
  }
  for (const id of created.screenIds) {
    const r = await http(`${API}/api/displays/screens/${id}`, { method: "DELETE", auth: true });
    if (r.ok || r.status === 204) removed++;
  }
  // Best effort: leftovers are clutter, not a broken deployment.
  pass("cleanup", `removed ${removed} item(s)${KEEP ? "" : " created by this run"}`);
}

/* ── report ──────────────────────────────────────────────────────────── */

function report() {
  const counts = { PASS: 0, WARN: 0, FAIL: 0, SKIP: 0 };
  for (const r of results) counts[r.status]++;

  process.stdout.write(`\n${C.b}${"═".repeat(62)}${C.n}\n`);
  process.stdout.write(`${C.b}  Smoke report${C.n}\n`);
  process.stdout.write(`  api  ${API}\n  ai   ${AI}\n  web  ${WEB}\n`);
  // Which machine this ran against is the first thing anybody asks when a
  // result surprises them, and "it read a .env you forgot about" is a real
  // answer. So say where the settings came from, every time.
  process.stdout.write(
    ENV_FILE
      ? `  ${C.d}env  ${ENV_FILE.path} (${ENV_FILE.applied} value(s))${C.n}\n`
      : `  ${C.d}env  process environment only — no .env read${C.n}\n`,
  );
  process.stdout.write(`${C.b}${"═".repeat(62)}${C.n}\n`);

  if (networkRetries.length) {
    process.stdout.write(
      `  ${C.d}network: ${networkRetries.length} request(s) passed only on a second try — the connection from ` +
        `this machine is unreliable, not the deployment: ${networkRetries.join("; ")}${C.n}\n`,
    );
  }
  for (const r of results.filter((x) => x.status === "FAIL")) {
    process.stdout.write(`  ${C.r}FAIL${C.n}  ${r.name}\n        ${r.detail}\n`);
  }
  for (const r of results.filter((x) => x.status === "WARN")) {
    process.stdout.write(`  ${C.y}WARN${C.n}  ${r.name}\n        ${r.detail}\n`);
  }

  process.stdout.write(
    `\n  ${C.g}${counts.PASS} passed${C.n}   ` +
      `${C.y}${counts.WARN} warned${C.n}   ` +
      `${C.r}${counts.FAIL} failed${C.n}   ` +
      `${C.d}${counts.SKIP} skipped${C.n}\n\n`,
  );

  if (counts.FAIL > 0) {
    process.stdout.write(`  ${C.r}${C.b}Not safe to ship.${C.n}\n\n`);
    return 1;
  }
  if (counts.WARN > 0) {
    process.stdout.write(
      `  ${C.y}Shippable, with warnings above worth reading.${C.n}\n` +
        `  ${C.d}A warning is a failure with a human explanation — usually a provider${C.n}\n` +
        `  ${C.d}having a bad minute rather than a bug in this build.${C.n}\n\n`,
    );
    return 0;
  }
  process.stdout.write(`  ${C.g}${C.b}Good to ship.${C.n}\n\n`);
  return 0;
}

/* ── main ────────────────────────────────────────────────────────────── */

/**
 * Runs one step, and turns anything it throws into a FAIL.
 *
 * Without this the first unreachable service ended the run with a Node stack
 * trace and no report — which is precisely the behaviour this script exists to
 * catch in the product. A smoke test that dies on the first problem also hides
 * every problem after it, so the one thing it must never do is stop early.
 */
async function step(name, fn) {
  try {
    return await fn();
  } catch (e) {
    fail(name, `unexpected error: ${e.message}`);
    return null;
  }
}

async function main() {
  process.stdout.write(
    `${C.b}Smoke${C.n} ${C.d}— one call per door, against a running deployment${C.n}\n`,
  );

  await step("reachability", reachability);
  await step("ai-service routes", aiRoutes);

  if (!(await step("sign in", signIn))) {
    process.stdout.write(
      `\n  ${C.r}Cannot continue without a session.${C.n}\n` +
        `  ${C.d}The API seeds this account itself, from the same SMOKE_EMAIL and${C.n}\n` +
        `  ${C.d}SMOKE_PASSWORD read above. If it has never existed, restart the API${C.n}\n` +
        `  ${C.d}so the seeder runs:  docker compose up -d api${C.n}\n` +
        `  ${C.d}If you changed the password here after the account was created, the${C.n}\n` +
        `  ${C.d}seeder will not have updated it — it never touches an existing account.${C.n}\n`,
    );
    process.exitCode = report();
    return;
  }

  await step("readiness", readiness);
  await step("account email", accountEmail);
  const planInfo = await step("plan", planAndUsage);
  await step("social & ads", socialAvailability);
  const posterId = await step("posters", posters);
  await step("campaign", () => campaign(posterId));
  await step("ai poster design", () => aiPosterDesign(planInfo));
  await step("video", video);
  await step("youtube post", youtubePost);
  await step("linkedin post", () => linkedInPost(posterId));
  await step("tiktok post", tikTokPost);
  await step("x post", () => xPost(posterId));
  await step("video refine", videoRefine);
  await step("display screens", displays);
  await step("cleanup", cleanup);

  process.exitCode = report();
}

main().catch((e) => {
  process.stdout.write(`\n${C.r}Smoke aborted:${C.n} ${e.stack || e.message}\n`);
  process.exitCode = 1;
});

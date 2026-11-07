#!/usr/bin/env node
/**
 * Static checks on the Next.js BFF proxies in apps/web/src/app/api.
 *
 * Two rules, both of which have already been broken in this codebase:
 *
 *   1. A catch-all proxy must export every verb the client actually calls
 *      against it. A missing export is a 405 that looks nothing like a routing
 *      mistake — the admin proxy shipped without PUT and every video-model
 *      change failed.
 *
 *      Derived from real call sites rather than demanding all five verbs
 *      everywhere: the invitations API only ever exposes GET and POST, and a
 *      check that nags for a DELETE nobody calls is a check people learn to
 *      ignore.
 *
 *   2. Every API action that returns something other than JSON — a file, or a
 *      Content(...) with a text/html or image content type — must have its own
 *      web route. The
 *      catch-all proxies parse every response as JSON and send `null` when that
 *      fails, so a CSV or PDF routed through one arrives as a page reading
 *      `null` — with a flawless API log, because the API did its job. The
 *      invitations CSV template shipped that way, and the poster PDF export had
 *      no web route at all.
 *
 *   3. A proxy must not invent a value when the API returned none. All five
 *      catch-all proxies did `NextResponse.json(result.data ?? true)`, so a GET
 *      whose resource legitimately has no value answered `true`. That is how
 *      every top-level pharmacy came to be told it was a branch of another:
 *      `GET /api/pharmacies/parent` returned 204, the proxy turned it into
 *      `true`, and `parent !== null` was satisfied by a boolean.
 *
 * The second rule is the interesting one. Nothing about it is visible from
 * either side alone: the API was correct, the page was reasonable, and the
 * layer in between quietly changed the meaning of the response. Typechecking
 * cannot see it because `unknown` casts to anything.
 *
 * Run: node scripts/check-proxies.mjs
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(fileURLToPath(new URL(".", import.meta.url)), "..");
const API_DIR = join(ROOT, "apps/web/src/app/api");

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (entry === "route.ts") out.push(full);
  }
  return out;
}

const problems = [];

/* ---- which verbs the browser actually sends, per /api prefix ------------ *
 * Every call site looks like fetch("/api/<prefix>/…", { method: "PUT" }), with
 * the method omitted meaning GET. Scanning a generous window after the URL
 * catches the multi-line option objects the client is written in.
 * ----------------------------------------------------------------------- */
const SRC = join(ROOT, "apps/web/src");
const usedVerbs = new Map(); // prefix -> Set<verb>

function collectUsage(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      // Skip the proxies themselves: the `method` argument they forward is not
      // a call site, and counting it would make every proxy justify itself.
      if (full.startsWith(API_DIR)) continue;
      collectUsage(full);
      continue;
    }
    if (!/\.tsx?$/.test(entry)) continue;

    const text = readFileSync(full, "utf8");
    for (const m of text.matchAll(/["'`]\/api\/([a-z-]+)\//g)) {
      const window = text.slice(m.index, m.index + 400);
      const verb = /method:\s*["'`](GET|POST|PUT|PATCH|DELETE)["'`]/.exec(window);
      if (!usedVerbs.has(m[1])) usedVerbs.set(m[1], new Set());
      usedVerbs.get(m[1]).add(verb ? verb[1] : "GET");
    }
  }
}
collectUsage(SRC);

for (const file of walk(API_DIR)) {
  const rel = relative(ROOT, file);
  const text = readFileSync(file, "utf8");
  const isCatchAll = file.includes("[...");

  // ---- rule 1: export every verb the client sends here --------------------
  if (isCatchAll) {
    const prefix = relative(API_DIR, file).split("/")[0];
    const needed = [...(usedVerbs.get(prefix) ?? [])];
    const missing = needed.filter(
      (m) =>
        !new RegExp(`export\\s+(?:const|async\\s+function)\\s+${m}\\b`).test(text),
    );
    if (missing.length > 0) {
      problems.push(
        `${rel}\n    the client sends ${missing.join(", ")} to /api/${prefix}/… ` +
          "but this proxy does not export them — those calls 405",
      );
    }
  }

  // ---- rule 3: do not manufacture a body ---------------------------------
  // `?? true`, `|| true` and `?? {}` applied to the upstream payload all turn
  // "no value" into a value. Guarding on the method is fine; guarding on the
  // data is not.
  for (const m of text.matchAll(
    /result\.data\s*(\?\?|\|\|)\s*(true|\{\}|\[\])/g,
  )) {
    problems.push(
      `${rel}:${text.slice(0, m.index).split("\n").length}\n` +
        `    \`${m[0]}\` substitutes a value the API did not send. A GET whose ` +
        "resource has no value will read as if it did.",
    );
  }
}

/* ---- rule 2: file downloads need a dedicated, non-JSON route ------------ */
const CONTROLLERS = join(ROOT, "apps/api/src/Acme.WebApi/Controllers");

function controllerFiles(dir, out = []) {
  for (const e of readdirSync(dir)) {
    const full = join(dir, e);
    if (statSync(full).isDirectory()) controllerFiles(full, out);
    else if (e.endsWith(".cs")) out.push(full);
  }
  return out;
}

// Every route.ts path, as a URL, for membership tests.
const webRoutes = new Set(
  walk(API_DIR).map((f) =>
    "/api/" + relative(API_DIR, f).replace(/\/route\.ts$/, ""),
  ),
);

for (const file of controllerFiles(CONTROLLERS)) {
  const text = readFileSync(file, "utf8");

  // `File(...)` is not the only non-JSON return. `Content(x, "text/html")` and
  // friends go through the same JSON-only catch-all and arrive as `null` in
  // exactly the same way — the campaign-email preview was written that way and
  // this rule did not see it, because it only knew about File().
  const RETURNS_NON_JSON = /\bFile\(|Content\([^)]*,\s*"(?:text|image|application\/(?!json))[^"]*"/;
  if (!RETURNS_NON_JSON.test(text)) continue;

  const prefix = /\[Route\("([^"]+)"\)\]/.exec(text)?.[1];
  if (!prefix) continue;

  // Split the controller into one block per action, then ask which blocks
  // contain a File(...) return.
  //
  // An earlier version scanned a fixed 600-character window after each
  // attribute. That found the invitations CSV template, whose File(...) is on
  // the next line, and silently missed the poster PDF export, whose File(...)
  // is 686 characters away past a doc comment — so the check passed while the
  // bug it was written for was still present. Anything positional would have
  // the same problem at a different distance.
  const blocks = text.split(/(?=\[Http(?:Get|Post|Put|Patch|Delete)\()/);

  for (const block of blocks) {
    if (!RETURNS_NON_JSON.test(block)) continue;

    const attr = /^\[Http(?:Get|Post)\((?:"([^"]*)")?\)\]/.exec(block);
    if (!attr) continue;

    // Anonymous endpoints are reached directly, not through the BFF. The BFF
    // exists to attach the caller's httpOnly cookie as a bearer token; an
    // action that takes no credential — a share link carrying its own signed
    // token, say — has nothing to gain from a hop through it. Skipping these
    // is a rule, not an allowlist: mark an action [AllowAnonymous] and it is
    // exempt by construction.
    if (/\[AllowAnonymous\]/.test(block)) continue;

    const segment = attr[1] ?? "";
    // Normalise {id}-style tokens to Next's [id] so the two can be compared.
    const url = `/${prefix}/${segment}`
      .replace(/\/+$/, "")
      .replace(/\/+/g, "/")
      .replace(/\{(\w+)[^}]*\}/g, "[$1]");

    if (!webRoutes.has(url)) {
      problems.push(
        `${relative(ROOT, file)}\n    ${url} returns a file but has no web route — ` +
          "it will 404, or be swallowed by a JSON catch-all and render as `null`",
      );
    }
  }
}

console.log(`${walk(API_DIR).length} BFF route files checked\n`);

if (problems.length === 0) {
  console.log("Every proxy forwards all its verbs and passes responses through unchanged.");
  process.exit(0);
}

for (const p of problems) console.log(`FAIL  ${p}\n`);
console.log(`${problems.length} problem(s).`);
process.exit(1);

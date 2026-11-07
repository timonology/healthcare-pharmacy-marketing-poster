#!/usr/bin/env node
/**
 * Verifies the three layers of a request agree with each other.
 *
 * A request from a page crosses two boundaries before it reaches business
 * logic:
 *
 *     api.ts  ──▶  Next route (BFF)  ──▶  .NET controller action
 *
 * Every layer is independently type-checked and independently tested, and the
 * seams between them are checked by nothing. That is where the bugs have been:
 *
 *   · the Template download went through a JSON-only proxy and rendered `null`
 *   · the poster PDF had no Next route and answered 404
 *   · the admin proxy had no PUT export, so video-model changes 405'd
 *
 * None is visible from inside any one layer. api.ts compiles, the proxy
 * compiles, the controller compiles, and the request still fails.
 *
 * Three checks:
 *
 *   A. Every URL api.ts calls resolves to a Next route that exports that verb.
 *   B. Every upstream path a Next route calls exists on a .NET controller.
 *   C. A call served by a catch-all proxy must exist on .NET at the same path,
 *      because a catch-all forwards the path unchanged.
 *
 * B and C are separate on purpose. A dedicated route file may rewrite the path
 * — `/api/auth/me` on the web maps to `/api/me` on the API — so the client URL
 * says nothing about the API URL. Only the catch-alls are pass-through, and
 * only for those can the client URL be checked against .NET directly.
 *
 * What this does NOT check: whether the response survives the proxy. A file
 * download routed through a JSON catch-all resolves perfectly — right URL,
 * right verb — and still arrives as `null`. That is check-proxies.mjs's job,
 * and the two are deliberately separate: this one is about whether a request
 * arrives, that one is about whether the reply comes back intact.
 *
 * String-based, because the thing being checked *is* a string: one URL written
 * in three places in three syntaxes, with no type spanning them.
 *
 * Run: node scripts/check-contract.mjs
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(fileURLToPath(new URL(".", import.meta.url)), "..");
const WEB_API = join(ROOT, "apps/web/src/app/api");
const CONTROLLERS = join(ROOT, "apps/api/src/Acme.WebApi/Controllers");
const CLIENT = join(ROOT, "apps/web/src/lib/api.ts");

const VERBS = ["GET", "POST", "PUT", "PATCH", "DELETE"];

function walk(dir, match, out = []) {
  for (const e of readdirSync(dir)) {
    const full = join(dir, e);
    if (statSync(full).isDirectory()) walk(full, match, out);
    else if (match(e)) out.push(full);
  }
  return out;
}

/** Text from `open` to its balanced close, so a scan cannot run into the next call. */
function balanced(text, openIndex) {
  let depth = 0;
  for (let i = openIndex; i < text.length; i++) {
    if (text[i] === "(") depth++;
    else if (text[i] === ")") {
      depth--;
      if (depth === 0) return text.slice(openIndex, i + 1);
    }
  }
  return text.slice(openIndex, openIndex + 400);
}

/**
 * Reduce a written URL to a comparable path.
 *
 * `${id}` between slashes is a path parameter. A trailing `${qs(params)}` is a
 * query string and contributes nothing — treating it as a segment invented a
 * route called `/api/templates:param` that of course matched nothing.
 */
function normalise(raw) {
  let url = raw.replace(/\$\{[^}]*\}/g, (m, off) =>
    off > 0 && raw[off - 1] === "/" ? ":param" : " ",
  );
  // An *unclosed* `${` means the capture was truncated.
  //
  // The regex that reads these paths takes everything up to the next quote or
  // backtick, so a nested template — `/api/x${q ? `?${q}` : ""}` — stops at the
  // inner backtick and yields `/api/x${q `. Comparing that against a route
  // reports a mismatch that does not exist, and the message points at a path
  // nobody wrote. Everything past the truncation is unknown, so drop it and
  // compare the part that is real.
  const truncated = url.indexOf("${");
  if (truncated >= 0) url = url.slice(0, truncated);

  url = url.replace(/ /g, "");
  return url.split("?")[0].replace(/\/+$/, "") || "/";
}

/* ── 1. what the browser asks for ─────────────────────────────────────── */

const clientText = readFileSync(CLIENT, "utf8");
const calls = [];

for (const m of clientText.matchAll(/(?:authedFetch|fetch)\s*\(/g)) {
  const call = balanced(clientText, m.index + m[0].length - 1);
  // A template literal may contain quotes inside an interpolation —
  // `/api/admin/users/${id}/${active ? "reactivate" : "deactivate"}` — so
  // backtick strings are read to their closing backtick, not to the first
  // quote of any kind.
  const urlMatch =
    /^\(\s*`((?:\/api)[^`]*)`/.exec(call) ??
    /^\(\s*["']((?:\/api)[^"']*)["']/.exec(call);
  if (!urlMatch) continue;

  const verb = /method:\s*["'`](GET|POST|PUT|PATCH|DELETE)["'`]/.exec(call);
  calls.push({
    url: normalise(urlMatch[1]),
    verb: verb ? verb[1] : "GET",
    line: clientText.slice(0, m.index).split("\n").length,
  });
}

// URLs handed to the browser to navigate to (downloads). These never appear as
// a fetch, but a missing route is just as fatal — that is the poster PDF bug.
for (const m of clientText.matchAll(/Url:\s*(?:\([^)]*\)\s*=>\s*)?[`"']([^`"']+)[`"']/g)) {
  if (!m[1].startsWith("/api/")) continue;
  calls.push({
    url: normalise(m[1]),
    verb: "GET",
    line: clientText.slice(0, m.index).split("\n").length,
    navigation: true,
  });
}

/* ── 2. what Next serves, and what it calls upstream ──────────────────── */

const nextRoutes = walk(WEB_API, (e) => e === "route.ts").map((f) => {
  const url = "/api/" + relative(WEB_API, f).replace(/\/route\.ts$/, "");
  const text = readFileSync(f, "utf8");

  // A catch-all forwards whatever path it was given, so the `/api/x/${segments}`
  // it builds is not a route anyone declared and must not be checked as one.
  const upstream = [];
  const isCatchAllFile = url.includes("[...");
  for (const m of isCatchAllFile ? [] : text.matchAll(
    /(?:apiFetch(?:<[^>]*>)?|proxyDownload)\s*\(\s*[`"']([^`"']+)[`"']/g,
  )) {
    if (m[1].startsWith("/api/")) upstream.push(normalise(m[1]));
  }
  // Routes that build the URL against API_BASE directly.
  for (const m of isCatchAllFile ? [] : text.matchAll(/\$\{API_BASE\}([^`"']+)/g)) {
    upstream.push(normalise(m[1]));
  }

  return {
    url,
    file: relative(ROOT, f),
    verbs: VERBS.filter((v) =>
      new RegExp(`export\\s+(?:const|async\\s+function)\\s+${v}\\b`).test(text),
    ),
    upstream: [...new Set(upstream)],
    catchAll: url.includes("[..."),
  };
});

/* ── 3. what .NET serves ──────────────────────────────────────────────── */

const dotnetActions = [];
for (const file of walk(CONTROLLERS, (e) => e.endsWith(".cs"))) {
  const text = readFileSync(file, "utf8");

  // One file can hold more than one controller — InvitationsController and
  // PublicInvitationsController share one — so split on the class boundary and
  // read each one's own [Route].
  for (const chunk of text.split(/(?=\[ApiController\])/)) {
    const prefix = /\[Route\("([^"]+)"\)\]/.exec(chunk)?.[1];
    if (!prefix) continue;

    // `[HttpGet]` — no parentheses — is the controller-root action and is how
    // seven of these controllers expose their list/create endpoints. An earlier
    // version of this regex required parentheses and therefore believed
    // /api/patients, /api/posters and /api/me did not exist.
    for (const block of chunk.split(/(?=\[Http(?:Get|Post|Put|Patch|Delete)[(\]])/)) {
      const attr = /^\[Http(Get|Post|Put|Patch|Delete)(?:\((?:"([^"]*)")?\))?\]/.exec(block);
      if (!attr) continue;

      dotnetActions.push({
        url: `/${prefix}/${attr[2] ?? ""}`.replace(/\/+/g, "/").replace(/\/$/, ""),
        verb: attr[1].toUpperCase(),
        file: relative(ROOT, file),
        anonymous: /\[AllowAnonymous\]/.test(block),
      });
    }
  }
}

/* ── matching ─────────────────────────────────────────────────────────── */
//
// Next writes a parameter `[id]`, .NET writes `{id}` or `{id:int}`, the client
// writes an interpolation reduced to `:param`. All three mean "one segment".

const isParam = (s) => s.startsWith("[") || s.startsWith("{") || s === ":param";

/**
 * How well a called path fits a declared route. -1 means no fit.
 *
 * A score rather than a boolean, because several routes can legitimately match
 * one call and the answer depends on which fits *best* — the same judgement
 * Next's own matcher makes. Two cases forced this:
 *
 *   · `/api/patients/${id}` fits both `/api/patients/[id]` and
 *     `/api/patients/bulk-add-to-group`. The first is the one that serves it.
 *   · `/api/admin/users/${id}/${active ? "reactivate" : "deactivate"}` is a
 *     variable segment that is really a choice between two literals, so it must
 *     be allowed to fit the literal routes `…/reactivate` and `…/deactivate` —
 *     just less well than a genuine parameter would.
 */
function matchScore(candidate, route) {
  const c = candidate.split("/").filter(Boolean);
  const r = route.split("/").filter(Boolean);

  const catchAllAt = r.findIndex((s) => s.startsWith("[..."));
  if (catchAllAt >= 0) {
    const prefixFits =
      c.length > catchAllAt &&
      r.slice(0, catchAllAt).every((s, i) => isParam(s) || s === c[i]);
    return prefixFits ? 0 : -1;
  }

  if (c.length !== r.length) return -1;

  let score = 0;
  for (let i = 0; i < r.length; i++) {
    const rp = isParam(r[i]);
    const cp = isParam(c[i]);

    if (!rp && !cp) {
      if (r[i] !== c[i]) return -1;
      score += 3;                 // literal meeting the same literal
    } else if (rp && cp) {
      score += 3;                 // parameter meeting a parameter
    } else if (rp && !cp) {
      score += 2;                 // a concrete value filling a parameter
    } else {
      score += 1;                 // a variable that may be this literal
    }
  }
  return score;
}

const matches = (candidate, route) => matchScore(candidate, route) >= 0;

const servesUrl = (url) => nextRoutes.filter((r) => matchScore(url, r.url) >= 0);
const bestRoute = (url, list) =>
  [...list].sort((a, b) => matchScore(url, b.url) - matchScore(url, a.url))[0];

/* ── report ───────────────────────────────────────────────────────────── */

const problems = [];
const add = (p) => problems.push(p);

// A: client → Next
for (const call of calls) {
  const serving = servesUrl(call.url);
  if (serving.length === 0) {
    add(
      `${call.verb} ${call.url}\n    api.ts:${call.line} — no Next route serves this. ` +
        (call.navigation
          ? "The browser navigates here and gets Next's 404 page."
          : "The fetch gets Next's 404 page, never the API."),
    );
    continue;
  }

  const chosen = bestRoute(call.url, serving);
  if (!chosen.verbs.includes(call.verb)) {
    add(
      `${call.verb} ${call.url}\n    api.ts:${call.line} — ${chosen.file} exports ` +
        `${chosen.verbs.join(", ") || "nothing"} but not ${call.verb}. This 405s.`,
    );
  }

  // C: a catch-all forwards the path unchanged, so it must exist on .NET too.
  if (chosen.catchAll) {
    const onApi = dotnetActions.some(
      (a) => a.verb === call.verb && matches(call.url, a.url),
    );
    if (!onApi) {
      const other = dotnetActions.filter((a) => matches(call.url, a.url));
      add(
        `${call.verb} ${call.url}\n    api.ts:${call.line} — forwarded unchanged by ` +
          `${chosen.file}, but the API has no such action. ` +
          (other.length
            ? `That path exists only as ${[...new Set(other.map((a) => a.verb))].join(", ")}.`
            : "The path does not exist on the API at all."),
      );
    }
  }
}

// B: Next → .NET
for (const route of nextRoutes) {
  for (const path of route.upstream) {
    if (!dotnetActions.some((a) => matches(path, a.url))) {
      add(
        `${path}\n    ${route.file} forwards to this, but no .NET action matches it.`,
      );
    }
  }
}

console.log(
  `${calls.length} client calls · ${nextRoutes.length} Next routes · ` +
    `${dotnetActions.length} .NET actions\n`,
);

if (problems.length === 0) {
  console.log("Client, BFF and API agree on every route.");
  process.exit(0);
}

const seen = new Set();
for (const p of problems) {
  if (seen.has(p)) continue;
  seen.add(p);
  console.log(`FAIL  ${p}\n`);
}
console.log(`${seen.size} contract mismatch(es).`);
process.exit(1);

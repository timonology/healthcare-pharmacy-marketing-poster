#!/usr/bin/env node
/**
 * Verifies that every internal link and navigation in the web app points at a
 * route that actually exists.
 *
 * This exists because a dead route fails in the most unhelpful way available:
 * Next renders its 404 entirely on the client, so nothing reaches the server,
 * nothing appears in any log, and the only symptom is a button that goes to a
 * blank page. `router.push("/editor")` sat in two admin screens for months —
 * typechecking cannot catch it, because a route is a string.
 *
 * Checks `href="..."`, `href={"..."}`, `router.push(...)` and
 * `router.replace(...)` with literal paths. Template literals with an
 * interpolation are reduced to their static prefix and matched against dynamic
 * segments, so `/posters/${id}` matches `/posters/[id]`.
 *
 * Run: node scripts/check-routes.mjs
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(fileURLToPath(new URL(".", import.meta.url)), "..");
const APP = join(ROOT, "apps/web/src/app");
const SRC = join(ROOT, "apps/web/src");

/** Every file under a directory, recursively. */
function walk(dir, filter, out = []) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (entry === "node_modules" || entry === ".next") continue;
      walk(full, filter, out);
    } else if (filter(entry)) {
      out.push(full);
    }
  }
  return out;
}

// ---- what routes exist -------------------------------------------------
const pageFiles = walk(APP, (f) => f === "page.tsx");
const routes = pageFiles
  .map((f) => "/" + relative(APP, f).replace(/\/page\.tsx$/, "").replace(/page\.tsx$/, ""))
  .map((r) => (r === "/" ? "/" : r.replace(/\/$/, "")))
  .map((r) => (r === "" ? "/" : r));

// Route groups like (marketing) are organisational, not part of the URL.
const urls = routes.map((r) => r.replace(/\/\([^)]+\)/g, "")).map((r) => r || "/");

/** True when a candidate path matches a route, allowing [param] segments. */
function matches(candidate, route) {
  const c = candidate.split("/").filter(Boolean);
  const r = route.split("/").filter(Boolean);

  if (r.some((s) => s.startsWith("[...")))
    return c.length >= r.length - 1 && r.slice(0, -1).every((s, i) => s === c[i]);

  if (c.length !== r.length) return false;
  return r.every((seg, i) => seg.startsWith("[") || seg === c[i]);
}

// ---- what the code navigates to ---------------------------------------
const sources = walk(SRC, (f) => f.endsWith(".tsx") || f.endsWith(".ts"));

const PATTERNS = [
  /href=\{?["'`](\/[^"'`{}\s]*)/g,
  // `href: "/patients"` — the object-literal form. The whole sidebar and header
  // navigation is built from arrays of these, so without this pattern the two
  // files that decide what the app's menus contain were never scanned at all.
  /href:\s*["'`](\/[^"'`{}\s]*)/g,
  /router\.(?:push|replace)\(\s*["'`](\/[^"'`{}\s]*)/g,
  /router\.(?:push|replace)\(\s*`(\/[^`$]*)\$\{/g,
  /href=\{`(\/[^`$]*)\$\{/g,
  // Leaving a delegated session and similar hard reloads.
  /window\.location\.href\s*=\s*["'`](\/[^"'`{}\s]*)/g,
];

const problems = [];
let checked = 0;

for (const file of sources) {
  const text = readFileSync(file, "utf8");

  for (const pattern of PATTERNS) {
    for (const m of text.matchAll(pattern)) {
      let path = m[1];

      // Strip query and hash — routing ignores both.
      path = path.split("?")[0].split("#")[0];
      if (path.length > 1) path = path.replace(/\/$/, "");

      // API routes are route handlers, not pages, and are covered elsewhere.
      if (path.startsWith("/api/")) continue;
      // Static assets.
      if (/\.(png|jpg|jpeg|svg|ico|webp|pdf|txt|xml|json)$/.test(path)) continue;

      checked++;

      // A path ending in a dynamic prefix (`/posters/` from `/posters/${id}`)
      // matches any route with one more segment.
      const isPrefix = m[0].includes("${");
      const ok = isPrefix
        ? urls.some((r) => matches(path + "x", r) || matches(path.replace(/\/$/, ""), r))
        : urls.some((r) => matches(path, r));

      if (!ok) {
        problems.push({
          file: relative(ROOT, file),
          line: text.slice(0, m.index).split("\n").length,
          path,
        });
      }
    }
  }
}

// ---- the inverse: pages nothing links to ------------------------------
//
// A dead link sends you to a 404. An unlinked page is quieter and worse: the
// feature ships, works, and is invisible, so the report is "I can't find the
// changes" rather than an error anyone can act on. The sub-pharmacies page
// shipped this way — its nav entry was never added, and every automated check
// still passed because the page itself was perfect.
//
// Only static, top-level routes are considered. Dynamic routes are reached
// through an interpolated href the patterns above already reduce to a prefix,
// and nested routes are reached from their parent.
const linked = new Set();
for (const file of sources) {
  const text = readFileSync(file, "utf8");
  for (const pattern of PATTERNS) {
    for (const m of text.matchAll(pattern)) {
      linked.add(m[1].split("?")[0].split("#")[0].replace(/\/(?=$)/, ""));
    }
  }
}

/** Routes that are deliberately not linked from anywhere, and why. */
const UNLINKED_BY_DESIGN = new Map([
  ["/", "the landing page is the root"],
  ["/login", "reached by redirect from the middleware"],
  ["/register", "reached from the login page's own copy"],
  ["/onboarding", "reached by redirect after first sign-in"],
  ["/activate", "reached from a link in an invitation email"],
  ["/admin", "the admin nav is built from ADMIN_NAV_GROUPS"],

  // The recovery pages. /forgot-password is deliberately NOT here — it is
  // linked from the login form, and if that link ever disappears the feature
  // becomes unreachable for anyone who is locked out, which is precisely when
  // they need it. These two are only ever opened from an emailed link.
  ["/reset-password", "opened from a link in a password reset email"],
  ["/verify-email", "opened from a link in a verification email"],
  // Security review R2-06: opened from the link in a campaign email, by a
  // patient who has no account.
  ["/unsubscribe", "opened from the unsubscribe link in a campaign email"],

  // Typed into a television by an installer, once, from a printed setup sheet.
  // Nothing in the app links to it on purpose: a link would only ever be
  // clicked by a pharmacist on a laptop, who would then be looking at a black
  // screen asking for a pairing code they do not need.
  ["/display", "typed into the screen itself by whoever installs it"],

  // A developer scratch page: a bare Konva stage with Add rect / Add circle /
  // Add text and, deliberately, no way to save anything.
  //
  // <b>It must stay unlinked.</b> Two product buttons used to point here — the
  // admin template composer's Refine, and Duplicate on the template list — and
  // both were dead ends: the first looked like a broken editor, and the second
  // could only lose the copy, because publishing a template needs a name, a
  // category and tags and this page has none of them. Both now go through the
  // composer, which can save. If a product route ever points here again, the
  // orphan check will stop complaining and that is the signal to look.
  ["/canvas", "developer scratch stage — no save path; product routes must not link here"],
]);

const orphans = urls
  .filter((u) => u.split("/").filter(Boolean).length === 1)
  .filter((u) => !u.includes("["))
  .filter((u) => !linked.has(u))
  .filter((u) => !UNLINKED_BY_DESIGN.has(u));

// ---- the middleware guard must actually run ---------------------------
//
// Next only invokes middleware for paths in `config.matcher`. A route listed in
// PROTECTED but missing from the matcher looks guarded in review and is not
// guarded at all — the list is read by code that never executes for it.
//
// /pharmacies shipped exactly that way. /social and /admin were missing from
// both, which the browser suite caught by walking in as a signed-out visitor.
const middlewareProblems = [];
{
  const mw = readFileSync(join(ROOT, "apps/web/src/middleware.ts"), "utf8");
  const listOf = (re) => {
    const block = re.exec(mw)?.[1] ?? "";
    return new Set([...block.matchAll(/"(\/[a-z-]+)/g)].map((m) => m[1]));
  };

  const protectedPaths = listOf(/const PROTECTED = \[([\s\S]*?)\];/);
  const matched = listOf(/matcher:\s*\[([\s\S]*?)\]/);

  // The matcher may be one catch-all ("/((?!api/|_next/static|…).*)") — it runs
  // on every page for the CSP nonce. Then a path is covered unless it starts
  // with one of the excluded prefixes.
  const catchAll = /matcher:\s*\[\s*"\/\(\(\?!([^)]*)\)\.\*\)"/.exec(mw);
  const excluded = catchAll ? catchAll[1].split("|").map((x) => "/" + x) : [];
  const covers = (p) =>
    catchAll ? !excluded.some((x) => (p + "/").startsWith(x)) : matched.has(p);

  for (const p of protectedPaths) {
    if (!covers(p)) {
      middlewareProblems.push(
        `${p} is in PROTECTED but not in config.matcher — the middleware never ` +
          "runs for it, so it is not protected at all",
      );
    }
  }

  // Every top-level app page that is not public should be guarded. Catches a
  // new page added without a middleware entry, which is how /social got in.
  const PUBLIC = new Set([
    "/", "/login", "/register", "/templates", "/pricing", "/activate",
    "/onboarding", "/billing",

    // Account recovery. All three must work for somebody who cannot sign in —
    // that is the entire point — so they are public by necessity rather than
    // by oversight. Each is guarded by a single-use token instead of a session.
    "/forgot-password", "/reset-password", "/verify-email",

    // Legal pages. Linked from the signup form, so they must be readable
    // *before* anyone has an account — being asked to agree to terms you can
    // only read once you have agreed to them is not consent.
    "/terms", "/privacy",

    // The kiosk page for an in-pharmacy screen. Public because the device that
    // opens it has no account and never will: a television on a wall cannot
    // sign in, and giving it a session cookie would be a far wider grant than
    // it needs. It carries a long random device token in a header instead, and
    // the page shows nothing at all until that token resolves to one screen.
    "/display",

    // Security review R2-06. Opened by a patient from a campaign email; they
    // have no account. The signed token in the link is the only credential.
    "/unsubscribe",
  ]);
  // ---- and the public ones must stay public ------------------------------
  //
  // The reverse of the check below, and it exists for one route in particular.
  // `/display` is the kiosk; `/displays` is the page a pharmacist manages them
  // from. They differ by one character, the guard is a `startsWith` prefix
  // match, and protecting the kiosk by accident would redirect every screen in
  // every pharmacy to a login page — silently, and only visible to whoever is
  // standing in the shop.
  //
  // Nothing else in the app would notice: the pages compile, the routes exist,
  // and the person who made the change is signed in.
  for (const u of PUBLIC) {
    const guarded = [...protectedPaths].filter((p) => u.startsWith(p));
    if (guarded.length > 0) {
      middlewareProblems.push(
        `${u} is listed as public but is matched by PROTECTED ${guarded.join(", ")} — ` +
        "a signed-out visitor would be redirected to /login",
      );
    }
  }

  for (const u of urls) {
    const seg = u.split("/").filter(Boolean);
    if (seg.length !== 1 || u.includes("[")) continue;
    if (PUBLIC.has(u) || protectedPaths.has(u)) continue;
    middlewareProblems.push(
      `${u} is neither public nor in PROTECTED — a signed-out visitor can open it`,
    );
  }
}

// ---- report ------------------------------------------------------------
console.log(`${urls.length} routes · ${checked} internal links checked\n`);

if (middlewareProblems.length > 0) {
  for (const m of middlewareProblems) console.log(`UNGUARDED  ${m}`);
  console.log("");
}

if (orphans.length > 0) {
  for (const o of orphans) {
    console.log(`ORPHAN  ${o.padEnd(32)} no href or router.push anywhere points here`);
  }
  console.log(
    `\n${orphans.length} unreachable page(s). Add a nav entry, or add the route to ` +
      "UNLINKED_BY_DESIGN in this script with a reason.",
  );
}

if (problems.length === 0) {
  if (orphans.length > 0 || middlewareProblems.length > 0) process.exit(1);
  console.log("Every internal link resolves to a real route, and every page is reachable.");
  process.exit(0);
}

// De-duplicate: the same dead path in one file is one bug, not five.
const seen = new Set();
for (const p of problems) {
  const key = `${p.file}:${p.path}`;
  if (seen.has(key)) continue;
  seen.add(key);
  console.log(`DEAD  ${p.path.padEnd(34)} ${p.file}:${p.line}`);
}

console.log(`\n${seen.size} dead link(s). These render Next's 404 with no server request.`);
process.exit(1);

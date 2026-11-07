#!/usr/bin/env node
/**
 * Controls that advertise an action and do nothing.
 *
 * The login page carried `<Link href="#forgot">Forgot password?</Link>` for
 * months. It rendered, it was styled like every other link, and clicking it
 * did nothing at all — while the feature behind it did not exist. Nobody
 * noticed, because nothing was looking.
 *
 * `check-routes.mjs` could not have caught it: every pattern there requires a
 * leading slash (`href="/..."`), because its job is "do internal links resolve
 * to real routes". A fragment is not a route, so `#forgot` was never even a
 * candidate. The gap is not in that check — it is that no check owned the
 * question "does this control go anywhere at all".
 *
 * This one does. It flags:
 *
 *   · href="#name"  with no id="name" anywhere in the app
 *   · href="#" and href=""      — placeholders that never got replaced
 *   · onClick={() => {}}        — a handler that was stubbed and forgotten
 *
 * It reads three href shapes, including the `{ href: "#terms" }` object-literal
 * form the site footer is built from. The first version only matched the JSX
 * attribute, and so reported the app clean while seven dead links sat in the
 * footer of every page.
 *
 * A dead link is worse than a missing one. A missing link is invisible; a dead
 * link tells the user a feature exists and then denies it, which on a password
 * reset means somebody locked out of their account clicking the one thing that
 * looks like it would help.
 *
 * Run: node scripts/check-dead-ui.mjs
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(fileURLToPath(new URL(".", import.meta.url)), "..");
const WEB = join(ROOT, "apps/web/src");

/**
 * Fragment links that are genuinely fine.
 *
 * Kept deliberately short, and each needs a reason. "It's just a placeholder"
 * is not one — that is the bug this file exists to find.
 */
const DEAD_BY_DESIGN = new Map([
  // Nothing yet. Add entries as `["file.tsx#fragment", "why"]`.
]);

/**
 * Known-dead and awaiting a decision.
 *
 * Separate from DEAD_BY_DESIGN on purpose: these are not fine, they are
 * *acknowledged*. Keeping them in their own list means the count is visible
 * and shrinking rather than quietly absorbed into an allowlist.
 */
const KNOWN_DEAD = new Map([
  // Empty since 12 October 2026: the footer and nav links to a blog, demo,
  // about page and DPA that did not exist were removed, and Contact now opens
  // an email. Add an entry here, with a reason, only for a control that must
  // ship before its destination exists.
]);

/** Keys in KNOWN_DEAD that were actually seen; the rest are stale entries. */
const knownDeadSeen = new Set();

/* ── collect ────────────────────────────────────────────────────────────── */

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    if (entry === "node_modules" || entry === ".next") continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (/\.(tsx|jsx)$/.test(entry)) out.push(full);
  }
  return out;
}

const files = walk(WEB);

/**
 * Every anchor id in the app, not just the current file.
 *
 * Resolving per-file was wrong and noisy: a nav component linking to
 * `#features`, where the section lives in the landing page, is completely
 * normal and was being reported as dead. Cross-file is the common case for
 * exactly the components that carry the most links.
 *
 * The trade-off is accepted deliberately — an id that exists on a *different*
 * page will satisfy this check without actually scrolling anywhere. Catching
 * that needs to know which page renders which component, which is a router
 * graph rather than a grep. The links that matter here are the ones pointing
 * at nothing at all, anywhere.
 */
const ANCHORS = new Set();
for (const file of files) {
  const src = readFileSync(file, "utf8");
  for (const m of src.matchAll(/\bid=["'`]([^"'`]+)["'`]/g)) ANCHORS.add(m[1]);
  for (const m of src.matchAll(/\bname=["'`]([^"'`]+)["'`]/g)) ANCHORS.add(m[1]);
}

const problems = [];
let controlsChecked = 0;

for (const file of files) {
  const src = readFileSync(file, "utf8");
  const name = relative(WEB, file);
  const anchors = ANCHORS;

  // href="#..." in the JSX-attribute, braced, and object-literal forms.
  //
  // The last one is not optional. The whole site footer is built from an array
  // of `{ href: "#terms", label: "Terms of service" }` objects, and the first
  // version of this check only matched the attribute form — so it declared the
  // app clean while seven dead links sat in the footer of every page.
  // `check-routes.mjs` already had a pattern for this shape; not copying it
  // across was the entire gap.
  const HREF_PATTERNS = [
    /href=\{?["'`](#[^"'`{}\s]*)["'`]/g,
    /href:\s*["'`](#[^"'`{}\s]*)["'`]/g,
  ];

  for (const m of HREF_PATTERNS.flatMap((re) => [...src.matchAll(re)])) {
    controlsChecked++;
    const fragment = m[1].slice(1);
    const key = `${name}#${fragment}`;
    const line = src.slice(0, m.index).split("\n").length;

    if (KNOWN_DEAD.has(key)) knownDeadSeen.add(key);
    if (DEAD_BY_DESIGN.has(key) || KNOWN_DEAD.has(key)) continue;

    if (fragment === "") {
      problems.push(`${name}:${line}  href="#" — a placeholder that never got a destination`);
      continue;
    }
    if (!anchors.has(fragment)) {
      problems.push(
        `${name}:${line}  href="#${fragment}" — no element anywhere in the app has ` +
          `id="${fragment}", so clicking it does nothing`,
      );
    }
  }

  for (const m of src.matchAll(/href=\{?["'`]["'`]/g)) {
    controlsChecked++;
    const line = src.slice(0, m.index).split("\n").length;
    problems.push(`${name}:${line}  href="" — renders as a link to the current page`);
  }

  // onClick={() => {}} and onClick={() => undefined}
  for (const m of src.matchAll(/onClick=\{\s*\(\s*\)\s*=>\s*(\{\s*\}|undefined)\s*\}/g)) {
    controlsChecked++;
    const line = src.slice(0, m.index).split("\n").length;
    problems.push(`${name}:${line}  onClick does nothing — a stub that was never filled in`);
  }

  // Menu items, which do none of the above.
  //
  // Added because the poster card's actions moved into an overflow menu, and
  // this check could not see them any more. Everything above looks for a
  // <Button> or a <Link>; a <DropdownMenuItem> is neither, so five controls
  // quietly left the only net that catches dead ones — verified by removing an
  // onSelect and watching this file still report "every control points
  // somewhere".
  //
  // An item does something if it handles a selection, or wraps a real link with
  // `asChild`. One of the two, or it is decoration you can click.
  for (const m of src.matchAll(/<DropdownMenuItem\b/g)) {
    const start = m.index;
    // The tag ends at the first '>' that is not inside braces or quotes.
    let i = start, depth = 0, quote = null, end = -1;
    while (i < src.length) {
      const c = src[i];
      if (quote) { if (c === quote) quote = null; }
      else if (c === '"' || c === "'" || c === "`") quote = c;
      else if (c === "{") depth++;
      else if (c === "}") depth--;
      else if (c === ">" && depth === 0) { end = i; break; }
      i++;
    }
    if (end === -1) continue;

    const tag = src.slice(start, end);
    controlsChecked++;

    if (!/\bonSelect=/.test(tag) && !/\basChild\b/.test(tag)) {
      const line = src.slice(0, start).split("\n").length;
      problems.push(
        `${name}:${line}  <DropdownMenuItem> with no onSelect and no asChild — ` +
          `it looks like a menu action and does nothing when chosen`,
      );
    }
  }
}

/* ── report ─────────────────────────────────────────────────────────────── */

console.log(
  `${files.length} component file(s) · ${controlsChecked} fragment/placeholder control(s) checked`,
);

if (KNOWN_DEAD.size > 0) {
  console.log(`\n${KNOWN_DEAD.size} known-dead control(s), acknowledged and not yet fixed:`);
  for (const [key, why] of KNOWN_DEAD) console.log(`  · ${key}\n      ${why}`);
}

// An acknowledged entry whose control no longer exists is a stale record:
// it makes the list look longer than the debt is, and would silently excuse
// a new dead link that happened to reuse the key.
for (const key of KNOWN_DEAD.keys()) {
  if (!knownDeadSeen.has(key)) {
    problems.push(`${key}  is listed in KNOWN_DEAD but no longer exists — remove the entry`);
  }
}

if (problems.length === 0) {
  console.log("\nEvery control points somewhere.");
  process.exit(0);
}

console.log("");
for (const p of problems) console.log(`DEAD  ${p}`);
console.log(
  `\n${problems.length} control(s) that look clickable and are not.\n` +
    "Give it a destination, or add it to KNOWN_DEAD in this script with a reason.",
);
process.exit(1);

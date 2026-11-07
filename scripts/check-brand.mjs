#!/usr/bin/env node
/**
 * The product is called Marketing25. Nothing a customer reads may say otherwise.
 *
 * Two old names keep resurfacing: "Sonar Marketing25", the previous product
 * name, and "Acme", the codename that is still the namespace root. Both are
 * fine in code and forbidden in copy, which is exactly the distinction a
 * find-and-replace cannot make and a reviewer stops making after ten minutes.
 *
 * So this checks **string literals and visible JSX text only**, and ignores:
 *
 *   · comments — documentation has to be able to say what the old name was,
 *     and a check that forbids explaining itself is worse than no check. An
 *     earlier test in this repo banned a literal domain by scanning whole
 *     files, and failed the moment the fix was documented.
 *   · `Acme.` namespaces, `@acme/` package imports, `acme_` cookie names and
 *     `acme-` container names — internal identifiers, invisible to users.
 *
 * Run: node scripts/check-brand.mjs
 */
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(fileURLToPath(new URL(".", import.meta.url)), "..");

/** Where a customer's eyes reach. */
const SURFACES = [
  { dir: "apps/web/src", exts: /\.(tsx|ts)$/ },
  { dir: "apps/api/src", exts: /\.cs$/ },
  // The worker draws text into the video itself — a watermark there said
  // "Sonar Marketing" long after every screen had been renamed, because this
  // list stopped at the two codebases with screens.
  { dir: "apps/ai-service/app", exts: /\.py$/ },
];

const BANNED = [
  {
    re: /Sonar\s+Marketing/i,
    why: 'the product is "Marketing25" — "Sonar Marketing25" is the previous name',
  },
  {
    re: /\bAcme\b/,
    why: '"Acme" is the internal codename and must not appear in copy',
  },
  {
    // The rule above is word-bounded and case-sensitive, so `acmepharmacy.com`
    // failed it twice over and rode along inside the poster templates until a
    // pharmacy noticed a duplicated address in a campaign they had sent. The
    // fake domain was the half nobody reported.
    re: /acmepharmacy/i,
    why: '"acmepharmacy" is an invented domain and must not reach a poster',
  },
  {
    // Reserved-for-fiction numbers. Harmless in a screenshot, not harmless on
    // a poster a patient reads and dials.
    re: /\b555-0\d{3}\b/,
    why: "an invented phone number must not appear in anything a patient sees",
  },
];

/**
 * Internal uses of the same words. Matched against the whole line.
 *
 * Deliberately specific. A loose rule here quietly switches the check off —
 * `/acme/i` would exempt every line that mentions the namespace, which is most
 * of the API.
 */
/**
 * The one file that has to name the strings this check bans.
 *
 * `TemplatePlaceholderMigration` rewrites the invented contact details out of
 * templates in databases that were seeded before they were fixed, so it must
 * carry each one as a search term. Exact path, not a glob: a rule broad enough
 * to cover "anything named like a migration" is a rule that stops checking.
 */
const EXEMPT_FILE =
  "apps/api/src/Acme.Infrastructure/Persistence/TemplatePlaceholderMigration.cs";

const INTERNAL = [
  /@acme\//,                       // package imports
  /\bAcme\.[A-Z]/,                 // Acme.Domain, Acme.WebApi — namespaces and types
  /\bnamespace\s+Acme\b/,
  /\busing\s+Acme\b/,
  /\backme[_-][a-z]/,              // acme_access cookie, acme-api container
  /"acme"/,                        // the Mongo database name
];

/* ── reading ────────────────────────────────────────────────────────────── */

function walk(dir, exts, out = []) {
  for (const entry of readdirSync(dir)) {
    if (entry === "node_modules" || entry === ".next" || entry === "bin" || entry === "obj") continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) walk(full, exts, out);
    else if (exts.test(entry)) out.push(full);
  }
  return out;
}

/**
 * Blanks out comments, leaving line numbers intact.
 *
 * Replaces rather than deletes so a reported line number still points at the
 * right line in the editor.
 */
function stripComments(src) {
  const out = src.split("\n");
  let inBlock = false;

  for (let i = 0; i < out.length; i++) {
    const line = out[i];
    const trimmed = line.trim();

    if (inBlock) {
      out[i] = "";
      if (trimmed.includes("*/")) inBlock = false;
      continue;
    }
    if (trimmed.startsWith("/*")) {
      out[i] = "";
      if (!trimmed.includes("*/")) inBlock = true;
      continue;
    }
    if (trimmed.startsWith("//") || trimmed.startsWith("*") || trimmed.startsWith("///")) {
      out[i] = "";
      continue;
    }
    // Trailing line comment: keep the code before it.
    const idx = line.indexOf("//");
    if (idx > -1 && !/https?:$/.test(line.slice(0, idx + 1))) out[i] = line.slice(0, idx);
  }
  return out;
}

/* ── checking ───────────────────────────────────────────────────────────── */

const problems = [];
let filesChecked = 0;

for (const { dir, exts } of SURFACES) {
  for (const file of walk(join(ROOT, dir), exts)) {
    filesChecked++;
    const name = relative(ROOT, file);

    // One file is allowed to contain the banned strings: the migration whose
    // job is to find and remove them. Exempting it by exact path rather than by
    // a pattern, so the exemption cannot spread to a file that merely looks
    // similar.
    if (name === EXEMPT_FILE) continue;

    const lines = stripComments(readFileSync(file, "utf8"));

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (!line.trim()) continue;
      if (INTERNAL.some((re) => re.test(line))) continue;

      for (const { re, why } of BANNED) {
        if (re.test(line)) {
          problems.push(`${name}:${i + 1}  ${why}\n        ${line.trim().slice(0, 100)}`);
          break;
        }
      }
    }
  }
}

/* ── report ─────────────────────────────────────────────────────────────── */

console.log(`${filesChecked} user-facing source file(s) checked`);

if (problems.length === 0) {
  console.log("\nEvery name a customer reads is Marketing25.");
  process.exit(0);
}

console.log("");
for (const p of problems) console.log(`BRAND  ${p}`);
console.log(
  `\n${problems.length} occurrence(s) of a retired name in copy.\n` +
    "Namespaces and identifiers are fine — this only looks at what users read.",
);
process.exit(1);

#!/usr/bin/env node
/**
 * Every API action that changes something says how it is audited.
 *
 * The audit log is written by AuditMiddleware for endpoints that carry
 * [Audit(...)], and views are written for endpoints that carry
 * [ActivityLog(...)]. Nothing is inferred. That keeps GET polls and health
 * checks out of the log by construction — and it means a new POST without an
 * attribute would silently go unrecorded. This check closes that gap:
 *
 *   1. Every [HttpPost] / [HttpPut] / [HttpPatch] / [HttpDelete] action has
 *      [Audit("…")] or [NoAudit("reason")], and the reason is not empty.
 *   2. [ActivityLog] is only on GETs — a view that changes data is a mutation.
 *   3. Action names are dotted lower-case and start with a known prefix, so
 *      the console's prefix filter ("poster.", "admin.") finds them.
 *   4. One mark per action; [Audit] and [NoAudit] together is a contradiction.
 *
 * Run: node scripts/check-audit.mjs
 */
import { readFileSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const DIR = join(ROOT, "apps/api/src/Acme.WebApi/Controllers");

const PREFIXES = [
  "poster", "video", "campaign", "patient", "brand", "display", "auth", "admin",
  "template", "subscription", "profile", "pharmacy", "social", "dashboard",
];
const NAME = /^[a-z][a-z_]*(\.[a-z][a-z_]*)+$/;

const MUTATING = /^\[Http(Post|Put|Patch|Delete)\b/;
const ANY_HTTP = /^\[Http(Get|Post|Put|Patch|Delete)\b/;

const problems = [];
let checked = 0;

for (const file of readdirSync(DIR).filter((f) => f.endsWith(".cs")).sort()) {
  const lines = readFileSync(join(DIR, file), "utf8").split("\n");

  lines.forEach((raw, i) => {
    const line = raw.trim();
    if (!ANY_HTTP.test(line)) return;

    // The attribute block around this verb: contiguous lines starting with
    // '[' above and below (doc comments end the block above).
    const block = [line];
    for (let j = i - 1; j >= 0 && lines[j].trim().startsWith("["); j--) block.push(lines[j].trim());
    for (let j = i + 1; j < lines.length && lines[j].trim().startsWith("["); j++) block.push(lines[j].trim());

    const where = `${file}:${i + 1} ${line}`;
    const audit = block.filter((l) => l.startsWith("[Audit("));
    const noAudit = block.filter((l) => l.startsWith("[NoAudit("));
    const activity = block.filter((l) => l.startsWith("[ActivityLog("));

    if (audit.length + noAudit.length + activity.length > 1)
      problems.push(`${where}: more than one audit mark`);

    if (MUTATING.test(line)) {
      checked++;
      if (activity.length) problems.push(`${where}: [ActivityLog] on a mutating verb — use [Audit]`);
      if (!audit.length && !noAudit.length)
        problems.push(`${where}: needs [Audit("area.verb", "entity")] or [NoAudit("why")]`);
    }

    for (const n of noAudit) {
      const reason = /\[NoAudit\("([^"]*)"\)\]/.exec(n)?.[1]?.trim() ?? "";
      if (reason.length < 10) problems.push(`${where}: [NoAudit] needs a real reason`);
    }

    for (const a of [...audit, ...activity]) {
      const name = /^\[(?:Audit|ActivityLog)\("([^"]+)"/.exec(a)?.[1] ?? "";
      if (!NAME.test(name)) problems.push(`${where}: action "${name}" is not dotted lower-case`);
      else if (!PREFIXES.includes(name.split(".")[0]))
        problems.push(`${where}: action "${name}" has an unknown prefix (known: ${PREFIXES.join(", ")})`);
      const failed = /FailedAction = "([^"]+)"/.exec(a)?.[1];
      if (failed && !NAME.test(failed)) problems.push(`${where}: FailedAction "${failed}" is not dotted lower-case`);
    }
  });
}

if (problems.length) {
  console.error(`check-audit: ${problems.length} problem(s)\n  ` + problems.join("\n  "));
  process.exit(1);
}
console.log(`check-audit: ${checked} mutating actions, all accounted for`);

#!/usr/bin/env node
/**
 * Guards the end-to-end suite against the mistake that made a quarter of its
 * status checks meaningless.
 *
 * `req` assigns HTTP_STATUS. Called inside `$( … )` or a pipeline it runs in a
 * subshell, the assignment is discarded, and the caller compares against
 * whatever the previous request happened to leave behind. Twenty-five checks in
 * run.sh were written that way and had never tested the request they appeared
 * to test. `rq` exists to fix it, and has exactly the same failure mode if it is
 * used the same way.
 *
 * Two rules:
 *   1. `rq` never appears inside a command substitution or a pipeline.
 *   2. `req` output is never captured — use `rq` and read $BODY.
 *
 * `req … >/dev/null` is fine: a redirection is not a subshell.
 *
 * Run: node scripts/check-e2e.mjs
 */
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const DIR = join(fileURLToPath(new URL(".", import.meta.url)), "e2e");
const problems = [];

// selftest.sh is the harness, not a caller: it *defines* the stubbed `rq` on
// top of the echoing `req`, and passes the status through a file precisely
// because the subshell would otherwise eat it. Exempt by name rather than by
// pattern, so a new script cannot exempt itself by accident.
const SCRIPTS = readdirSync(DIR).filter((f) => f.endsWith(".sh") && f !== "selftest.sh");

for (const name of SCRIPTS) {
  const text = readFileSync(join(DIR, name), "utf8");
  const lines = text.split("\n");

  for (let i = 0; i < lines.length; i++) {
    // Join line continuations so a pipe on a later line is still seen.
    let stmt = lines[i];
    let end = i;
    while (stmt.trimEnd().endsWith("\\") && end + 1 < lines.length) {
      stmt += "\n" + lines[++end];
    }
    const bare = stmt.replace(/#.*$/gm, "");

    if (/^\s*rq\s/.test(bare) && /\|/.test(bare)) {
      problems.push(
        `${name}:${i + 1}  rq is in a pipeline — that is a subshell, so $BODY and ` +
          "$HTTP_STATUS are discarded",
      );
    }
    if (/=\s*"?\$\(\s*rq\s/.test(bare)) {
      problems.push(
        `${name}:${i + 1}  rq inside $( … ) — a subshell; assign from $BODY instead`,
      );
    }
    if (/=\s*"?\$\(\s*req\s/.test(bare)) {
      problems.push(
        `${name}:${i + 1}  req output captured — HTTP_STATUS will be the PREVIOUS ` +
          "request's status. Use rq and read $BODY.",
      );
    }
  }
}

console.log(`${SCRIPTS.length} e2e script(s) checked\n`);

if (problems.length === 0) {
  console.log("Every request helper is used in the current shell.");
  process.exit(0);
}
for (const p of problems) console.log(`FAIL  ${p}`);
console.log(`\n${problems.length} problem(s).`);
process.exit(1);

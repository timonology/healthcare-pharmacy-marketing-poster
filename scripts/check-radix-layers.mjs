#!/usr/bin/env node
/**
 * One layer manager, or the app freezes.
 *
 * Radix's `DismissableLayer` is what makes a modal modal: while one is open it
 * sets `document.body.style.pointerEvents = "none"` so clicks cannot reach the
 * page behind, and restores the previous value when the last layer closes.
 *
 * Both halves of that live in module scope:
 *
 *     var originalBodyPointerEvents;              // the value to restore
 *     context.layersWithOutsidePointerEventsDisabled   // how many are open
 *
 * So the bookkeeping is correct only if every layer on the page shares one
 * copy of the module. Two copies means two independent bookkeepers, both
 * writing the same `document.body` property and neither aware of the other:
 *
 *   1. a dropdown opens (copy A)  — saves "", sets body to "none"
 *   2. a dialog opens on top (copy B, its own counter still at zero)
 *                                 — saves the body's *current* value: "none"
 *   3. both close                 — copy B restores "none"
 *
 * The page then renders perfectly and cannot be clicked at all. Nothing looks
 * broken, no error is logged, and only a refresh clears it — which is exactly
 * how it was reported.
 *
 * <b>Why this happens by accident.</b> Radix packages pin their internal
 * dependencies to *exact* versions rather than ranges, so `react-dialog` asking
 * for 1.1.11 and `react-menu` asking for 1.1.19 is not a conflict pnpm can
 * resolve — it installs both, correctly, and the app breaks. Nothing in the
 * repository's own dependencies says anything is wrong.
 *
 * The fix is the `pnpm.overrides` entry in the root package.json. This check
 * exists because that entry is one line that a later dependency bump could
 * quietly drop, and the symptom it prevents is silent, intermittent, and looks
 * nothing like a dependency problem.
 */
import { readdirSync, existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

const STORE = join(process.cwd(), "node_modules", ".pnpm");

/**
 * Packages whose module scope holds state shared across the whole document.
 * A second copy of any of these is the same class of fault.
 */
const MUST_BE_SINGLETON = [
  {
    pkg: "@radix-ui+react-dismissable-layer",
    breaks:
      "Each copy keeps its own record of what document.body's pointer-events\n" +
      "     was before a layer opened, so one of them restores the wrong value\n" +
      "     and the page becomes unclickable until it is reloaded.",
  },
  {
    pkg: "@radix-ui+react-focus-scope",
    breaks:
      "Each copy keeps its own stack of trapped-focus containers, so two open\n" +
      "     layers can both believe they own focus — tab order escapes the\n" +
      "     dialog, or focus is yanked back and forth between them.",
  },
  {
    pkg: "@radix-ui+react-focus-guards",
    breaks:
      "Each copy keeps its own `count` of open layers and its own pair of\n" +
      "     guard <span>s at the edges of <body>. Two counters means one copy\n" +
      "     can tear its guards out while the other still needs them, so Tab\n" +
      "     from the last control in a dialog escapes to the page behind it.",
  },
  {
    pkg: "react-remove-scroll-bar",
    breaks:
      "Each copy counts open scroll locks separately, so the padding added to\n" +
      "     compensate for the hidden scrollbar can be left on the page and the\n" +
      "     layout stays shifted after everything is closed.",
  },
];

if (!existsSync(STORE)) {
  console.log("radix layers: SKIP (no pnpm store — run pnpm install first)");
  process.exit(0);
}

// Only what the lockfile installs. pnpm leaves a superseded version's
// directory in the virtual store after an upgrade until it is pruned; nothing
// links to it, Node never loads it, and counting it would fail this check for
// a copy that is not in the app.
const lockPath = join(process.cwd(), "pnpm-lock.yaml");
const lock = existsSync(lockPath) ? readFileSync(lockPath, "utf8") : "";
const npmName = (pkg) => pkg.replace(/^@([^+]+)\+/, "@$1/");
const inLock = (pkg, version) =>
  !lock || lock.includes(`'${npmName(pkg)}@${version}'`) || lock.includes(`\n  ${npmName(pkg)}@${version}:`);

const snapshotKeys = (() => {
  const i = lock.indexOf("\nsnapshots:\n");
  if (i < 0) return [];
  return [...lock.slice(i).matchAll(/^  '?([^\s':][^\n]*?)'?:\s*$/gm)].map((m) => m[1]);
})();

const entries = readdirSync(STORE);
let failed = false;

for (const { pkg, breaks } of MUST_BE_SINGLETON) {
  // <b>Directories, not versions.</b>
  //
  // Store directories are named `<name>@<version>_<peer hash>`, and it is
  // tempting to treat the hash as noise and count distinct versions. It is not
  // noise: each directory is a different realpath, Node keys its module
  // registry by realpath, so two directories are two module objects with two
  // sets of module-scoped state — which is the whole problem — even when the
  // version is identical. A peer-hash split would then leave the page freezing
  // while this check reported everything was fine.
  // From the lockfile when there is one: each snapshot key is one installed
  // instance, peers included — the same thing a store directory is, but
  // without the directories an earlier install left behind (an upgrade of
  // react or @types/react leaves a whole set of old-peer copies in the store
  // that nothing links to). The store is only the fallback.
  const dirs = snapshotKeys.length
    ? snapshotKeys.filter((k) => k.startsWith(`${npmName(pkg)}@`))
        .map((k) => `${pkg}@${k.slice(npmName(pkg).length + 1).replace(/\(/, "_(")}`)
    : entries
        .filter((e) => e.startsWith(`${pkg}@`))
        .filter((e) => inLock(pkg, e.slice(pkg.length + 1).split("_")[0]));
  const versions = [...new Set(dirs.map((e) => e.slice(pkg.length + 1).split("_")[0]))];

  if (dirs.length > 1) {
    failed = true;
    const detail =
      versions.length > 1
        ? `versions ${versions.sort().join(", ")}`
        : `version ${versions[0]}, installed ${dirs.length}× under different ` +
          "peer dependencies — still separate module instances";
    console.error(`FAIL ${pkg}: ${dirs.length} copies installed (${detail}).`);
    console.error(`     ${breaks}`);
    console.error(
      versions.length > 1
        ? "     Pin a single version in the root package.json under " +
            "pnpm.overrides."
        : "     Align the peer dependencies (usually a single react / " +
            "@types/react\n     version across the workspace).",
    );
  } else if (dirs.length === 1) {
    console.log(`  ok  ${pkg} @ ${versions[0]}`);
  }
}

// <b>The pin must be what the Radix packages ask for.</b>
//
// One copy is not enough if it is the wrong copy. Radix packages import named
// exports from these modules that exist only from the version they pin, so an
// override left on an older version after `pnpm up "@radix-ui/*"` installs one
// copy — this check passes — and the build then fails with "'X' is not exported
// from '@radix-ui/react-focus-scope'". Seen on the React 19 upgrade.
const overrides =
  JSON.parse(readFileSync(join(process.cwd(), "package.json"), "utf8")).pnpm?.overrides ?? {};
const mismatches = new Map();
for (const dir of entries.filter((e) => e.startsWith("@radix-ui+"))) {
  const [bare, version] = dir.slice("@radix-ui+".length).split("_")[0].split("@");
  const name = "@radix-ui/" + bare;
  if (!inLock(`@radix-ui+${bare}`, version)) continue;
  const manifest = join(STORE, dir, "node_modules", name, "package.json");
  if (!existsSync(manifest)) continue;
  const deps = JSON.parse(readFileSync(manifest, "utf8")).dependencies ?? {};
  for (const [dep, wanted] of Object.entries(deps)) {
    const pinned = overrides[dep];
    if (pinned && pinned !== wanted) {
      mismatches.set(`${name} → ${dep}`, `asks for ${wanted}, overrides pin ${pinned}`);
    }
  }
}
if (mismatches.size > 0) {
  failed = true;
  for (const [what, why] of mismatches) console.error(`FAIL ${what}: ${why}.`);
  console.error(
    "     Set the pnpm.overrides in the root package.json to the version the\n" +
      "     Radix packages pin, then pnpm install.",
  );
}

if (failed) process.exit(1);
console.log("radix layers: PASS (one copy of each document-scoped module)");

#!/usr/bin/env node
/**
 * The photo framing preview and the render do the same sums.
 *
 * The dialog promises that what a pharmacist frames is what the video shows.
 * That is only true while `packages/shared-types/src/framing.ts` (the preview)
 * and `apps/ai-service/app/video_agent/framing.py` (the render) agree to the
 * pixel. Both are written to reproduce one table of answers; this check runs
 * the TypeScript against it, and `tests/test_framing.py` runs the Python.
 *
 * A single row disagreeing means one photo, at one zoom and focal point, will
 * render somewhere other than where the pharmacist put it — and nobody would
 * notice until they did.
 */
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { join } from "node:path";

const root = process.cwd();

/**
 * The workspace's own TypeScript, never whatever `require("typescript")` finds.
 *
 * Resolved from a package that actually depends on it, then from the pnpm
 * store, and accepted only if it can transpile. A bare resolve from the repo
 * root walks up to any global install — on one machine that was a stub exposing
 * nothing but a version string — and a check whose result depends on what else
 * happens to be installed is not a check.
 */
function workspaceTypeScript() {
  const candidates = [
    join(root, "packages/shared-types/package.json"),
    join(root, "apps/web/package.json"),
  ];
  for (const pkg of candidates) {
    try {
      const ts = createRequire(pkg)("typescript");
      if (typeof ts.transpileModule === "function") return ts;
    } catch { /* try the next */ }
  }
  const store = join(root, "node_modules", ".pnpm");
  if (existsSync(store)) {
    for (const dir of readdirSync(store).filter((d) => d.startsWith("typescript@"))) {
      try {
        const ts = createRequire(join(root, "package.json"))(
          join(store, dir, "node_modules", "typescript"),
        );
        if (typeof ts.transpileModule === "function") return ts;
      } catch { /* try the next */ }
    }
  }
  return null;
}

const ts = workspaceTypeScript();
if (!ts) {
  console.log("framing: SKIP (no usable TypeScript in the workspace — run pnpm install)");
  process.exit(0);
}

const source = readFileSync(join(root, "packages/shared-types/src/framing.ts"), "utf8");
const js = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2020 },
}).outputText;
const mod = await import(`data:text/javascript;base64,${Buffer.from(js).toString("base64")}`);

const fixture = JSON.parse(
  readFileSync(join(root, "packages/shared-types/fixtures/framing-geometry.json"), "utf8"),
);

let wrong = 0;
for (const { in: args, out } of fixture.rows) {
  const g = mod.framingGeometry(...args);
  const got = [g.width, g.height, g.x, g.y];
  // `Object.is` would call -0 and 0 different; a pixel offset of -0 is 0.
  if (got.some((v, i) => v + 0 !== out[i] + 0)) {
    if (wrong < 5) {
      console.error(`  MISMATCH in=${JSON.stringify(args)} expected=${JSON.stringify(out)} got=${JSON.stringify(got)}`);
    }
    wrong++;
  }
}

if (wrong) {
  console.error(
    `framing: FAIL — ${wrong} of ${fixture.rows.length} rows disagree. The preview ` +
      "would show a different crop from the one the video renders.",
  );
  process.exit(1);
}
console.log(`framing: PASS — preview reproduces all ${fixture.rows.length} golden rows`);

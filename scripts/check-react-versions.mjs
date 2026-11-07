#!/usr/bin/env node
/**
 * The React the editor's canvas library is built for must be the React the
 * app actually runs.
 *
 * Next.js's App Router does not run the `react` in package.json in the
 * browser: it bundles its own copy (next/dist/compiled/react). react-konva
 * ships one release line per React minor and refuses to start on another —
 * react-konva 19.3 with Next 15.5 (which bundles React 19.2) threw
 * "react-konva requires React 19.3 or later" and the poster editor showed
 * "Application error". Nothing at build time noticed; only opening a poster
 * did. This compares the three numbers that have to agree.
 */
import { readFileSync, existsSync } from "node:fs";
import { createRequire } from "node:module";
import { join } from "node:path";

const WEB = join(process.cwd(), "apps", "web");
const require = createRequire(join(WEB, "package.json"));

function versionOf(spec) {
  try {
    return JSON.parse(readFileSync(require.resolve(`${spec}/package.json`), "utf8")).version;
  } catch {
    return null;
  }
}

const nextDir = (() => {
  try { return join(require.resolve("next/package.json"), ".."); } catch { return null; }
})();
if (!nextDir) {
  console.log("react versions: SKIP (next not installed — run pnpm install first)");
  process.exit(0);
}

const bundledPath = join(nextDir, "dist", "compiled", "react", "cjs", "react.production.js");
const bundled = existsSync(bundledPath)
  ? /exports\.version = "([^"]+)"/.exec(readFileSync(bundledPath, "utf8"))?.[1] ?? null
  : null;
const react = versionOf("react");
const konva = versionOf("react-konva");

const minor = (v) => (v ? v.split(".").slice(0, 2).join(".") : "?");
console.log(`  next bundles React ${bundled ?? "?"} · react ${react ?? "?"} · react-konva ${konva ?? "?"}`);

const problems = [];
if (bundled && konva && minor(konva) !== minor(bundled)) {
  problems.push(
    `react-konva ${konva} is built for React ${minor(konva)}, but Next runs React ${bundled} ` +
      `in the browser. Use react-konva ${minor(bundled)}.x.`,
  );
}
if (bundled && react && minor(react) !== minor(bundled)) {
  problems.push(
    `react ${react} differs from the React ${minor(bundled)} Next bundles. Keep them on the ` +
      "same minor so tests and the server render run the version the browser runs.",
  );
}

if (problems.length) {
  for (const p of problems) console.error(`FAIL ${p}`);
  process.exit(1);
}
console.log("react versions: PASS");

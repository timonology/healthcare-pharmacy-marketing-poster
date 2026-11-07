#!/usr/bin/env node
/**
 * Fails when the UI uses a colour that does not exist.
 *
 * The bug this exists for: `bg-popover` and `text-popover-foreground` were used
 * by the account menu and the pharmacy switcher, but `popover` was never
 * declared in tailwind.config and `--popover` was never defined in globals.css.
 * Tailwind does not warn about a class it doesn't recognise — it emits nothing.
 * So the menus rendered with *no background at all*: transparent panels with
 * their text overlapping the sidebar links underneath. Everything about the
 * markup was correct and the failure was invisible outside a screenshot.
 *
 * Two things are checked, and the second is the one with teeth:
 *
 *  1. Every colour declared in tailwind.config resolves to a CSS variable that
 *     is defined in *both* themes. A token defined only in `:root` looks fine
 *     until someone switches to dark and the element turns transparent.
 *
 *  2. Every semantic colour class used anywhere in the app actually compiles.
 *     This is decided by running Tailwind and looking at the output, not by
 *     pattern-matching the config — a real compiler settles the question, and
 *     my own guesses about what Tailwind accepts have been wrong before.
 */
import { execFileSync } from "node:child_process";
import { readFileSync, readdirSync, mkdtempSync, statSync } from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";

const web = join(dirname(fileURLToPath(import.meta.url)), "..", "apps", "web");
const fail = [];

/* ------------------------------------------------------------------ *
 * 1. Declared colours must be defined in both themes.
 * ------------------------------------------------------------------ */
const configPath = ["tailwind.config.ts", "tailwind.config.js", "tailwind.config.mjs"]
  .map((f) => join(web, f))
  .find((f) => { try { statSync(f); return true; } catch { return false; } });

const configSrc = readFileSync(configPath, "utf8");
const cssSrc = readFileSync(join(web, "src", "app", "globals.css"), "utf8");

// Every `hsl(var(--x))` the config's *colours* rely on. Scoped to the colours
// block on purpose: --radius is a length, has one correct value, and demanding
// a dark counterpart for it would be noise that trains people to ignore this.
const colours = configSrc.slice(
  configSrc.indexOf("colors:"),
  configSrc.indexOf("borderRadius:") === -1 ? configSrc.length : configSrc.indexOf("borderRadius:"),
);
const needed = [...colours.matchAll(/var\(--([a-z0-9-]+)\)/g)].map((m) => m[1]);

// The two theme blocks, sliced apart so "defined somewhere" cannot pass for
// "defined in the theme you are looking at".
const darkAt = cssSrc.indexOf(".dark");
const light = cssSrc.slice(0, darkAt === -1 ? cssSrc.length : darkAt);
const dark = darkAt === -1 ? "" : cssSrc.slice(darkAt);

for (const name of [...new Set(needed)]) {
  const decl = new RegExp(`--${name}\\s*:`);
  if (!decl.test(light)) fail.push(`--${name} is used by tailwind.config but never defined for the light theme`);
  if (!decl.test(dark)) fail.push(`--${name} is used by tailwind.config but never defined for the dark theme`);
}

/* ------------------------------------------------------------------ *
 * 2. Semantic colour classes in the source must compile to a rule.
 * ------------------------------------------------------------------ */
const PREFIXES = "bg|text|border|ring|fill|stroke|from|via|to|placeholder|caret|divide|outline|decoration|shadow";

// Tailwind's own palette and the non-colour values these prefixes also take.
// Anything left over is a name the project invented, which is exactly the set
// that has to be declared.
const BUILT_IN = new Set([
  "inherit", "current", "transparent", "black", "white", "slate", "gray", "grey",
  "zinc", "neutral", "stone", "red", "orange", "amber", "yellow", "lime", "green",
  "emerald", "teal", "cyan", "sky", "blue", "indigo", "violet", "purple", "fuchsia",
  "pink", "rose", "none", "auto", "left", "right", "center", "justify", "start",
  "end", "wrap", "nowrap", "balance", "pretty", "clip", "ellipsis", "solid",
  "dashed", "dotted", "double", "hidden", "inner", "sm", "md", "lg", "xl", "base",
  "xs", "top", "bottom", "opacity", "offset", "width", "color", "spacing",
]);

const files = [];
(function walk(dir) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === "node_modules" || e.name.startsWith(".")) continue;
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (/\.(tsx|ts|jsx|js)$/.test(e.name)) files.push(p);
  }
})(join(web, "src"));

// class -> where we first saw it, so a failure names a file rather than a token.
const candidates = new Map();
const CLASS_RE = new RegExp(`(?<![\\w-])(?:${PREFIXES})-([a-z][a-z-]*)(?![\\w-])`, "g");

for (const file of files) {
  const text = readFileSync(file, "utf8")
    // Prose mentions a class without using it ("drawn with stroke-dasharray"),
    // and `fill="url(#fill-posters)"` names a gradient, not a colour.
    .replace(/\/\*[\s\S]*?\*\//g, " ")
    .replace(/(^|[^:])\/\/[^\n]*/g, "$1 ")
    .replace(/url\(#[^)]*\)/g, " ");

  for (const m of text.matchAll(CLASS_RE)) {
    const root = m[1].split("-")[0];
    if (BUILT_IN.has(root)) continue;
    if (!candidates.has(m[0])) candidates.set(m[0], file.slice(web.length + 1));
  }
}

// Compile once, with the project's real config, and ask what survived.
const out = join(mkdtempSync(join(tmpdir(), "theme-")), "out.css");
execFileSync(
  "npx",
  ["tailwindcss", "-c", configPath, "-i", join(web, "src", "app", "globals.css"), "-o", out],
  { cwd: web, stdio: "pipe" },
);
const compiled = readFileSync(out, "utf8");

for (const [cls, where] of candidates) {
  // A class used only under a variant is emitted as `.hover\:text-accent-foreground:hover`
  // — the name is there, but not directly after a dot. Anchoring on `.` alone
  // reported six healthy classes as dead, which is how a check gets switched off.
  const emitted = new RegExp(`[.:]${cls.replace(/[-]/g, "\\-")}(?![\\w-])`);
  if (!emitted.test(compiled)) {
    fail.push(`${where}: "${cls}" compiles to nothing — the element will have no ${cls.split("-")[0]}`);
  }
}

/* ------------------------------------------------------------------ */
if (fail.length) {
  console.error("check-theme: FAIL\n");
  for (const f of fail) console.error(`  ${f}`);
  console.error(`\n${fail.length} problem(s). A colour class Tailwind does not recognise is silently dropped.`);
  process.exit(1);
}

console.log(`check-theme: ok (${new Set(needed).size} theme variables in both themes, ${candidates.size} semantic colour classes all compile)`);

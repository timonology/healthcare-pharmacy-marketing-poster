#!/usr/bin/env node
/**
 * Three invariants about the app shell, its forms, and its full-height pages.
 *
 * <b>1. A sign-in page never renders the signed-in chrome.</b>
 * The bug: AppShell chose its chrome with `isMarketing && !user`, so a stale
 * user object in the client store was enough to draw the sidebar around the
 * login page. Signing out landed people on /login with a full navigation menu
 * for the account they had just left. Being signed out is not something to
 * infer from client state that may be a step behind, so the route decides —
 * and this check fails if that condition ever grows a `&& user` again.
 *
 * <b>2. Every password field can be revealed.</b>
 * Typing a password blind is where sign-in failures come from, and a second
 * failure sends people into the reset flow, which signs out all their
 * sessions. A raw `type="password"` input is a field with no way to check
 * what was typed.
 *
 * <b>3. A full-height page allows for the manage-as banner.</b>
 * `h-screen` under that banner is a full viewport starting below one, so the
 * page overflows the window by the banner's height and the sticky banner then
 * scrolls over the page's own header. In the poster designer that covered the
 * title field, which QA could not click at all.
 *
 * None of these can be caught by the type checker or by a route check: every
 * file involved is individually valid.
 */
import { readFileSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const web = join(dirname(fileURLToPath(import.meta.url)), "..", "apps", "web");
const src = join(web, "src");
const fail = [];

const files = [];
(function walk(dir) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === "node_modules" || e.name.startsWith(".")) continue;
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (/\.tsx$/.test(e.name)) files.push(p);
  }
})(src);

/* ---- 1. auth routes are decided by the route, not by the store ---- */
const shellPath = join(src, "components", "site", "AppShell.tsx");
const shell = readFileSync(shellPath, "utf8");

const decision = /if \(([^)]*isAuthPage[^)]*)\)/.exec(shell);
if (!decision) {
  fail.push(
    "AppShell no longer decides its chrome on isAuthPage — the sign-in pages "
    + "can fall through to the signed-in shell again",
  );
} else if (/isAuthPage\s*&&/.test(decision[1])) {
  fail.push(
    `AppShell guards isAuthPage with a condition: \`${decision[1].trim()}\`. `
    + "It must be unconditional — the whole bug was the chrome depending on "
    + "client state that lags behind the actual session",
  );
}

for (const route of ["login", "register", "onboarding", "forgot-password", "reset-password"]) {
  if (!shell.includes(`/^\\/${route}\\/?$/`)) {
    fail.push(`/${route} is not listed in AppShell's AUTH_PATTERNS, so it can render the sidebar`);
  }
}

/* ---- 2. no bare password inputs ---- */
for (const file of files) {
  if (file.endsWith("password-input.tsx")) continue; // the one place it belongs
  const text = readFileSync(file, "utf8");
  if (text.includes('type="password"')) {
    fail.push(
      `${file.slice(web.length + 1)}: a raw type="password" input — `
      + "use <PasswordInput/> so it can be revealed",
    );
  }
}

/* ---- and each form's reveal buttons are told apart ---- */
for (const file of files) {
  const text = readFileSync(file, "utf8");
  const fields = (text.match(/<PasswordInput\b/g) ?? []).length;
  if (fields < 2) continue;

  const labelled = (text.match(/revealLabel=/g) ?? []).length;
  if (labelled < fields - 1) {
    fail.push(
      `${file.slice(web.length + 1)}: ${fields} password fields but ${labelled} `
      + "revealLabel — every reveal button would be announced as \"Show password\", "
      + "so a screen reader user cannot tell which field they are exposing",
    );
  }
}

/* ---- 3. full-height pages must allow for the manage-as banner ---- */
//
// `h-screen` is a full viewport. Under the manage-as banner it becomes a full
// viewport *starting below one*, so the page ends up taller than the window by
// the banner's height — and because the banner is sticky, the page then scrolls
// it straight over whatever is at the top. In the poster designer that was the
// title field, which QA could not click at all.
//
// `h-app-screen` subtracts the banner. That holds for every page the app shell
// wraps — which is every page except the ones AppShell explicitly refuses to
// wrap.
//
// The exception is real rather than a convenience. A route in
// NO_CHROME_PATTERNS renders no header, no sidebar and no manage-as banner at
// all, so there is no banner height to subtract; `h-app-screen` there would
// shrink the page by a bar that does not exist. The kiosk is the case that
// forced this: a screen on a pharmacy wall is a full viewport by definition,
// and it is not signed in, so no banner can ever appear above it.
//
// Derived from AppShell rather than listed here, so a page added to or removed
// from that list cannot drift out of step with this check.
// AppShell is already read above, for check 1.
const noChromeBlock = shell.match(
  /const NO_CHROME_PATTERNS: RegExp\[\] = \[([\s\S]*?)\];/);

if (!noChromeBlock) {
  console.error(
    "check-shell: could not find NO_CHROME_PATTERNS in AppShell — this check "
    + "cannot tell which pages render without the banner, so it is not safe to run.",
  );
  process.exit(1);
}

/** Route prefixes AppShell renders bare. */
const chromeless = [...noChromeBlock[1].matchAll(/\/\^\\\/([\w-]*)/g)]
  .map((m) => m[1])
  .filter(Boolean);

const rendersBare = (file) => {
  const route = file.slice(join(web, "src/app").length).replace(/\/page\.tsx$/, "");
  return chromeless.some((prefix) => route.startsWith(`/${prefix}`));
};

for (const file of files) {
  if (rendersBare(file)) continue;
  const text = readFileSync(file, "utf8");
  for (const [i, line] of text.split("\n").entries()) {
    if (line.trimStart().startsWith("//") || line.trimStart().startsWith("*")) continue;
    if (/(?<![\w-])h-screen(?![\w-])/.test(line)) {
      fail.push(
        `${file.slice(web.length + 1)}:${i + 1}: \`h-screen\` — use \`h-app-screen\`, `
        + "or the manage-as banner will push this page past the bottom of the window "
        + "and then cover its header",
      );
    }
  }
}

if (fail.length) {
  console.error("check-shell: FAIL\n");
  for (const f of fail) console.error(`  ${f}`);
  process.exit(1);
}

console.log(
  `check-shell: ok (auth routes never render the app shell, `
  + `every password field has a distinct reveal control, no bare h-screen)`,
);

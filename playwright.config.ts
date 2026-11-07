import { defineConfig, devices } from "@playwright/test";

/**
 * Browser tests run against a LIVE stack — web, API and Mongo all up.
 *
 * They are not part of `pnpm test`, which must stay runnable with nothing
 * running. A suite that needs infrastructure and is wired into the default
 * command turns into a suite people skip, and a skipped suite is worse than no
 * suite because its name still appears in the output.
 *
 *   pnpm test:browser              # against localhost:3000
 *   WEB_URL=https://…  pnpm test:browser
 */
// Starting the web app is Playwright's job when it is not already up. The API
// is not: it needs MongoDB, it has its own lifecycle, and a `dotnet run` that
// fails inside a test harness produces a far worse message than the global
// setup's "start it with…". So this removes the friction that actually recurs
// — a forgotten `pnpm dev:web` — and leaves the rest explicit.
//
// Skipped entirely when WEB_URL points somewhere else: starting a local server
// to test a remote deployment would be nonsense.
const LOCAL_WEB = !process.env.WEB_URL || process.env.WEB_URL.includes("localhost");

// A production build by default, the dev server on request (E2E_WEB_MODE=dev).
//
// The dev server compiles each route the first time it is visited. On Next 15
// that took 20 s to nearly 2 min per route on a developer laptop, with the API
// and Mongo running alongside — so a suite of 125 tests spent most of its
// time waiting on the compiler and failed on timeouts that had nothing to do
// with the app ("Loading poster…", sign-in redirects that never arrived). A
// build is paid once, up front, and then every page answers in milliseconds,
// as it will in production. It also tests what ships rather than dev mode.
const DEV_SERVER = process.env.E2E_WEB_MODE === "dev";

export default defineConfig({
  testDir: "./tests/browser",

  webServer: LOCAL_WEB
    ? {
        command: DEV_SERVER
          ? "pnpm --filter web dev"
          : "pnpm --filter web build && pnpm --filter web start",
        url: "http://localhost:3000",
        // `next start` runs as production, which would mark cookies Secure;
        // this server is plain http://localhost (cookies.ts reads this).
        env: DEV_SERVER ? {} : { COOKIE_SECURE: "false" },
        // Reuse whatever is already running. Without this, a dev server the
        // developer started by hand would collide with the one Playwright
        // starts, and the port conflict reads as a test failure.
        // In dev mode only. In production mode a dev server already on :3000
        // would otherwise be reused silently — and the run would test dev mode
        // at dev-mode speed, which is what this mode exists to avoid. With
        // reuse off, Playwright stops with "port 3000 is already used" instead.
        reuseExistingServer: DEV_SERVER,
        // A production build takes a few minutes on a laptop; the dev server's
        // first compile is quicker. Either way this is paid once per run.
        timeout: DEV_SERVER ? 120_000 : 600_000,
        stdout: "pipe",
        stderr: "pipe",
      }
    : undefined,
  // Checks the stack is up before anything runs, so a missing service reports
  // itself once and by name rather than as 67 connection refusals.
  globalSetup: "./tests/browser/global-setup.ts",
  // Sequential. These sign in, create records and read them back; running them
  // in parallel against one database makes failures depend on interleaving,
  // and a flaky suite gets ignored.
  workers: 1,
  fullyParallel: false,
  // No retries. A test that passes on the second attempt is a test that found
  // something, and retrying hides it.
  retries: 0,
  timeout: 30_000,
  expect: { timeout: 10_000 },
  reporter: [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  use: {
    baseURL: process.env.WEB_URL ?? "http://localhost:3000",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "off",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});

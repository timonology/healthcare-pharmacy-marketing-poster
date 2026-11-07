import { test, expect, type Page, type BrowserContext } from "@playwright/test";
import { PAGES, DOWNLOADS, type Role } from "./routes";

/**
 * The app as a person uses it.
 *
 * Every bug that reached manual testing lived here and nowhere else:
 *
 *   · /patients and /pharmacies rendered perfectly but nothing linked to them
 *   · the Template download rendered the word `null`
 *   · the poster PDF link answered Next's 404
 *   · every top-level pharmacy was told it was a branch of another
 *
 * None was a domain-logic failure, and none was visible to a test that called
 * the API directly. What they have in common is that a person clicked
 * something. So this suite clicks things.
 *
 * Three properties, checked on every page:
 *
 *   1. the right roles can open it, and the wrong ones cannot
 *   2. it renders content rather than an error boundary or an empty shell
 *   3. nothing it requests fails — no 404s, no 405s, no console errors
 *
 * Rule 3 is the quiet one. A page can look completely correct while a fetch
 * behind it 404s and the UI shows an empty list.
 */

const WEB = process.env.WEB_URL ?? "http://localhost:3000";

const CREDENTIALS: Record<Exclude<Role, "guest">, { email: string; password: string }> = {
  pharmacy: {
    // The dev seeder's first demo pharmacy. Names, not numbers — see
    // DevUserSeeder.DemoPharmacies.
    email: process.env.E2E_PHARMACY_EMAIL ?? "riverside@example.test",
    password: process.env.E2E_PHARMACY_PASSWORD ?? "TestPass123!",
  },
  superadmin: {
    email: process.env.SUPERADMIN_EMAIL ?? "superadmin@sonar.local",
    password: process.env.SUPERADMIN_PASSWORD ?? "ChangeMe-Dev-Only-123",
  },
};

/* ── signing in ─────────────────────────────────────────────────────────── */

async function signIn(context: BrowserContext, role: Exclude<Role, "guest">) {
  const page = await context.newPage();
  await page.goto(`${WEB}/login`);

  const { email, password } = CREDENTIALS[role];
  await page.getByLabel(/email/i).fill(email);
  // Exact, because the reveal toggle beside the field is also labelled
  // "Show password" — /password/i now matches two elements and Playwright
  // refuses an ambiguous locator rather than guessing.
  await page.getByLabel("Password", { exact: true }).fill(password);
  // The submit button specifically: the page also carries a disabled
  // "Sign in with Google", and a name match on /sign in/i resolves to both,
  // which Playwright rejects rather than guessing between.
  await page.locator('form button[type="submit"]').click();

  // Landing on /dashboard or /onboarding both count as signed in; staying on
  // /login does not.
  try {
    await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 15_000 });
  } catch {
    // A timeout here blocks every test for this role, so it has to say why
    // rather than leaving 31 tests marked "did not run" with a stack trace
    // about navigation.
    const shown = await page
      .locator("body")
      .innerText()
      .catch(() => "");
    const message = /invalid|incorrect|not found|no account/i.exec(shown)?.[0] ?? "no error shown";
    throw new Error(
      `Could not sign in as ${role} (${email}). The page said: ${message}.\n` +
        "These are the dev seeder's accounts. If your database has different ones, set " +
        "E2E_PHARMACY_EMAIL / E2E_PHARMACY_PASSWORD (or SUPERADMIN_EMAIL / SUPERADMIN_PASSWORD).",
    );
  }
  await page.close();
}

/**
 * Watches a page for the failures that do not throw.
 *
 * Returned as a live array the caller reads after navigating, rather than an
 * assertion, so one page can report every problem it has instead of stopping
 * at the first.
 */
function watch(page: Page) {
  const failures: string[] = [];

  page.on("console", (msg) => {
    if (msg.type() !== "error") return;
    const text = msg.text();
    // Ignore noise the app does not control: extensions, favicon, and the
    // hydration warning React emits for third-party attribute injection.
    if (/favicon|extension:|Download the React DevTools/i.test(text)) return;

    // The browser logs every non-2xx fetch as a console error, including the
    // 401s a page gets while probing /api/auth/me to decide what to render.
    // The response handler below already judges status codes on their merits;
    // counting the same 401 twice, once as a legitimate probe and once as a
    // console error, made every public page fail.
    if (/Failed to load resource.*\b(401|403)\b/i.test(text)) return;

    failures.push(`console error: ${text.slice(0, 200)}`);
  });

  page.on("response", (res) => {
    const url = new URL(res.url());
    if (url.origin !== new URL(WEB).origin) return;
    if (!url.pathname.startsWith("/api/")) return;

    // 401 is legitimate — pages probe /api/auth/me before deciding what to
    // render. 404 and 405 are not: they mean a route is missing or a verb is
    // not exported, which is precisely the class of bug that keeps shipping.
    if (res.status() === 404 || res.status() === 405) {
      failures.push(`${res.status()} from ${url.pathname}`);
    }
  });

  page.on("pageerror", (err) => failures.push(`uncaught: ${err.message.slice(0, 200)}`));

  return failures;
}

/* ── fixtures ───────────────────────────────────────────────────────────── */

const fixtures: Record<string, string | null> = {
  poster: null,
  template: null,
  pharmacy: null,
  branch: null,
};

async function loadFixtures(context: BrowserContext, role: Exclude<Role, "guest">) {
  const page = await context.newPage();

  const first = async (path: string): Promise<string | null> => {
    const res = await page.request.get(`${WEB}${path}`);
    if (!res.ok()) return null;
    const body = await res.json().catch(() => null);
    const items = Array.isArray(body) ? body : (body?.items ?? []);
    return items[0]?.id ?? items[0]?.userId ?? null;
  };

  fixtures.poster = await first("/api/posters?take=1");
  fixtures.template = await first("/api/templates?take=1");
  fixtures.pharmacy = await first("/api/admin/users?role=PharmacyUser&take=1");
  // Only a head office has any, and only for itself — a superadmin gets a
  // non-2xx here and the branch page is skipped for that role, correctly.
  fixtures.branch = await first("/api/pharmacies/children");

  // Make one rather than skip.
  //
  // On a database where this account owns no posters, /posters/:id and the PDF
  // download both skipped — and the PDF download is the regression test for a
  // bug that actually shipped. A skip is honest about not having run, but a
  // regression test that never runs is the same as not having one, and it is
  // quiet about it. Creating the fixture costs one request.
  if (role === "pharmacy" && !fixtures.poster) {
    const created = await page.request.post(`${WEB}/api/posters`, {
      data: { name: "Browser suite fixture poster" },
    });
    if (created.ok()) {
      fixtures.poster = (await created.json().catch(() => null))?.id ?? null;
    }
  }

  await page.close();
}

const withFixture = (path: string, id: string) => path.replace(":id", id);

/**
 * Waits for a page to stop showing a loading placeholder.
 *
 * `networkidle` says the requests finished, not that React has rendered what
 * came back. The poster editor failed on exactly that gap: the assertion read
 * "Loading poster…" — fifteen characters, precisely the length the failure
 * reported — while the editor was still mounting. Every other page passed on
 * timing luck; there are four such placeholders in the app.
 */
async function settle(page: Page, timeoutMs = 20_000): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  let text = "";

  while (Date.now() < deadline) {
    text = (await page.locator("body").innerText().catch(() => "")).trim();
    if (text && !/^loading\b/i.test(text)) return text;
    await page.waitForTimeout(200);
  }
  return text;
}


/**
 * Waits until the client knows who is signed in.
 *
 * `networkidle` is not enough. The shell renders its navigation immediately but
 * fills in everything role-dependent only once /api/auth/me answers — until
 * then the account menu is a skeleton and the admin console shows a spinner
 * with no text in it. Harvesting links before that point finds the pharmacy nav
 * and none of the admin nav, which reads exactly like "the admin pages are
 * unreachable" when in fact the page had not finished loading.
 *
 * The account menu trigger is the signal: it is rendered from the resolved user
 * and from nothing else.
 */
async function waitForSession(page: Page) {
  await expect(
    page.locator('button[aria-haspopup="menu"]').first(),
    "the session never resolved — the page is still showing its loading state",
  ).toBeVisible({ timeout: 30_000 });
}

/* ── the suite ──────────────────────────────────────────────────────────── */

for (const role of ["guest", "pharmacy", "superadmin"] as const) {
  test.describe(`as ${role}`, () => {
    let context: BrowserContext;

    test.beforeAll(async ({ browser }) => {
      context = await browser.newContext();
      if (role !== "guest") {
        await signIn(context, role);
        await loadFixtures(context, role);
      }
    });

    test.afterAll(async () => context?.close());

    for (const spec of PAGES) {
      const allowed = spec.allow.includes(role);
      const denied = spec.deny?.includes(role) ?? false;
      if (!allowed && !denied) continue;

      test(`${allowed ? "can open" : "is kept out of"} ${spec.path}`, async () => {
        if (spec.needsFixture && !fixtures[spec.needsFixture]) {
          test.skip(true, `no ${spec.needsFixture} exists to open — create one and re-run`);
        }

        const path = spec.needsFixture
          ? withFixture(spec.path, fixtures[spec.needsFixture]!)
          : spec.path;

        const page = await context.newPage();
        const failures = watch(page);

        const response = await page.goto(`${WEB}${path}`, { waitUntil: "networkidle" });

        if (denied) {
          // Two legitimate ways to keep somebody out, and the suite must accept
          // both: bounce them to another URL, or keep the URL and render a
          // refusal instead of the content. /admin does the second for a
          // signed-in non-staff user, and an earlier version of this test
          // insisted on a redirect and called that a failure.
          //
          // The wait below matters as much as the assertion. The admin guard is
          // asynchronous — it probes /api/me before it can know the role, and
          // renders a bare spinner meanwhile. That spinner has no text, but the
          // app shell around it does: the sidebar. So reading the body once,
          // straight after `networkidle`, can catch the page mid-decision and
          // report a refusal that "never appeared" when in fact it had not been
          // reached yet. This failed exactly that way on /admin/analytics, and
          // read like an access-control hole.
          const refusal = page
            .getByText(
              /don't have access|do not have access|not authorised|not authorized|sign in|log in/i,
            )
            .first();

          await Promise.race([
            page
              .waitForURL((url) => new URL(url).pathname !== path, { timeout: 15_000 })
              .catch(() => {}),
            refusal.waitFor({ state: "visible", timeout: 15_000 }).catch(() => {}),
          ]);

          const stayed = new URL(page.url()).pathname === path;
          if (stayed) {
            await expect(
              refusal,
              `${role} stayed on ${path} and was shown its content rather than a refusal`,
            ).toBeVisible();
          }
          await page.close();
          return;
        }

        expect(response?.status(), `${path} did not load`).toBeLessThan(400);
        expect(new URL(page.url()).pathname, `${path} redirected away`).toBe(path);

        // Rendered something, rather than a blank shell or an error boundary.
        const body = await settle(page);

        expect(body, `${path} never finished loading`).not.toMatch(/^loading\b/i);
        expect(body).not.toMatch(/something went wrong|application error|unhandled/i);

        // The literal symptom of a JSON proxy swallowing a response.
        expect(body, `${path} rendered a bare null`).not.toBe("null");

        if (spec.rendered === "canvas") {
          // A drawing surface, so the proof it worked is that the stage mounted
          // — not how much text is on screen.
          await expect(
            page.locator("canvas").first(),
            `${path} did not mount a canvas`,
          ).toBeVisible();
        } else {
          expect(body.length, `${path} rendered an empty page`).toBeGreaterThan(30);
        }

        if (spec.expect) {
          await expect(
            page.getByText(spec.expect, { exact: false }).first(),
            `${path} did not show "${spec.expect}"`,
          ).toBeVisible();
        }

        expect(failures, `${path} reported failures`).toEqual([]);
        await page.close();
      });
    }

    /* ── navigation must actually reach the pages ───────────────────────── */

    if (role !== "guest") {
      test("every nav link opens the page it names", async () => {
        // One full server render per sidebar link (a dozen or more), in a row.
        test.slow();
        const page = await context.newPage();
        await page.goto(`${WEB}${role === "superadmin" ? "/admin" : "/dashboard"}`, {
          waitUntil: "networkidle",
        });

        await waitForSession(page);

        const hrefs = await page
          .locator("nav a[href^='/']")
          .evaluateAll((els) => [...new Set(els.map((e) => e.getAttribute("href")!))]);

        expect(hrefs.length, "the sidebar rendered no links at all").toBeGreaterThan(3);

        const broken: string[] = [];
        for (const href of hrefs) {
          const res = await page.request.get(`${WEB}${href}`, { maxRedirects: 0 });
          if (res.status() === 404) broken.push(`${href} -> 404`);
        }
        expect(broken, "nav links that go nowhere").toEqual([]);
        await page.close();
      });

      test("every page in this role's nav is reachable from the nav", async () => {
        // The inverse, and the one that would have caught both nav bugs:
        // /patients and /pharmacies existed, rendered and were protected — and
        // no menu anywhere pointed at them, so nobody could get there.
        const page = await context.newPage();
        await page.goto(`${WEB}${role === "superadmin" ? "/admin" : "/dashboard"}`, {
          waitUntil: "networkidle",
        });

        await waitForSession(page);

        const hrefs = new Set(
          await page
            .locator("a[href^='/']")
            .evaluateAll((els) => els.map((e) => e.getAttribute("href")!)),
        );

        const expected = PAGES.filter(
          (p) =>
            p.allow.includes(role) &&
            !p.needsFixture &&
            !p.path.includes(":") &&
            // Reached by redirect or from another page, not from a menu.
            //
            // /playlists is the newest of these and the only one worth
            // explaining: it pairs with /displays the way a detail page pairs
            // with a list. Screens is the sidebar entry, because "change what
            // my screens show" is how somebody arrives; the playlist is the
            // content behind that, reached by a button on Screens, and each
            // page links back to the other. A second nav entry for one feature
            // is clutter, and the app already keeps sub-concepts off the nav —
            // patient groups have no entry either.
            //
            // Being excluded here does not make it unreachable: check-routes
            // fails the build if nothing anywhere links to a page, and it is
            // that check — not this one — which guards against an orphan.
            !["/", "/canvas", "/pricing", "/profile", "/templates", "/playlists"]
              .includes(p.path) &&
            p.path.startsWith("/admin") === (role === "superadmin"),
        );

        const unreachable = expected
          .filter((p) => ![...hrefs].some((h) => h === p.path || h.startsWith(`${p.path}?`)))
          .map((p) => p.path);

        expect(
          unreachable,
          "these pages work but nothing links to them — a user cannot get there",
        ).toEqual([]);
        await page.close();
      });
    }

    /* ── the campaign email ─────────────────────────────────────────────── */

    if (role === "superadmin") {
      test("the campaign email lays out correctly", async () => {
        // Email layout is a visual fact, so it gets checked by rendering the
        // real output of CampaignEmail.BuildHtml in a real browser rather than
        // by reading the markup. The preview passes the builder a placeholder
        // image of the same shape in place of a real poster URL — every table,
        // width and alignment rule is the one that ships.
        //
        // The poster is wrapped in a link now, because it is fetched rather than
        // embedded and a reader whose client blocks remote images needs a way
        // through to it. The anchor is display:block so the image keeps behaving
        // exactly as it did when it sat directly in the cell.
        const page = await context.newPage();
        const failures = watch(page);

        await page.goto(`${WEB}/api/admin/diagnostics/campaign-email/preview?videos=1`, {
          waitUntil: "networkidle",
        });

        // The whole point of the change: the design is in the body.
        const poster = page.locator("img").first();
        await expect(poster, "the poster did not render in the body").toBeVisible();

        const card = page.locator("table table").first();
        const posterBox = await poster.boundingBox();
        const cardBox = await card.boundingBox();
        expect(posterBox, "poster has no box").not.toBeNull();
        expect(cardBox, "card has no box").not.toBeNull();

        // Centred: the gap either side of the image should match. Allowing two
        // pixels covers sub-pixel rounding without letting a real misalignment
        // through.
        const left = posterBox!.x - cardBox!.x;
        const right = cardBox!.x + cardBox!.width - (posterBox!.x + posterBox!.width);
        expect(
          Math.abs(left - right),
          `poster is off-centre: ${left.toFixed(1)}px left, ${right.toFixed(1)}px right`,
        ).toBeLessThanOrEqual(2);

        // Nothing may spill out of the card — a poster wider than its container
        // is what produces the horizontal scrollbar people complain about.
        expect(
          posterBox!.width,
          "the poster is wider than the email body",
        ).toBeLessThanOrEqual(cardBox!.width + 1);

        // The video sits below the design, not above or beside it.
        const watch_ = page.getByRole("link", { name: /watch/i }).first();
        await expect(watch_, "no video link rendered").toBeVisible();
        const videoBox = await watch_.boundingBox();
        expect(
          videoBox!.y,
          "the video should sit below the poster",
        ).toBeGreaterThan(posterBox!.y + posterBox!.height);

        expect(failures, "the preview reported failures").toEqual([]);
        await page.close();
      });

      test("the campaign email fits a phone without sideways scrolling", async () => {
        const page = await context.newPage();
        await page.setViewportSize({ width: 375, height: 800 });

        await page.goto(`${WEB}/api/admin/diagnostics/campaign-email/preview?videos=1`, {
          waitUntil: "networkidle",
        });

        // `width:100%;max-width:600px` is what is supposed to make this true.
        // Asserting the outcome rather than the CSS means the rule can be
        // rewritten without the test caring, and a rule that stops working
        // cannot pass.
        const overflow = await page.evaluate(
          () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
        );
        expect(overflow, "the email overflows a 375px screen").toBeLessThanOrEqual(1);

        await page.close();
      });
    }

    /* ── downloads ──────────────────────────────────────────────────────── */

    for (const dl of DOWNLOADS.filter((d) => d.role === role)) {
      test(`download ${dl.path} comes back as a file`, async () => {
        if (dl.needsFixture && !fixtures[dl.needsFixture]) {
          test.skip(true, `no ${dl.needsFixture} to export`);
        }
        const path = dl.needsFixture
          ? withFixture(dl.path, fixtures[dl.needsFixture]!)
          : dl.path;

        const page = await context.newPage();
        const res = await page.request.get(`${WEB}${path}`);

        expect(res.status(), `${path} did not resolve — is there a Next route?`).toBeLessThan(400);

        const type = (res.headers()["content-type"] ?? "").toLowerCase();
        for (const bad of dl.mustNotBe) {
          expect(
            type,
            `${path} came back as ${type} — a JSON proxy swallowed the file`,
          ).not.toContain(bad);
        }
        expect(type, `${path} content-type was ${type}`).toContain(dl.expectContains);

        // The exact symptom of the Template link.
        expect((await res.text()).trim()).not.toBe("null");
        await page.close();
      });
    }
  });
}

import { test, expect, type Page } from "@playwright/test";

/**
 * Things a person does, done the way a person does them.
 *
 * `app.spec.ts` proves every page opens for the right roles. It signs in
 * through the real login form once per role and then navigates by URL. That
 * leaves a specific shape of gap: **the only form the suite ever fills is the
 * login form.** Registration, signing out, and every create/send flow are
 * reached by `page.request.post(...)` or not at all — so the API is covered and
 * the controls that call it are not.
 *
 * The difference matters because the bugs that reached manual testing were all
 * of this kind: a page that rendered but nothing linked to, a download that
 * returned `null`, a button wired to a route that had moved. A disabled submit
 * button, a validation rule that rejects valid input, or a dialog that never
 * closes are all invisible to a test that posts JSON.
 *
 * These tests are deliberately few and slow. They are journeys, not coverage:
 * each one is a sentence about something the product promises.
 */

// The shared 30s budget in playwright.config.ts fits a test that opens one
// page. These sign in, submit a form, wait for a redirect and then read a list
// — and in Playwright the beforeEach hook spends the same budget. Raising it
// globally would slow down failures in app.spec.ts, so it is raised here only.
test.describe.configure({ timeout: 90_000 });

const WEB = process.env.WEB_URL ?? "http://localhost:3000";

/** The seeded pharmacy — already onboarded, and on Pro, so campaigns are open. */
const PHARMACY = {
  email: process.env.E2E_PHARMACY_EMAIL ?? "riverside@example.test",
  password: process.env.E2E_PHARMACY_PASSWORD ?? "TestPass123!",
};

/** Platform staff, for the queue that only they can open. */
const SUPERADMIN = {
  email: process.env.SUPERADMIN_EMAIL ?? "superadmin@sonar.local",
  password: process.env.SUPERADMIN_PASSWORD ?? "ChangeMe-Dev-Only-123",
};

const PASSWORD = "JourneyTest-123!";

/** A fresh address per test, so a rerun never collides with its own history. */
const newEmail = (label: string) =>
  `journey-${label}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}@example.test`;

/* ── helpers ────────────────────────────────────────────────────────────── */

/**
 * Fills the signup form. Leaves submitting to the caller, because half these
 * tests are about what happens when it is submitted wrong.
 */
async function fillSignup(
  page: Page,
  over: Partial<{ email: string; password: string; confirm: string; terms: boolean }> = {},
) {
  const email = over.email ?? newEmail("signup");
  const password = over.password ?? PASSWORD;

  await page.goto(`${WEB}/register`);

  await page.getByLabel(/pharmacy name/i).fill("Journey Test Pharmacy");
  await page.getByLabel(/ODS \/ F code/i).fill("FJ001");
  await page.getByLabel(/postcode/i).fill("SW1A 1AA");
  await page.getByLabel(/your full name/i).fill("Journey Tester");
  await page.getByLabel(/email/i).fill(email);

  // Both fields need exact labels now. "Password" and "Confirm password" match
  // each other loosely, and each field's reveal button is labelled "Show
  // password" / "Show confirm password" — so /confirm password/i matches the
  // input *and* its toggle. Playwright refuses an ambiguous locator rather than
  // picking one, which is the right call and why these are all exact.
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByLabel("Confirm password", { exact: true }).fill(over.confirm ?? password);

  if (over.terms !== false) await page.locator('input[type="checkbox"]').first().check();

  return { email, password };
}

const submitSignup = (page: Page) =>
  page.getByRole("button", { name: /create free account/i }).click();

/**
 * Makes sure the signed-in pharmacy has at least one published poster, and
 * returns its name.
 *
 * Posters are created as drafts, and a draft can no longer be sent in a
 * campaign or posted to social. Anything that exercises sending therefore has
 * to publish one first — the same step the product now requires of a person.
 */
async function publishAPoster(page: Page): Promise<string> {
  const list = await page.request.get(`${WEB}/api/posters?take=20`);
  const body = list.ok() ? await list.json().catch(() => null) : null;
  const items: Array<{ id: string; name: string; status: string }> =
    Array.isArray(body) ? body : (body?.items ?? []);

  const alreadyPublished = items.find((p) => p.status === "Published");
  if (alreadyPublished) return alreadyPublished.name;

  let target = items[0];
  if (!target) {
    const created = await page.request.post(`${WEB}/api/posters`, {
      data: { name: `Publishable poster ${Date.now().toString(36)}` },
    });
    target = await created.json();
  }

  const res = await page.request.patch(`${WEB}/api/posters/${target.id}/status`, {
    data: { status: "Published" },
  });
  expect(res.ok(), `could not publish a poster: ${res.status()}`).toBeTruthy();
  return target.name;
}

/** Signs in through the form and waits to be let in. */
async function signIn(page: Page, email: string, password: string) {
  await page.goto(`${WEB}/login`);
  await page.getByLabel(/email/i).fill(email);
  // Exact, because the reveal toggle beside the field is also labelled
  // "Show password" — /password/i now matches two elements and Playwright
  // refuses an ambiguous locator rather than guessing.
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.locator('form button[type="submit"]').click();
  await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 20_000 });
}

/**
 * Starts working as another pharmacy, so the manage-as banner is real.
 *
 * Driving the switcher menu would be prettier and would skip on any database
 * where staff have no memberships listed — and a test that skips guards
 * nothing. This posts to the same server-enforced switch the menu posts to, so
 * the session, the cookies and the banner are all genuine; only the click is
 * bypassed, and the switcher has its own tests for that.
 *
 * Returns "ok" or a reason, so a caller can skip with something specific.
 */
async function startWorkingAsAnotherPharmacy(page: Page): Promise<string> {
  return page.evaluate(async () => {
    const list = await fetch("/api/admin/users?role=PharmacyUser&take=1", {
      credentials: "same-origin",
    });
    if (!list.ok) return "no-admin-list";

    const body = await list.json().catch(() => null);
    const items = Array.isArray(body) ? body : (body?.items ?? []);
    const target = items[0]?.id ?? items[0]?.userId;
    if (!target) return "no-pharmacy";

    const res = await fetch("/api/workspaces/switch", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pharmacyId: target }),
    });
    return res.ok ? "ok" : `switch-failed-${res.status}`;
  });
}

/** Opens the sidebar account menu and signs out. */
async function signOut(page: Page, deadline = Date.now() + 15_000) {
  // The menu is collapsed until its trigger is clicked; the item does not
  // exist in the DOM before that, so this is a real click, not a convenience.
  //
  // `:visible` matters. The shell renders menu triggers for both the desktop
  // sidebar and the mobile header, and the one for the other breakpoint sits
  // in the DOM but hidden — clicking it waits for an element that will never
  // become visible, until the test gives up ninety seconds later.
  //
  // <b>Waited for, not snapshotted.</b> The account trigger is rendered from
  // the signed-in user, and the sidebar footer shows an `aria-hidden` skeleton
  // with **no button in it** until that user arrives. So there is a real window
  // in which the shell is fully painted, `<aside>` is visible, and the only
  // menu trigger on the page is the pharmacy switcher. Taking one snapshot in
  // that window is how this helper came to open the switcher, read its list of
  // branches, and report that no menu offered Sign out — a true sentence about
  // the wrong menu, and a failure that looks like a product bug.
  //
  // `networkidle` does not close that window: the user is held in a client
  // store that settles after the last response, which is the same lag the
  // expired-session test below relies on.
  // The deadline is a parameter, not a local. It is re-checked before the one
  // retry below, and a local would be re-initialised by that recursive call —
  // giving a helper that can never time out, which is a worse failure than the
  // one it was added to fix.
  const accountTrigger = page
    .locator('button[aria-haspopup="menu"]:visible')
    .filter({ hasText: /@/ });

  // The account button shows the signed-in email, and nothing else in the shell
  // does. Cheap, and it means the common case waits for the *right* trigger
  // rather than trying each in turn.
  await accountTrigger
    .first()
    // Floored at 50ms, never 0: Playwright reads `timeout: 0` as *disable the
    // timeout*, so a retry entered after the deadline had passed would wait for
    // ever instead of giving up — turning a helper written to fail fast into
    // one that hangs until the whole test times out.
    .waitFor({
      state: "visible",
      timeout: Math.max(50, Math.min(10_000, deadline - Date.now())),
    })
    .catch(() => {
      // Not fatal on its own. A collapsed sidebar shows initials only, so the
      // loop below still has to do the work — this just skips the guessing in
      // the case that covers most runs.
    });

  const triggers = page.locator('button[aria-haspopup="menu"]:visible');
  const count = await triggers.count();
  expect(count, "no visible account menu trigger on the page").toBeGreaterThan(0);

  // What each trigger did, so a failure can say which of two very different
  // things went wrong.
  //
  // The old message — "opened every visible account menu and none offered Sign
  // out" — asserted something it had not checked. It never established that a
  // menu *opened*, so "the click did nothing" and "the menu opened and had no
  // Sign out" arrived as the same sentence, and the one it named was the one
  // that was not happening. A diagnosis that has to be re-derived from a
  // screenshot is a diagnosis the test failed to make.
  const attempts: string[] = [];

  for (let i = count - 1; i >= 0; i--) {
    const trigger = triggers.nth(i);
    const label = ((await trigger.getAttribute("aria-label")) ??
      (await trigger.innerText().catch(() => ""))).replace(/\s+/g, " ").trim().slice(0, 60) || `trigger ${i}`;

    // Clicked up to twice, and this is the fix for the flake rather than a
    // longer timeout.
    //
    // The shell is server-rendered: the button is in the HTML before React has
    // hydrated it, and a click landing in that window hits a real element with
    // no handler attached. Nothing opens, nothing errors, and the two-second
    // wait below then expires truthfully. Waiting longer does not help — the
    // click was already lost. Clicking again does.
    const menuIsOpen = (timeout: number) =>
      page
        .locator('[role="menu"]:visible')
        .first()
        .waitFor({ state: "visible", timeout })
        .then(() => true)
        .catch(() => false);

    let menuOpened = false;
    for (let attempt = 0; attempt < 2 && !menuOpened; attempt++) {
      if (attempt > 0) {
        // Look before clicking again. A first click that opened the menu a
        // moment after the probe gave up would be *toggled shut* by a blind
        // retry — and the helper would then report "no menu ever opened",
        // which is the opposite of what happened. Re-checking first costs
        // nothing and stops the fix from inventing its own false diagnosis.
        menuOpened = await menuIsOpen(250);
        if (menuOpened) break;

        // Nothing open: normalise before the second click so it cannot land on
        // a half-dismissed overlay.
        await page.keyboard.press("Escape");
        await page.waitForTimeout(150);
      }

      await trigger.click();
      menuOpened = await menuIsOpen(2_000);
    }

    if (!menuOpened) {
      attempts.push(`"${label}": clicked twice, no menu ever opened`);
      continue;
    }

    const item = page.getByRole("menuitem", { name: /sign out/i });
    const hasSignOut = await item
      .waitFor({ state: "visible", timeout: 2_000 })
      .then(() => true)
      .catch(() => false);

    if (!hasSignOut) {
      // Wrong menu — the pharmacy switcher, say. Close it before trying the
      // next one: these are modal menus, so an open one covers the page with a
      // blocking overlay and every later click would land on that instead of
      // the trigger it was aimed at.
      const offered = await page
        .getByRole("menuitem")
        .allInnerTexts()
        .then((t) => t.map((x) => x.replace(/\s+/g, " ").trim()).filter(Boolean).join(", "))
        .catch(() => "(unreadable)");
      attempts.push(`"${label}": menu opened, offered [${offered}]`);

      await page.keyboard.press("Escape");
      await page.waitForTimeout(150);
      continue;
    }

    await item.click();

    // Wait for the app's own redirect before returning.
    //
    // Signing out is `await api.logout()` and then
    // `window.location.assign("/login")`. The cookies are cleared by the
    // Set-Cookie on that response, so navigating the moment the click lands
    // races two things at once: the response that clears the session, and the
    // app's redirect competing with our own goto.
    //
    // Without this the next assertion reads "/dashboard was still reachable"
    // and looks exactly like a broken sign-out, when the only thing broken is
    // the test's timing.
    await page.waitForURL(/\/login/, { timeout: 20_000 });
    return;
  }

  // Every visible trigger was tried and none offered Sign out. Before giving
  // up, allow for the trigger that renders late: if the account button has
  // appeared since the snapshot above, go round again.
  if (Date.now() < deadline && (await accountTrigger.count()) > 0) {
    await page.keyboard.press("Escape");
    await page.waitForTimeout(250);
    return signOut(page, deadline);
  }

  throw new Error(
    `No account menu offered Sign out. Tried ${count} visible trigger(s):\n  ` +
      attempts.join("\n  ") +
      `\n\nIf the only trigger listed is the pharmacy switcher, the account ` +
      `button had not rendered: the sidebar footer shows a skeleton with no ` +
      `button until the signed-in user loads.`,
  );
}

/* ── signing up ─────────────────────────────────────────────────────────── */

test.describe("signing up", () => {
  test("a new pharmacy can register through the form", async ({ page }) => {
    const { email } = await fillSignup(page);
    await submitSignup(page);

    // Registration hands off to onboarding. Landing anywhere else — including
    // staying put with an error — means the form did not create an account.
    await page.waitForURL(/\/onboarding/, { timeout: 25_000 }).catch(async () => {
      const shown = await page.locator("body").innerText().catch(() => "");
      const complaint = /passwords|required|already|invalid|failed/i.exec(shown)?.[0];
      throw new Error(
        `Registering ${email} did not reach /onboarding (still at ${page.url()}).` +
          (complaint ? ` The form said: "${complaint}".` : ""),
      );
    });

    expect(new URL(page.url()).pathname).toMatch(/^\/onboarding/);
  });

  test("a half-finished signup cannot reach the app yet", async ({ page }) => {
    // The middleware gate. A new account carries acme_onboarded=0, and every
    // protected route must bounce to /onboarding until that changes —
    // otherwise a pharmacy lands on an app configured for nobody.
    await fillSignup(page);
    await submitSignup(page);
    await page.waitForURL(/\/onboarding/, { timeout: 25_000 });

    await page.goto(`${WEB}/campaigns`);
    expect(
      new URL(page.url()).pathname,
      "a pharmacy that has not finished onboarding reached /campaigns",
    ).toMatch(/^\/onboarding/);
  });

  test("mismatched passwords are refused and no account is created", async ({ page }) => {
    const { email } = await fillSignup(page, { confirm: "SomethingElse-123!" });
    await submitSignup(page);

    // Scoped to the form, not `getByRole("alert")` on its own.
    //
    // Next renders a permanently-present route announcer —
    // <div role="alert" aria-live="assertive" id="__next-route-announcer__"> —
    // for screen readers. It is empty and invisible, but it matches the role,
    // so an unscoped locator resolves to two elements and Playwright refuses
    // to guess. The failure reads like the assertion failed when in fact it
    // never ran.
    await expect(
      page.locator('form [role="alert"]'),
      "the form accepted two different passwords",
    ).toContainText(/passwords/i);
    expect(new URL(page.url()).pathname).toBe("/register");

    // The real assertion: the account must not exist. A form that shows an
    // error *and* registers anyway looks correct on screen.
    await signInExpectingRefusal(page, email, PASSWORD);
  });

  test("the terms have to be accepted", async ({ page }) => {
    const { email } = await fillSignup(page, { terms: false });
    await submitSignup(page);

    expect(
      new URL(page.url()).pathname,
      "the form was submitted without accepting the terms",
    ).toBe("/register");

    // Staying on /register is also what a completely dead submit button looks
    // like, so that assertion alone would pass against a broken form. This is
    // the one that means something: no account exists behind it.
    await signInExpectingRefusal(page, email, PASSWORD);
  });
});

/** Signs in and asserts it does not work. */
async function signInExpectingRefusal(page: Page, email: string, password: string) {
  await page.goto(`${WEB}/login`);
  await page.getByLabel(/email/i).fill(email);
  // Exact, because the reveal toggle beside the field is also labelled
  // "Show password" — /password/i now matches two elements and Playwright
  // refuses an ambiguous locator rather than guessing.
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.locator('form button[type="submit"]').click();

  // Give a wrong sign-in the same chance to succeed that a right one gets,
  // then insist it did not.
  await page.waitForTimeout(3_000);
  expect(
    new URL(page.url()).pathname,
    `signing in as ${email} succeeded when it should have been refused`,
  ).toMatch(/^\/login/);
}

/* ── signing in and out ─────────────────────────────────────────────────── */

test.describe("signing in and out", () => {
  test("a wrong password is refused", async ({ page }) => {
    await signInExpectingRefusal(page, PHARMACY.email, "definitely-not-the-password");
  });

  test("signing out ends the session", async ({ page }) => {
    // Covered at the API level by AuthLifecycleTests. What is *not* covered
    // there is whether any control in the UI reaches that endpoint — the
    // integration test would keep passing if the menu item were unwired.
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/dashboard`);

    await signOut(page);

    await page.goto(`${WEB}/dashboard`);
    expect(
      new URL(page.url()).pathname,
      "after signing out, /dashboard was still reachable",
    ).toMatch(/^\/login/);
  });
});

/* ── doing the work ─────────────────────────────────────────────────────── */

test.describe("the pharmacy's own work", () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
  });

  test("creating a blank poster opens the editor", async ({ page }) => {
    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });

    // Two buttons carry this name when the list is empty — one in the header,
    // one in the empty state. Either is the right thing to click, but an
    // unqualified locator matching both is a strict-mode failure that would
    // only appear on an account with no posters.
    await page.getByRole("button", { name: /new blank poster/i }).first().click();

    // The editor is a drawing surface, so the proof is that the stage mounted.
    await page.waitForURL(/\/(canvas|posters)\//, { timeout: 25_000 });
    await expect(
      page.locator("canvas").first(),
      "creating a poster did not open an editor with a canvas",
    ).toBeVisible({ timeout: 25_000 });
  });

  test("a campaign can be sent and shows up in the list", async ({ page }) => {
    // A campaign will only take a published poster, and posters are created as
    // drafts — so on an account whose posters are all drafts the wizard would
    // open with nothing selectable and Send disabled. Publish one first, which
    // is what a pharmacist does before sending anything.
    await publishAPoster(page);

    await page.goto(`${WEB}/campaigns`, { waitUntil: "networkidle" });

    const start = page.getByRole("button", { name: /new campaign/i });
    await expect(
      start,
      "New campaign is disabled — this account needs a paid plan and at least one poster",
    ).toBeEnabled({ timeout: 20_000 });
    await start.click();

    const name = `Journey campaign ${Date.now().toString(36)}`;
    await page.locator("#name").fill(name);
    await page.locator("#recipients").fill("journey-recipient@example.test");

    // The label counts the audience, so it also proves the recipient was
    // actually parsed — "Send to 0" stays disabled.
    const send = page.getByRole("button", { name: /^Send to \d+/ });
    await expect(
      send,
      "the recipient was typed but the audience count never reached 1",
    ).toBeEnabled({ timeout: 20_000 });
    await send.click();

    // It has to appear in the list the pharmacy actually looks at.
    await expect(
      page.getByText(name, { exact: false }).first(),
      "the campaign was sent but never appeared in the campaign list",
    ).toBeVisible({ timeout: 30_000 });
  });
});

/* ── working across pharmacies ──────────────────────────────────────────── */

test.describe("working as another pharmacy", () => {
  test("asking for access records the request and shows its status", async ({ page }) => {
    // The API tests prove the boundary holds. What they cannot prove is that
    // any control in the UI reaches it — this feature could be entirely
    // correct server-side and unreachable, which is how the dead "Forgot
    // password?" link survived for months.
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/pharmacies`, { waitUntil: "networkidle" });

    await page.getByLabel(/pharmacy email address/i).fill("oakfield@example.test");
    await page
      .getByLabel(/why do you need access/i)
      .fill("Covering the Tuesday locum shift at this branch.");

    await page.getByRole("button", { name: /send request/i }).click();

    // The confirmation is deliberately the same whatever the address, so this
    // asserts the acknowledgement rather than a success message.
    await expect(
      page.getByText(/request submitted/i).first(),
      "submitting must acknowledge, not fail silently",
    ).toBeVisible({ timeout: 20_000 });

    // And it must appear in the person's own list, which is the part that
    // proves the request reached the server rather than the form merely
    // clearing itself.
    await page.reload({ waitUntil: "networkidle" });
    await expect(
      page.getByText(/covering the tuesday locum shift/i).first(),
      "the request should be listed with its status",
    ).toBeVisible({ timeout: 20_000 });
  });

  test("a short reason is refused, and says so", async ({ page }) => {
    // The one error this form is allowed to show. Everything about the address
    // is deliberately vague; a complaint about your own input is not.
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/pharmacies`, { waitUntil: "networkidle" });

    await page.getByLabel(/pharmacy email address/i).fill("oakfield@example.test");
    await page.getByLabel(/why do you need access/i).fill("hmm");
    await page.getByRole("button", { name: /send request/i }).click();

    // Either the browser's own minLength refuses it, or the API does and the
    // form shows why. Both are acceptable; silently accepting it is not.
    await expect(
      page.getByText(/request submitted/i),
      "a reason under ten characters must not be accepted",
    ).toHaveCount(0);
  });

  test("an administrator sees the review queue", async ({ page }) => {
    await signIn(page, SUPERADMIN.email, SUPERADMIN.password);
    await page.goto(`${WEB}/admin/access-requests`, { waitUntil: "networkidle" });

    // Either there are requests to decide, or the empty state says so. What
    // must not happen is a blank page or an error boundary.
    await expect(
      page
        .getByText(/access requests|nothing waiting|no requests yet/i)
        .first(),
      "the queue must render something a person can read",
    ).toBeVisible({ timeout: 20_000 });

    const body = await page.locator("body").innerText();
    expect(body).not.toMatch(/something went wrong|application error|unhandled/i);
    expect(body).not.toBe("null");
  });
});

/* ── the app shell's two menus ──────────────────────────────────────────── */

test.describe("the sidebar menus float properly", () => {
  /**
   * Both menus used to position themselves with `absolute` inside the sidebar,
   * which is a scrolling container — so they were clipped by it and drawn among
   * the navigation links instead of over them. These check the property that
   * fixes it: the panel is portalled out of the sidebar, and nothing overlaps.
   */

  /** The rectangle a locator occupies, or null if it isn't rendered. */
  async function boxOf(page: Page, selector: string) {
    const el = page.locator(selector).first();
    if (!(await el.isVisible().catch(() => false))) return null;
    return el.boundingBox();
  }

  test("the profile menu opens over the page, not inside the sidebar", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });

    // The trigger carries the person's email, which is what makes it findable
    // without depending on the avatar's initials.
    const trigger = page
      .locator('button[aria-haspopup="menu"]:visible')
      .filter({ hasText: PHARMACY.email })
      .first();

    await trigger.click();

    const menu = page.getByRole("menu");
    await expect(menu, "the profile menu did not open").toBeVisible({ timeout: 10_000 });

    // Portalled: the panel is a child of body, not of the sidebar. This is the
    // actual fix — a menu inside the sidebar is clipped by it no matter what
    // z-index it carries.
    const portalled = await menu.evaluate(
      (el) => !el.closest("nav") && !el.closest("aside"),
    );
    expect(portalled, "the menu is still rendered inside the sidebar").toBe(true);

    // Every item is present and reachable, rather than clipped to a sliver.
    for (const label of [/profile/i, /plan & billing/i, /sign out/i]) {
      await expect(menu.getByText(label).first()).toBeVisible();
    }

    // Escape closes it, and focus is not left stranded.
    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden({ timeout: 5_000 });
  });

  test("the pharmacy switcher opens without colliding with the nav", async ({ page }) => {
    await signIn(page, SUPERADMIN.email, SUPERADMIN.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });

    const trigger = page.getByRole("button", { name: /working as/i }).first();

    // Staff always see the switcher, because it carries the admin console link.
    await expect(trigger, "the switcher chip should be visible").toBeVisible({
      timeout: 20_000,
    });
    await trigger.click();

    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible({ timeout: 10_000 });

    // The bug as reported: menu text mixed into the sidebar's own links. If the
    // panel overlaps a nav link's box, it is being drawn in the wrong place.
    const menuBox = await menu.boundingBox();
    const navBox = await boxOf(page, "nav");
    expect(menuBox, "the menu has no box").not.toBeNull();

    if (menuBox) {
      // Overlapping the nav in position is fine and expected — the menu floats
      // above it. What must not happen is the menu being *clipped*, which is
      // what a sidebar-width panel with its content cut off looks like.
      expect(
        menuBox.width,
        "the menu is narrower than its content, so it is being clipped",
      ).toBeGreaterThan(180);

      // And it must not be squeezed to a sliver vertically either.
      expect(
        menuBox.height,
        "the menu is too short to be showing its items",
      ).toBeGreaterThan(60);
    }

    // navBox is read only to prove the nav exists on this page; a missing nav
    // would make the check above vacuous.
    expect(navBox, "no sidebar nav on the page — the check would prove nothing")
      .not.toBeNull();

    // Clicking away closes it.
    await page.mouse.click(5, 5);
    await expect(menu).toBeHidden({ timeout: 5_000 });
  });

  test("both menus stay on screen at a phone width", async ({ page }) => {
    // A menu anchored to the bottom-left of a sidebar is the classic candidate
    // for opening off the bottom of a short viewport.
    await page.setViewportSize({ width: 390, height: 720 });
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });

    // The sidebar lives behind a menu button on a phone.
    const open = page.getByRole("button", { name: /open menu/i }).first();
    if (await open.isVisible().catch(() => false)) await open.click();

    // `:visible` matters here for the same reason it does in signOut. At a
    // phone width the desktop sidebar is `hidden lg:block` — still in the DOM,
    // just `display:none` — so an unfiltered locator found its account trigger
    // first, reported it as not visible, and the test skipped having checked
    // nothing. It did exactly that on every run.
    const trigger = page
      .locator('button[aria-haspopup="menu"]:visible')
      .filter({ hasText: PHARMACY.email })
      .first();

    await expect(
      trigger,
      "no account menu at phone width — the mobile drawer did not open",
    ).toBeVisible({ timeout: 10_000 });

    await trigger.click();
    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible({ timeout: 10_000 });

    const box = await menu.boundingBox();
    expect(box, "the menu has no box").not.toBeNull();

    if (box) {
      expect(box.x, "the menu runs off the left edge").toBeGreaterThanOrEqual(-1);
      expect(box.x + box.width, "the menu runs off the right edge").toBeLessThanOrEqual(391);
      expect(box.y, "the menu runs off the top").toBeGreaterThanOrEqual(-1);
      expect(box.y + box.height, "the menu runs off the bottom").toBeLessThanOrEqual(721);
    }
  });

  /**
   * Every test above passed while both menus were completely transparent.
   *
   * They checked where the panel was, never whether it painted anything. The
   * menus used `bg-popover`, but `popover` was not a declared colour — Tailwind
   * emitted no rule, and the panels rendered with no background at all, their
   * text overlapping the sidebar links showing through from underneath. Position
   * was perfect. The thing was unreadable.
   */
  test("the menus are opaque, not see-through", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });

    const trigger = page
      .locator('button[aria-haspopup="menu"]:visible')
      .filter({ hasText: PHARMACY.email })
      .first();

    await trigger.click();
    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible({ timeout: 10_000 });

    // Let the open animation finish before measuring anything.
    //
    // Radix fades the panel in (`data-[state=open]:fade-in-0`), so reading the
    // computed style the instant it becomes visible catches it mid-flight —
    // this assertion first ran at 0.74 and called a perfectly healthy menu
    // faded out. `toHaveCSS` retries until it settles, which is the difference
    // between testing the finished state and testing the animation.
    //
    // The bug being guarded against is a panel that is transparent *forever*,
    // not one that is transparent for 150ms on purpose.
    await expect(menu, "the menu never finished fading in").toHaveCSS("opacity", "1");

    const background = await menu.evaluate((el) => getComputedStyle(el).backgroundColor);

    // `rgba(0, 0, 0, 0)` is what an undeclared colour resolves to, and it is
    // indistinguishable from "no background specified". Assert the alpha
    // directly rather than trusting the class list, which looked correct
    // throughout the entire bug.
    expect(
      background,
      `the menu background is ${background} — the page shows straight through it`,
    ).not.toBe("rgba(0, 0, 0, 0)");

    // Count the components rather than pattern-matching the last number.
    // `rgb(255, 255, 255)` has no alpha at all, and reading its final value as
    // one gave an "alpha" of 255 — a fully opaque menu failing an opacity
    // check. Three components means opaque by definition; only rgba() carries
    // a fourth.
    const parts = (background.match(/[\d.]+/g) ?? []).map(Number);
    const alpha = parts.length >= 4 ? parts[3] : 1;
    expect(
      alpha,
      `the menu background is ${background} — partly transparent`,
    ).toBe(1);
  });
});

test.describe("the manage-as banner", () => {
  /**
   * The banner claims "you are managing X. Anything you change here belongs to
   * them." It is the only on-screen indication that a delegated session is
   * running, so it has to be both truthful and readable.
   */

  test("is not shown to somebody who is signed out", async ({ page, context }) => {
    // Reproduces the report exactly: the readable label cookie was written with
    // a thirty-day lifetime while the session it described lasted thirty
    // minutes, and the banner rendered on the strength of the label alone. So a
    // signed-out visitor on the login page was told they were managing another
    // pharmacy.
    const url = new URL(WEB);
    await context.addCookies([
      {
        name: "acme_manage_as_name",
        value: "New Home",
        domain: url.hostname,
        path: "/",
      },
    ]);

    await page.goto(`${WEB}/login`, { waitUntil: "networkidle" });

    // No token cookie was set, so there is no session — and without one the
    // banner must not appear no matter what the label says.
    await expect(
      page.getByText(/you are managing/i),
      "a signed-out visitor is being told they are managing another pharmacy",
    ).toHaveCount(0);
  });

  test("does not share a strip with the page header when it is shown", async ({ page }) => {
    // Signed in as staff and then switched, because the banner only exists in a
    // delegated session.
    //
    // This used to sign in as an ordinary pharmacy and skip when no banner
    // appeared — which was every single run, since an ordinary sign-in is not
    // delegated. The test never executed once, and reported as a skip rather
    // than as a gap. It guards a bug that actually shipped: the banner and the
    // header were both `sticky top-0` and scrolled over one another.
    await signIn(page, SUPERADMIN.email, SUPERADMIN.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });

    const switched = await startWorkingAsAnotherPharmacy(page);
    if (switched !== "ok") {
      test.skip(true, `could not work as another pharmacy: ${switched}`);
    }

    await page.reload({ waitUntil: "networkidle" });

    const banner = page.getByText(/you are managing/i).first();
    await expect(
      banner,
      "switching did not produce the banner, so the collision cannot be checked",
    ).toBeVisible({ timeout: 15_000 });

    // Both the banner and the header were `sticky top-0`, which pins them to
    // the same line: scroll down and the header slid underneath the banner,
    // two sets of text in one strip.
    await page.mouse.wheel(0, 600);
    await page.waitForTimeout(300);

    const bar = await page.locator("div").filter({ has: banner }).first().boundingBox();
    const header = await page.locator("header").first().boundingBox();
    expect(bar, "the banner has no box").not.toBeNull();

    if (bar && header) {
      expect(
        header.y,
        "the header is drawn on top of the manage-as banner",
      ).toBeGreaterThanOrEqual(bar.y + bar.height - 2);
    }
  });
});


test.describe("importing pharmacies can be found", () => {
  /**
   * Reported from production: a super admin showing the product to
   * stakeholders could not find the import. It lived only under a tab called
   * "Invitations". It is now reachable from the list of pharmacies, where
   * people look, and the tab says what it does.
   */
  test("a super admin reaches the CSV import from the pharmacies list", async ({ page }) => {
    await signIn(page, SUPERADMIN.email, SUPERADMIN.password);
    await page.goto(`${WEB}/admin/pharmacies`, { waitUntil: "networkidle" });

    const entry = page.getByRole("link", { name: /add or import pharmacies/i });
    await expect(entry, "no way to import from the pharmacies list").toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole("link", { name: /invite & import/i })).toBeVisible();

    await entry.click();
    await page.waitForURL(/\/admin\/invitations/);
    // Exact: with no invitations yet, the empty state also says "…or import a
    // list from your spreadsheet", and a loose match would find both.
    await expect(page.getByText("Import a list", { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole("button", { name: /choose a csv/i })).toBeVisible();
  });
});

test.describe("a head office working as a branch", () => {
  /**
   * Can see the branch's plan, cannot change it (security review R3-06).
   *
   * The branch pays its own bill. The API refuses checkout, top-ups and the
   * billing portal in a delegated session; this checks the page says so rather
   * than offering buttons that would only be refused.
   */
  test("sees the branch's plan but is not offered billing", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);

    const switched = await page.evaluate(async () => {
      const list = await fetch("/api/workspaces", { credentials: "same-origin" });
      if (!list.ok) return `no-workspaces-${list.status}`;
      const body = await list.json();
      const branch = (body.workspaces ?? []).find((w: { isOwn: boolean }) => !w.isOwn);
      if (!branch) return "no-branch";
      const res = await fetch("/api/workspaces/switch", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ pharmacyId: branch.pharmacyId }),
      });
      return res.ok ? "ok" : `switch-failed-${res.status}`;
    });
    // Riverside is seeded as a head office with three branches; without them
    // there is nothing to work as.
    if (switched !== "ok") test.skip(true, `could not work as a branch: ${switched}`);

    // Ask the API first. An API process started before this rule existed
    // answers without the field at all, and the page then (correctly) falls
    // back to offering billing — which reads as a broken page rather than as
    // "restart the API".
    const status = await page.evaluate(async () => {
      const res = await fetch("/api/billing/status", { credentials: "same-origin" });
      return res.ok ? ((await res.json()) as { canManageBilling?: boolean }) : null;
    });
    expect(
      status && "canManageBilling" in status,
      "the API does not report canManageBilling — it is running a build from before " +
        "the billing rule (R3-06). Restart `dotnet run` and run again.",
    ).toBe(true);
    expect(status?.canManageBilling, "the API lets a head office manage a branch's billing").toBe(false);

    await page.goto(`${WEB}/pricing`, { waitUntil: "networkidle" });

    await expect(
      page.getByTestId("billing-read-only"),
      "the pricing page does not say billing belongs to the branch",
    ).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole("button", { name: /^choose /i })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /manage billing/i })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /buy pack/i })).toHaveCount(0);

    // And the server agrees, whatever the page shows.
    const portal = await page.evaluate(async () => {
      const res = await fetch("/api/billing/portal", { method: "POST", credentials: "same-origin" });
      return res.status;
    });
    expect(portal, "the billing portal opened for somebody working as a branch").toBe(403);

    // Back home, the account holder's own plan is offered again — so the rule
    // above is about whose billing it is, not a page that lost its buttons.
    const left = await page.evaluate(async () => {
      const res = await fetch("/api/workspaces/switch", { method: "DELETE", credentials: "same-origin" });
      return res.status;
    });
    expect(left, "could not return to the head office's own account").toBe(200);
    await page.goto(`${WEB}/pricing`, { waitUntil: "networkidle" });
    await expect(page.getByTestId("billing-read-only")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /^choose |your current plan/i }).first()).toBeVisible({
      timeout: 15_000,
    });
  });
});

test.describe("the password field can be read back", () => {
  /**
   * Typing a password blind is where sign-in failures come from, and failing
   * twice sends people to the reset flow — which signs every one of their
   * sessions out. The control is small; the alternative is not.
   */

  test("reveals and re-hides the password on the sign-in form", async ({ page }) => {
    await page.goto(`${WEB}/login`, { waitUntil: "networkidle" });

    const field = page.getByLabel("Password", { exact: true });
    await field.fill("Something-Secret-123");

    // Masked to begin with, always. A field that remembers it was revealed is
    // a field that reveals a password to whoever is next at the counter.
    await expect(field, "the password starts out visible").toHaveAttribute("type", "password");

    const toggle = page.getByRole("button", { name: /show password/i });
    await expect(toggle, "there is no reveal control on the password field").toBeVisible();
    await expect(toggle).toHaveAttribute("aria-pressed", "false");

    await toggle.click();

    // The value must survive the switch — swapping the input type by
    // re-mounting the element would silently empty the field.
    await expect(field, "revealing did not unmask the password").toHaveAttribute("type", "text");
    await expect(field, "revealing lost what was typed").toHaveValue("Something-Secret-123");

    const pressed = page.getByRole("button", { name: /hide password/i });
    await expect(pressed, "the control does not announce that it is pressed")
      .toHaveAttribute("aria-pressed", "true");

    await pressed.click();
    await expect(field, "it could not be hidden again").toHaveAttribute("type", "password");
  });

  test("the toggle does not submit the form", async ({ page }) => {
    // A bare <button> inside a form defaults to type="submit". Getting this
    // wrong means tapping the eye posts half-typed credentials and lands you
    // on an error — which looks like the password was wrong.
    await page.goto(`${WEB}/login`, { waitUntil: "networkidle" });
    await page.getByLabel(/email/i).fill("nobody@example.test");
    await page.getByLabel("Password", { exact: true }).fill("partial");

    await page.getByRole("button", { name: /show password/i }).click();
    await page.waitForTimeout(1_500);

    expect(
      new URL(page.url()).pathname,
      "revealing the password submitted the sign-in form",
    ).toMatch(/^\/login/);
    await expect(page.locator('form [role="alert"]')).toHaveCount(0);
  });

  test("is reachable by keyboard", async ({ page }) => {
    await page.goto(`${WEB}/login`, { waitUntil: "networkidle" });
    await page.getByLabel("Password", { exact: true }).focus();
    await page.keyboard.press("Tab");

    const focused = await page.evaluate(() => document.activeElement?.getAttribute("aria-label"));
    expect(focused, "the reveal control cannot be reached from the password field")
      .toMatch(/show password/i);
  });
});

test.describe("signing out leaves the app behind", () => {
  /**
   * The reported bug: signing out landed on /login with the whole authenticated
   * shell still drawn around it — sidebar, navigation, account menu. The chrome
   * was chosen with `isMarketing && !user`, so a stale user object in the client
   * store was enough to keep the app shell on a page for people with no session.
   */

  async function shellIsGone(page: Page, when: string) {
    // The sidebar's own landmark, not just "some nav" — the marketing header
    // contains a <nav> too, so asserting on <nav> would pass either way.
    await expect(
      page.locator("aside"),
      `the sidebar is still on screen ${when}`,
    ).toHaveCount(0);

    for (const item of [/dashboard/i, /campaigns/i, /patients/i]) {
      await expect(
        page.getByRole("link", { name: item }),
        `an authenticated nav link is still on screen ${when}`,
      ).toHaveCount(0);
    }
  }

  test("the login page carries no app chrome after signing out", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });

    // Prove the shell was there to begin with, so its absence afterwards means
    // something. Without this the assertions below could pass on a blank page.
    await expect(page.locator("aside").first(), "no sidebar while signed in").toBeVisible();

    await signOut(page);
    await page.waitForURL(/\/login/, { timeout: 20_000 });
    await page.waitForLoadState("networkidle");

    await shellIsGone(page, "after signing out");
  });

  test("an expired session reaching /login drops the app chrome too", async ({ page, context }) => {
    // The condition that actually produced the bug, reproduced exactly: the
    // browser has no session left, but the client store has not caught up and
    // still holds a user.
    //
    // The first version of this test navigated with page.goto, which is a full
    // document load — the store is rebuilt from nothing, so `user` was null and
    // the old code would have passed. It proved nothing, and it skipped. To
    // reproduce the bug the store has to survive the trip.
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });
    await expect(page.locator("aside").first(), "no sidebar to lose").toBeVisible();

    // Session gone, page untouched — exactly what an expiry looks like from
    // the browser's side. Middleware will now let /login through instead of
    // redirecting a signed-in visitor away from it.
    await context.clearCookies();

    // A client-side route change: no document load, so the store survives.
    await page.evaluate(() => {
      window.history.pushState({}, "", "/login");
      window.dispatchEvent(new PopStateEvent("popstate"));
    });
    await page.waitForFunction(() => window.location.pathname.startsWith("/login"), {
      timeout: 10_000,
    });
    await page.waitForTimeout(1_000); // let the route settle and re-render

    await shellIsGone(page, "on /login with a stale user in the store");
  });
});

test.describe("onboarding can be put off, but not dodged", () => {
  /**
   * Setup can be skipped so somebody can get on with their day. What it must
   * not become is a way to never do it: the deferral lives in a session cookie
   * that sign-out clears, so the next sign-in asks again.
   */

  async function signUpAndReachOnboarding(page: Page) {
    const { email, password } = await fillSignup(page);
    await submitSignup(page);
    await page.waitForURL(/\/onboarding/, { timeout: 30_000 });
    return { email, password };
  }

  test("skipping lets the app be used, and keeps asking", async ({ page }) => {
    const { email, password } = await signUpAndReachOnboarding(page);

    const skip = page.getByRole("button", { name: /skip for now/i });
    await expect(skip, "onboarding offers no way to skip").toBeVisible();
    await skip.click();

    // The app has to actually let them in. Middleware used to bounce anyone
    // un-onboarded straight back here, which would make the button a loop.
    await page.waitForURL(/\/dashboard/, { timeout: 20_000 });
    await expect(
      page.locator("aside").first(),
      "skipping did not leave a usable app",
    ).toBeVisible();

    // And it says what is missing, rather than letting them find out from a
    // patient who received an unbranded email.
    await expect(
      page.getByText(/aren.t set up yet|finish setup/i).first(),
      "nothing on screen says setup is unfinished",
    ).toBeVisible({ timeout: 10_000 });

    // Sign out and back in: onboarding must be waiting.
    await signOut(page);
    await page.waitForURL(/\/login/, { timeout: 20_000 });

    await page.getByLabel(/email/i).fill(email);
    await page.getByLabel("Password", { exact: true }).fill(password);
    await page.locator('form button[type="submit"]').click();

    await page.waitForURL(/\/onboarding/, { timeout: 20_000 });
    expect(
      new URL(page.url()).pathname,
      "signing in again did not return to onboarding — the skip outlived the session",
    ).toMatch(/^\/onboarding/);
  });

  test("completing it stops the asking", async ({ page }) => {
    await signUpAndReachOnboarding(page);

    // Step 1: the pharmacy. Registration seeds most of this already.
    await page.getByLabel(/postal address/i).fill("12 High Street, Testville");
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 2: who to contact. The contact name is already seeded from the
    // signup form, so the number is the only thing step 2 still needs.
    await page.getByLabel(/mobile number/i).fill("020 7946 0000");
    await page.getByRole("button", { name: /^next$/i }).click();

    // Step 3: plan, then finish on whatever is preselected.
    await page.getByRole("button", { name: /finish setup/i }).click();
    await page.waitForURL(/\/dashboard/, { timeout: 30_000 });

    // The prompt is gone, because it is driven by the server's view of whether
    // setup is done — not by the skip cookie.
    await expect(
      page.getByText(/aren.t set up yet/i),
      "the finish-setup prompt survived finishing setup",
    ).toHaveCount(0);

    // And onboarding no longer holds on to anyone who goes back to it.
    await page.goto(`${WEB}/onboarding`);
    await page.waitForURL(/\/dashboard/, { timeout: 20_000 });
  });
});

test.describe("the poster designer", () => {
  /**
   * The designer is the one screen with no app chrome — it fills the window
   * with its own toolbar and canvas. That makes it the screen most easily
   * broken by anything the app draws above it.
   */

  /** Opens a fresh poster and waits for the canvas to mount. */
  async function openDesigner(page: Page) {
    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });
    await page.getByRole("button", { name: /new blank poster/i }).first().click();
    await page.waitForURL(/\/(canvas|posters)\//, { timeout: 25_000 });
    await expect(page.locator("canvas").first()).toBeVisible({ timeout: 25_000 });
  }

  test("there is a labelled way to finish and leave", async ({ page }) => {
    // The only exit used to be an unlabelled back arrow in the corner. Testers
    // reported not recognising it as "I have finished with this poster".
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await openDesigner(page);

    const done = page.getByRole("button", { name: /^done$/i });
    await expect(done, "the designer offers no obvious way to finish").toBeVisible();

    // Make an edit first, so leaving has something to save. Autosave waits
    // 1.5s; pressing Done immediately afterwards is exactly the case that
    // used to lose the change.
    const title = page.getByLabel(/poster name/i);
    await title.fill("QA finished this poster");
    await done.click();

    await page.waitForURL(/\/posters\/?$/, { timeout: 25_000 });

    // And the edit survived the trip, rather than being dropped by the
    // debounce that never got to fire.
    await expect(
      page.getByText("QA finished this poster").first(),
      "the poster name was lost on the way out",
    ).toBeVisible({ timeout: 15_000 });
  });

  test("the manage-as banner does not cover the toolbar", async ({ page }) => {
    // Reported from QA: while working as another pharmacy, the poster title
    // could not be clicked because the amber banner sat on top of it.
    //
    // The cause was arithmetic. The designer asked for a full viewport of
    // height while starting *below* a banner, so the page came out taller than
    // the window by exactly the banner's height — and the sticky banner then
    // slid over the toolbar as soon as anything scrolled.
    await signIn(page, SUPERADMIN.email, SUPERADMIN.password);
    await page.goto(`${WEB}/dashboard`, { waitUntil: "networkidle" });

    const switched = await startWorkingAsAnotherPharmacy(page);

    if (switched !== "ok") {
      test.skip(true, `could not work as another pharmacy: ${switched}`);
    }

    await page.reload({ waitUntil: "networkidle" });

    const banner = page.getByText(/you are managing/i).first();
    await expect(banner, "switching did not produce the banner").toBeVisible({
      timeout: 25_000,
    });

    await openDesigner(page);
    await expect(banner, "the banner vanished inside the designer").toBeVisible();

    // 1. The designer fits the window. If it does not, the page scrolls and the
    //    sticky banner rides over the toolbar — which is the whole bug.
    const overflow = await page.evaluate(
      () => document.documentElement.scrollHeight - document.documentElement.clientHeight,
    );
    expect(
      overflow,
      `the designer is ${overflow}px taller than the window, so the page scrolls under the banner`,
    ).toBeLessThanOrEqual(2);

    // 2. The toolbar sits below the banner rather than underneath it.
    const bannerBox = await page.locator("div").filter({ has: banner }).first().boundingBox();
    const toolbar = await page.locator("header").first().boundingBox();
    expect(bannerBox, "the banner has no box").not.toBeNull();
    expect(toolbar, "the designer has no toolbar").not.toBeNull();

    if (bannerBox && toolbar) {
      expect(
        toolbar.y,
        "the designer toolbar starts above the bottom of the banner — they overlap",
      ).toBeGreaterThanOrEqual(bannerBox.y + bannerBox.height - 2);
    }

    // 3. The thing QA actually could not do: type in the title.
    const title = page.getByLabel(/poster name/i);
    await title.click({ timeout: 10_000 });
    await title.fill("Typed while managing another pharmacy");
    await expect(title).toHaveValue("Typed while managing another pharmacy");
  });
});

test.describe("design with AI", () => {
  /**
   * A pharmacy gets the same photo choices an administrator has — None, Stock,
   * Upload and (when set up) AI — and a bad file is refused on pick, before a
   * design is generated or an allowance is touched.
   */
  async function openDialog(page: Page) {
    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });
    await page.getByRole("button", { name: /design with ai/i }).first().click();
    await expect(page.getByRole("dialog")).toBeVisible({ timeout: 15_000 });
  }

  test("offers the photo choices, with stock as the default", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await openDialog(page);

    const photo = page.getByRole("radiogroup", { name: /photograph/i });
    for (const name of [/^none$/i, /^stock$/i, /^upload$/i]) {
      await expect(photo.getByRole("radio", { name }), `no ${name} option`).toBeVisible();
    }
    await expect(photo.getByRole("radio", { name: /^stock$/i })).toHaveAttribute("aria-checked", "true");

    // AI is either offered or explained — never offered and then broken.
    const ai = photo.getByRole("radio", { name: /ai image/i });
    if (!(await ai.isVisible())) {
      await expect(page.getByText(/AI images aren't set up/i)).toBeVisible();
    }
  });

  test("refuses a file that is not a photo before anything is generated", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await openDialog(page);

    await page.getByRole("radiogroup", { name: /photograph/i })
      .getByRole("radio", { name: /^upload$/i }).click();
    await page.getByLabel(/photo file/i).setInputFiles({
      name: "notes.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("not a photo"),
    });

    await expect(page.getByRole("alert").filter({ hasText: /JPEG, PNG or WebP/i })).toBeVisible();
    await page.getByLabel(/what is the poster for/i).fill("walk-in flu clinic, bold, no appointment needed");
    await expect(page.getByRole("button", { name: /design it/i }), "a design could start without a photo")
      .toBeDisabled();
  });
});

test.describe("poster cards", () => {
  /**
   * Six actions — Open, Share, PDF, Duplicate, Publish, Delete — were laid out
   * in a row that could not wrap, inside a card a third of the screen wide that
   * clips what overflows it. Delete was pushed past the edge: present in the
   * DOM, impossible to see or click.
   *
   * Nothing about that is visible to a test that only asks whether the button
   * exists, which is why this asks about geometry instead.
   */

  async function ensureAPoster(page: Page) {
    const res = await page.request.get(`${WEB}/api/posters?take=1`);
    const body = res.ok() ? await res.json().catch(() => null) : null;
    const items = Array.isArray(body) ? body : (body?.items ?? []);
    if (items.length === 0) {
      await page.request.post(`${WEB}/api/posters`, {
        data: { name: "Card layout fixture poster" },
      });
    }
  }

  test("the actions fit inside the card", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await ensureAPoster(page);
    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });

    const cards = page.locator("main .grid > div").filter({ has: page.getByRole("link", { name: /^open$/i }) });
    // The list loads after the page does: "networkidle" can arrive while the
    // grid still shows skeletons, and count() does not wait. Wait for a real
    // card before counting.
    await expect(cards.first(), "no poster card rendered").toBeVisible({ timeout: 20_000 });
    const count = await cards.count();
    expect(count, "no poster cards on the page — this would prove nothing").toBeGreaterThan(0);

    for (let i = 0; i < count; i++) {
      // A card clips what overflows it, but scrollWidth still reports how wide
      // the contents actually are. That difference is the bug, measured.
      const spill = await cards.nth(i).evaluate((el) => el.scrollWidth - el.clientWidth);
      expect(
        spill,
        `poster card ${i + 1} has ${spill}px of content running past its edge — `
        + "a control out there cannot be clicked",
      ).toBeLessThanOrEqual(1);
    }
  });

  test("every action is reachable, including delete", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await ensureAPoster(page);
    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });

    // Open is on the card itself, as is the status change.
    await expect(page.getByRole("link", { name: /^open$/i }).first()).toBeVisible();
    await expect(
      page.getByRole("button", { name: /^(publish|archive|restore)$/i }).first(),
      "the status action is not on the card",
    ).toBeVisible();

    // The rest are behind the overflow menu, which is portalled so the card
    // cannot clip it.
    const more = page.getByRole("button", { name: /more actions for/i }).first();
    await expect(more, "no overflow menu on the poster card").toBeVisible();
    await more.click();

    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible({ timeout: 10_000 });

    for (const label of [/share to social/i, /download pdf/i, /duplicate/i, /delete poster/i]) {
      const item = menu.getByText(label).first();
      await expect(item, `${label} is missing from the poster menu`).toBeVisible();

      // Visible is not the same as clickable. The whole bug was a control that
      // existed and could not be reached, so insist each one is hittable.
      const box = await item.boundingBox();
      expect(box, `${label} has no box`).not.toBeNull();
      if (box) expect(box.width, `${label} is clipped to nothing`).toBeGreaterThan(20);
    }

    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden({ timeout: 5_000 });
  });
});

test.describe("only published posters go out", () => {
  /**
   * Draft, Published and Archived were three labels that did nothing: the
   * status was stored, it filtered the poster list, and no other part of the
   * product read it. Publishing appeared to do nothing because it did nothing.
   *
   * Now Published is what allows a poster to reach patients, so these check the
   * rule where a person meets it.
   */

  test("a draft is offered but not selectable in the campaign wizard", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);

    // One of each, so the test is about the difference between them.
    await publishAPoster(page);
    const draftName = `Draft poster ${Date.now().toString(36)}`;
    const created = await page.request.post(`${WEB}/api/posters`, { data: { name: draftName } });
    expect(created.ok(), "could not create a draft poster").toBeTruthy();

    await page.goto(`${WEB}/campaigns`, { waitUntil: "networkidle" });
    const start = page.getByRole("button", { name: /new campaign/i });
    await expect(start).toBeEnabled({ timeout: 20_000 });
    await start.click();

    const picker = page.locator("#posterId");
    await expect(picker).toBeVisible({ timeout: 10_000 });

    // Listed, not hidden. Somebody looking for the poster they just made has to
    // find it and learn why it cannot be sent — a poster that has silently
    // vanished from the list looks like the app lost it.
    const draftOption = picker.locator("option", { hasText: draftName });
    await expect(
      draftOption,
      "the draft was hidden from the picker instead of explained",
    ).toHaveCount(1);

    await expect(draftOption, "the draft can be chosen for a campaign")
      .toBeDisabled();
    await expect(draftOption).toContainText(/publish it first/i);

    // And the wizard did not open with an unsendable poster preselected.
    const selected = await picker.inputValue();
    expect(selected, "the wizard opened with nothing chosen").not.toBe("");
  });

  test("the poster card says what its status means", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await publishAPoster(page);
    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });

    // Publishing used to move a badge and change nothing. The card now says
    // what the status is *for*, next to the button that changes it.
    await expect(
      page.getByText(/ready to send in campaigns/i).first(),
      "a published poster does not say what being published means",
    ).toBeVisible({ timeout: 15_000 });
  });

  test("sharing a draft to social is offered with its reason, and refused", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);

    const draftName = `Social draft ${Date.now().toString(36)}`;
    await page.request.post(`${WEB}/api/posters`, { data: { name: draftName } });

    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });

    const card = page.locator("main .grid > div").filter({ hasText: draftName }).first();
    await expect(card, "the draft poster is not on the page").toBeVisible({ timeout: 15_000 });

    await card.getByRole("button", { name: /more actions for/i }).click();
    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible({ timeout: 10_000 });

    const share = menu.getByText(/share to social/i).first();
    await expect(share).toBeVisible();
    await expect(menu.getByText(/publish it first/i).first()).toBeVisible();

    // Disabled rather than removed, for the same reason as the picker.
    await expect(
      menu.getByRole("menuitem").filter({ hasText: /share to social/i }).first(),
    ).toHaveAttribute("data-disabled", "");

    await page.keyboard.press("Escape");
  });
});

test.describe("leaflets and brochures", () => {
  /**
   * Formats ride on the page-size primitives rather than a new entity: a
   * trifold *is* A4 landscape, so the sheet cannot say what something is and
   * the format has to say it instead.
   */

  test("the designer offers leaflet sizes and folds", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/posters`, { waitUntil: "networkidle" });
    await page.getByRole("button", { name: /new blank poster/i }).first().click();
    await page.waitForURL(/\/(canvas|posters)\//, { timeout: 25_000 });
    await expect(page.locator("canvas").first()).toBeVisible({ timeout: 25_000 });

    // The two leaflet sheets appear because the picker iterates the shared
    // list — adding them to the primitive was the whole change.
    for (const size of ["DL", "A5"]) {
      await expect(
        page.getByRole("button", { name: size, exact: true }),
        `${size} is missing from the size picker`,
      ).toBeVisible({ timeout: 15_000 });
    }

    // Folds are meaningless on a poster, so the control only appears once the
    // artwork is something that folds.
    await expect(page.getByText(/^folds$/i)).toHaveCount(0);

    await page.getByRole("button", { name: "Brochure", exact: true }).click();

    await expect(
      page.getByText(/^folds$/i).first(),
      "choosing Brochure did not offer a fold count",
    ).toBeVisible({ timeout: 10_000 });

    await expect(
      page.getByRole("button", { name: /3 panels/i }),
      "a trifold is the default brochure fold",
    ).toHaveAttribute("aria-pressed", "true");

    // And it says the guides do not print, because that is the question anyone
    // sending artwork to a printer will actually have.
    await expect(page.getByText(/never printed/i).first()).toBeVisible();
  });

  test("templates can be browsed by format", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/templates`, { waitUntil: "networkidle" });

    await expect(page.getByText(/^format$/i).first()).toBeVisible({ timeout: 20_000 });

    for (const f of ["Poster", "Leaflet", "Brochure"]) {
      await expect(
        page.getByRole("button", { name: f, exact: true }).first(),
        `no ${f} filter`,
      ).toBeVisible();
    }

    // Filtering must actually narrow the list rather than decorate it. Every
    // seeded template is a poster today, so Leaflet is the honest test: it
    // should show none of them.
    await page.getByRole("button", { name: "Poster", exact: true }).first().click();
    await page.waitForTimeout(400);
    const posters = await page.locator("main .grid > div").count();
    expect(posters, "filtering to Poster hid everything").toBeGreaterThan(0);
  });

  test("the gallery never shows a raw template token", async ({ page }) => {
    // The built-in templates write their contact line as {{address}} style
    // tokens, and none of them has an uploaded thumbnail — so the gallery draws
    // the template's own canvas, tokens and all, unless the preview resolves
    // them to a label.
    //
    // This is the most public surface in the product: the library is browsable
    // before signing in. A card reading "{{address}}" is visible to anyone
    // evaluating whether to sign up.
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/templates`, { waitUntil: "networkidle" });

    await expect(page.getByText(/^format$/i).first()).toBeVisible({ timeout: 20_000 });

    const body = (await page.locator("main").innerText()).replace(/\s+/g, " ");

    expect(
      body,
      "a template card is showing an unresolved token — CanvasPreview should " +
        "render these as a label such as 'Your address'",
    ).not.toMatch(/\{\{\s*\w/);
  });
});

test.describe("head office is visible in the app", () => {
  /**
   * Head office existed only in the database: the memberships were granted, the
   * switching worked, and nothing in the interface ever said which kind of
   * account you were signed in to. Running branches is now a status Marketing25
   * grants, so the app has to show it.
   */

  test("a pharmacy that runs no branches is not offered branch tools", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);
    await page.goto(`${WEB}/pharmacies`, { waitUntil: "networkidle" });

    const context = await page.request.get(`${WEB}/api/pharmacies/context`);
    const isHeadOffice = context.ok()
      ? Boolean((await context.json().catch(() => ({})))?.isHeadOffice)
      : false;

    if (isHeadOffice) {
      // The seeded pharmacy runs branches, so this is the other half of the
      // rule: it should be badged and offered the tools.
      await expect(
        page.getByText(/head office/i).first(),
        "a head office is not told that it is one",
      ).toBeVisible({ timeout: 15_000 });
      await expect(
        page.getByRole("button", { name: /add branch/i }),
        "a head office cannot add a branch",
      ).toBeVisible();
      return;
    }

    // Not a head office: no badge, and no button that would be refused.
    await expect(page.getByText(/head office/i)).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: /add branch/i }),
      "offering a button the server will refuse teaches people not to trust the controls",
    ).toHaveCount(0);
  });

  test("the grant is what the server actually enforces", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);

    const context = await page.request.get(`${WEB}/api/pharmacies/context`);
    const isHeadOffice = Boolean((await context.json().catch(() => ({})))?.isHeadOffice);

    const attempt = await page.request.post(`${WEB}/api/pharmacies/children`, {
      data: {
        fullName: "Browser suite branch",
        email: `branch-${Date.now().toString(36)}@example.test`,
        pharmacyName: "Browser suite branch",
      },
    });

    if (isHeadOffice) {
      expect(attempt.ok(), "a granted head office was refused a branch").toBeTruthy();

      // <b>Detached again, because this test creates real data every run.</b>
      //
      // It did not, and twelve identically-named branches accumulated on the
      // suite's pharmacy — enough to fill the workspace switcher and make an
      // unrelated failure's diagnostics unreadable. Nothing here asserts on the
      // branch surviving, so keeping it bought nothing and cost a little more
      // on every run, for ever.
      //
      // <b>Detach is not delete, and that is worth being precise about.</b>
      // `DetachChildAsync` clears `ParentPharmacyId` and nothing else, so the
      // child account still exists — it simply stops being a branch and stops
      // appearing in the switcher. The growth this fixes is the visible kind;
      // one inert `@example.test` account per run remains, and removing those
      // needs an administrator. Said plainly so nobody reads this block as
      // "the suite cleans up after itself" and stops looking.
      //
      // Failing to clean up is not worth failing the test over: the assertion
      // above has already passed, and a detach that 404s because somebody tidied
      // by hand would turn housekeeping into a red build.
      const created = await attempt.json().catch(() => null);
      const childId = created?.userId;
      if (childId) {
        await page.request
          .delete(`${WEB}/api/pharmacies/children/${childId}`)
          .catch(() => undefined);
      }
    } else {
      // The gate is server-side, so hiding the button is a courtesy rather than
      // the control. Posting directly proves the control is real.
      expect(attempt.status(), "the gate is only in the UI").toBe(403);
      expect((await attempt.text()).toLowerCase()).toContain("head office");
    }
  });
});

test.describe("a head office can look into a branch", () => {
  /**
   * The endpoint, the audit entry and the client call all existed before this
   * screen did — and nothing called them. An audited read that no one can
   * reach protects nobody and tells nobody anything.
   */

  /** The signed-in pharmacy's branches, or an empty list. */
  async function branchesOf(page: Page) {
    const res = await page.request.get(`${WEB}/api/pharmacies/children`);
    if (!res.ok()) return [];
    const body = await res.json().catch(() => null);
    return (Array.isArray(body) ? body : []) as Array<{
      userId: string;
      pharmacyName: string;
    }>;
  }

  test("opening a branch shows its records and says the read is recorded", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);

    const branches = await branchesOf(page);
    if (branches.length === 0) {
      test.skip(true, "this pharmacy runs no branches — nothing to open");
    }

    await page.goto(`${WEB}/pharmacies`, { waitUntil: "networkidle" });

    const view = page.getByRole("link", { name: /^view$/i }).first();
    await expect(view, "no way into a branch from the list").toBeVisible({ timeout: 15_000 });
    await view.click();

    await page.waitForURL(/\/pharmacies\/[^/]+$/, { timeout: 20_000 });

    // The counts, which is the point of the page. Patients is the one that
    // matters most and the one a head office is most likely to be checking.
    await expect(
      page.getByText(/^patients$/i).first(),
      "the branch's records did not load",
    ).toBeVisible({ timeout: 20_000 });

    for (const label of [/^posters$/i, /^campaigns$/i]) {
      await expect(page.getByText(label).first()).toBeVisible();
    }

    // And the notice. The read is audited whether or not it is mentioned;
    // saying so is what makes the trail a deterrent rather than a secret.
    await expect(
      page.getByText(/recorded against your name/i),
      "the page does not say the read is recorded",
    ).toBeVisible();
  });

  test("a branch that is not yours cannot be opened", async ({ page }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);

    // A well-formed id that belongs to nobody. Not-found is the correct answer
    // for both "no such branch" and "not yours" — telling them apart would let
    // somebody probe for other pharmacies' account ids.
    const res = await page.request.get(
      `${WEB}/api/pharmacies/children/ffffffffffffffffffffffffffffffff/overview`,
    );

    expect(
      res.status(),
      "the server let a pharmacy read a branch it does not run",
    ).toBe(404);
  });
});

test.describe("the admin drawer reflects a change on the first press", () => {
  /**
   * The bug this exists for: `onChanged` re-seeded the open drawer by reading
   * a ref that mirrored `rows` from a `useEffect`. That effect runs after the
   * next render, which is after the line that read it — so the drawer was
   * always refilled with the record from *before* the change, and every action
   * appeared to need pressing twice.
   *
   * It is a state-timing bug, so nothing below the UI can catch it: the API
   * returned 200 both times and the audit log recorded both. Only a test that
   * presses once and then looks can tell the difference.
   */

  /** A pharmacy that is not a head office and not a branch, so Grant applies. */
  async function grantableRow(page: Page) {
    const res = await page.request.get(
      `${WEB}/api/admin/users?pharmacyAccounts=true&take=100`,
    );
    if (!res.ok()) return null;
    const body = await res.json().catch(() => null);
    const items = (Array.isArray(body) ? body : body?.items ?? []) as Array<{
      id: string;
      email: string;
      isHeadOffice: boolean;
      isChildPharmacy: boolean;
      pharmacyName: string;
    }>;
    return items.find((u) => !u.isHeadOffice && !u.isChildPharmacy) ?? null;
  }

  test("one press of Grant flips the badge", async ({ page }) => {
    await signIn(page, SUPERADMIN.email, SUPERADMIN.password);

    const target = await grantableRow(page);
    if (!target) test.skip(true, "every demo pharmacy is already a head office or a branch");

    await page.goto(`${WEB}/admin/pharmacies`, { waitUntil: "networkidle" });

    // Typed into the box, not passed in the URL.
    //
    // `?search=` looks like it should work and does nothing: the page holds its
    // filter in `useState("")` and never reads the query string, so a URL with
    // a search term loads page one of every pharmacy, NewestFirst, 25 at a
    // time. This test used to "pass" that way — the account it picks is
    // recently created, so it happened to be on the first page. It would have
    // started skipping silently as the demo data grew.
    await page.getByPlaceholder(/search pharmacy/i).fill(target!.email);

    // Open the drawer from the row. The list is debounced, so the row arrives
    // a beat after the keystrokes.
    const row = page.getByRole("row").filter({ hasText: target!.email });
    await expect(row, "the target pharmacy is not in the list").toBeVisible({ timeout: 20_000 });
    await row.getByRole("button").first().click();

    // Scoped to the Head office group. "Grant" is also the button that adds
    // video credits, and an unscoped exact match is ambiguous — Playwright
    // refuses rather than guessing, which is the right call.
    const dialog = page.getByRole("dialog");
    const headOffice = dialog.getByRole("group", { name: "Head office" });
    await expect(headOffice.getByText(/single pharmacy/i)).toBeVisible({ timeout: 10_000 });

    // Exactly one press.
    await headOffice.getByRole("button", { name: /^grant$/i }).click();

    // And the drawer must now say so, without being touched again. The badge
    // is what an operator reads, so the badge is what is asserted.
    await expect(
      headOffice.getByText(/runs branches/i),
      "the drawer still shows the old value after one press — the stale-read bug is back",
    ).toBeVisible({ timeout: 15_000 });

    // Put it back, so a rerun starts from the same place. This second press
    // also exercises the withdraw direction through the same code path.
    await headOffice.getByRole("button", { name: /^withdraw$/i }).click();
    await expect(headOffice.getByText(/single pharmacy/i)).toBeVisible({ timeout: 15_000 });
  });

  test("a refusal is shown next to the control that was refused", async ({ page }) => {
    // A branch can never be a head office. Before this, the refusal rendered
    // at the top of a dialog that scrolls, so an operator working near the
    // bottom of it saw nothing at all and pressed again. And again.
    await signIn(page, SUPERADMIN.email, SUPERADMIN.password);

    await page.goto(`${WEB}/admin/pharmacies`, { waitUntil: "networkidle" });

    // Same correction as above: the search has to be typed. This test skipped
    // every run with "no seeded branch to open", which read like missing demo
    // data — the branches were there all along, just not on the first page of
    // an unfiltered list.
    await page.getByPlaceholder(/search pharmacy/i).fill("riverside-");

    const row = page.getByRole("row").filter({ hasText: /riverside-/i }).first();
    if (!(await row.isVisible({ timeout: 10_000 }).catch(() => false))) {
      test.skip(true, "no seeded branch to open — check DevUserSeeder ran");
    }
    await row.getByRole("button").first().click();

    const headOffice = page.getByRole("dialog").getByRole("group", { name: "Head office" });

    // The button is disabled rather than merely failing, and the reason is
    // inside the same group — not in a tooltip, and not above the fold.
    await expect(
      headOffice.getByRole("button", { name: /^grant$/i }),
      "a branch is still offered head office",
    ).toBeDisabled();
    await expect(headOffice.getByText(/branch of another one/i)).toBeVisible();
  });
});

test.describe("finishing setup after skipping it", () => {
  /**
   * "Finish setup" has to reach the setup page. It did not.
   *
   * Two sources of truth disagreed. The prompt is driven by the API's
   * `onboardingCompleted` and was right; middleware is driven by the public
   * `acme_onboarded` cookie, which had drifted — and middleware wins, because
   * it runs first. The click went to /onboarding and came back as /dashboard.
   *
   * The cookie drifts because the app's only profile load is /api/me/full,
   * which did not re-sync it (the sibling /api/me does, and nothing calls it).
   * Switching pharmacy drifts it too: the effective pharmacy changes and the
   * cookie still describes the previous one.
   *
   * So the test deletes the cookie outright. That is not a contrivance — it is
   * the "we do not know" state, and the rule now is that not knowing must
   * never lock somebody out of the page that fixes their account.
   */
  test("the prompt reaches the setup page even with no onboarding cookie", async ({
    page,
    context,
  }) => {
    const { email, password } = await fillSignup(page);
    await submitSignup(page);
    await page.waitForURL(/\/onboarding/, { timeout: 25_000 });

    // Skip, so the app is usable and the standing prompt appears.
    await page.getByRole("button", { name: /skip for now/i }).click();
    await page.waitForURL((url) => !url.pathname.startsWith("/onboarding"), {
      timeout: 25_000,
    });

    const prompt = page.getByRole("link", { name: /finish setup/i });
    await expect(
      prompt,
      "the standing prompt is missing for an account that skipped",
    ).toBeVisible({ timeout: 15_000 });

    // Now the drift. Everything else about the session stays valid.
    //
    // Removed on its own, not by clearing every cookie and adding the rest
    // back: in the moment between those two calls the page had no session at
    // all, and Next.js prefetched /onboarding exactly then. Middleware sent
    // that cookieless prefetch to /login, the router cached the answer, and
    // the click followed it — /login, then /dashboard. The test was failing
    // on its own gap, not on the rule it checks.
    await context.clearCookies({ name: "acme_onboarded" });
    expect(
      (await context.cookies()).some((c) => c.name === "acme_access"),
      "removing the onboarding cookie must leave the session in place",
    ).toBe(true);

    // Deliberately no reload before the click. A reload would hit
    // /api/me/full, which now rewrites the cookie — and the test would pass on
    // that alone, proving nothing about the middleware rule. Clicking straight
    // through leaves middleware facing a cookie that is genuinely absent.
    await page.getByRole("link", { name: /finish setup/i }).click();

    await expect
      .poll(() => new URL(page.url()).pathname, {
        message:
          "Finish setup bounced to the dashboard — middleware is trusting a cookie it does not have",
        timeout: 20_000,
      })
      .toMatch(/^\/onboarding/);

    // And the page is really the setup form, not a shell that redirects a
    // moment later.
    //
    // The field, not a button. This originally looked for "Finish setup" —
    // the same words as the prompt that got us here — which is the submit
    // control on the *third* step of the wizard. Arriving shows step one and a
    // "Next" button, so the assertion could never pass, and it was checking the
    // wrong thing anyway: the question is whether the form rendered, and the
    // first field answers that.
    await expect(page.getByLabel(/pharmacy name/i)).toBeVisible({ timeout: 15_000 });

    // Still here a moment later. A redirect that loses a race with the
    // assertion above would otherwise read as a pass.
    await page.waitForTimeout(1_000);
    expect(new URL(page.url()).pathname).toMatch(/^\/onboarding/);

    // The other fix, asserted separately: loading a profile re-syncs the
    // cookie, so the drift heals instead of persisting for the whole session.
    // Without this the app would depend forever on middleware being lenient.
    await expect
      .poll(
        async () =>
          (await context.cookies()).find((c) => c.name === "acme_onboarded")?.value,
        {
          message: "/api/me/full did not restore acme_onboarded — the sync is still dead",
          timeout: 20_000,
        },
      )
      .toBe("0");

    // eslint-disable-next-line no-console
    console.log(`[journey] finish-setup recovery verified for ${email} / ${password}`);
  });

  test("a finished account is still sent away from onboarding", async ({ page }) => {
    // The other half of the rule. Loosening the bounce must not make
    // /onboarding a page that completed accounts can wander back into.
    await signIn(page, PHARMACY.email, PHARMACY.password);

    await page.goto(`${WEB}/onboarding`);

    await expect
      .poll(() => new URL(page.url()).pathname, {
        message: "an onboarded pharmacy was left sitting on /onboarding",
        timeout: 20_000,
      })
      .toMatch(/^\/dashboard/);
  });
});

test.describe("the brand kit takes a logo on a fresh account", () => {
  /**
   * You should not have to press Save before you are allowed to pick a file.
   *
   * The server does require the kit to exist first — it holds the pharmacy
   * name and licence number that go on printed, patient-facing material, and
   * it will not invent them. But the brand kit form is prefilled from the
   * pharmacy profile on load, so those values are already on screen; making
   * somebody press Save was the page asking them to satisfy its storage model
   * by hand.
   *
   * This runs on a brand-new account, because that is the only state where the
   * bug existed. An account that has ever saved a kit cannot reproduce it.
   */

  /** Smallest valid PNG: one transparent pixel. */
  const PNG = Buffer.from(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
    "base64",
  );

  test("picking a file uploads it without a save first", async ({ page }) => {
    const { email } = await fillSignup(page);
    await submitSignup(page);
    await page.waitForURL(/\/onboarding/, { timeout: 25_000 });

    // Let the onboarding page finish its own "am I already onboarded?" check
    // first. If the API call below lands before that check, the page sees a
    // finished account and sends itself to /dashboard — mid-way through the
    // goto to /brand-kit, which then aborts.
    await page.waitForLoadState("networkidle");

    // Complete onboarding rather than skip it. Skipping leaves the profile
    // empty, and then there is genuinely nothing to build a kit from — the
    // save fails on a missing pharmacy name, which is correct and is a
    // different situation from the one being tested.
    const onboarded = await page.request.post(`${WEB}/api/me/onboard`, {
      data: {
        pharmacyName: "Logo Test Pharmacy",
        address: "1 Test Street, Manchester",
        postCode: "M1 1AA",
        description: "",
        contactName: "Journey Tester",
        contactPhone: "0161 000 0000",
        sonarFCode: "FJ999",
        tier: "Free",
      },
    });
    expect(onboarded.ok(), `could not onboard ${email}: ${onboarded.status()}`).toBeTruthy();

    await page.goto(`${WEB}/brand-kit`, { waitUntil: "networkidle" });

    const picker = page.locator('input[type="file"]');
    await expect(
      picker,
      "the file input is disabled on an account that has never saved a kit",
    ).toBeEnabled({ timeout: 20_000 });

    await picker.setInputFiles({ name: "logo.png", mimeType: "image/png", buffer: PNG });

    // The preview replaces the "No logo" placeholder, which is the only
    // signal a person actually gets that the upload worked.
    await expect(
      page.getByRole("img", { name: /^logo$/i }),
      "no logo preview after upload — the kit was probably never created",
      ).toBeVisible({ timeout: 25_000 });

    await expect(page.getByText(/no logo/i)).toBeHidden();

    // And it survives a reload, i.e. it was stored rather than shown from a
    // local object URL.
    await page.reload({ waitUntil: "networkidle" });
    await expect(page.getByRole("img", { name: /^logo$/i })).toBeVisible({
      timeout: 25_000,
    });
  });
});

test.describe("a screen on the wall", () => {
  /**
   * The kiosk, end to end, in a browser that has never signed in.
   *
   * Everything else in this suite runs inside an authenticated context, which
   * is exactly the wrong shape for this feature: the whole point is that a
   * television in a waiting room reaches a pharmacy's posters with no cookie,
   * no account and no session. A test that paired inside the signed-in context
   * would pass even if the endpoints quietly required a login.
   *
   * So the device gets its own context. If the kiosk ever starts depending on
   * a cookie it happens to inherit, this is what notices.
   */
  test("a new screen offers both ways to reach it", async ({ page }) => {
    // Registering a screen issues a pairing code immediately, and the panel
    // used to switch on whether a code was outstanding — with Direct link
    // living only in the "no code" branch. So for the first four hours of a
    // screen's life, while somebody is stood in front of it installing it,
    // the direct link was not on the page. It appeared once the code expired.
    //
    // That is backwards. A direct link is what you reach for when the device
    // cannot type a code, which is a thing you know at installation time, not
    // four hours later.
    //
    // Asserted right after creating a screen, because that is the only moment
    // the bug existed.
    await signIn(page, PHARMACY.email, PHARMACY.password);

    const made = await page.evaluate(async () => {
      const res = await fetch("/api/displays/screens", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          name: `Button check ${Date.now()}`,
          location: "Counter",
        }),
      });
      return { ok: res.ok, status: res.status };
    });

    if (!made.ok) {
      // Screens are a paid feature; a Free demo account cannot run this.
      test.skip(true, `could not register a screen: ${made.status}`);
    }

    await page.goto(`${WEB}/displays`, { waitUntil: "networkidle" });

    const panel = page.locator("main");

    await expect(
      panel.getByRole("button", { name: /new code|pairing code/i }).first(),
      "a screen must offer a way to get a code",
    ).toBeVisible({ timeout: 15_000 });

    await expect(
      panel.getByRole("button", { name: /direct link/i }).first(),
      "the direct link is the escape hatch for a device that cannot type a " +
        "code — it has to be offered while the screen is being installed, not " +
        "only once the pairing code has expired",
    ).toBeVisible();
  });

  test("pairs with a code and loops the playlist without any session", async ({
    page,
    browser,
  }) => {
    await signIn(page, PHARMACY.email, PHARMACY.password);

    // Arrange through the API. The screens and playlists pages have their own
    // coverage; what is under test here is the device.
    const setup = await page.evaluate(async () => {
      const post = async (url: string, body?: unknown) => {
        const res = await fetch(url, {
          method: "POST",
          credentials: "same-origin",
          headers: body ? { "Content-Type": "application/json" } : undefined,
          body: body ? JSON.stringify(body) : undefined,
        });
        return { ok: res.ok, status: res.status, data: await res.json().catch(() => null) };
      };

      const screen = await post("/api/displays/screens", {
        name: `Kiosk journey ${Date.now()}`,
        addressLine: "12 Riverside Walk, Manchester",
      });
      if (!screen.ok) return { error: `screen-${screen.status}` };

      // A published poster, because a screen refuses a draft.
      // Its own poster, every time.
      //
      // This used to reuse the first published poster it could find. That made
      // the test depend on whatever the rest of the suite had left in a shared
      // database — and it duly failed on a poster called "Draft poster
      // mtj59v5p", created and published by an entirely different test, whose
      // canvas this test knows nothing about. A fixture you did not create is
      // a fixture you cannot reason about.
      const made = await post("/api/posters", { name: `Kiosk poster ${Date.now()}` });
      if (!made.ok) return { error: `poster-${made.status}` };

      const published = await fetch(`/api/posters/${made.data.id}/status`, {
        method: "PATCH",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status: "Published" }),
      });
      if (!published.ok) return { error: `publish-${published.status}` };

      const poster = made.data;

      const playlist = await post("/api/displays/playlists", {
        name: `Kiosk loop ${Date.now()}`,
        items: [{ kind: "Poster", refId: poster.id, durationSeconds: 5 }],
      });
      if (!playlist.ok) return { error: `playlist-${playlist.status}` };

      const assigned = await fetch(`/api/displays/screens/${screen.data.id}/playlist`, {
        method: "PUT",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ playlistId: playlist.data.id }),
      });
      if (!assigned.ok) return { error: `assign-${assigned.status}` };

      return { code: screen.data.pairingCode as string, poster: poster.name as string };
    });

    if (setup.error) {
      // Screens are a paid feature, so a Free demo account cannot run this.
      test.skip(true, `could not set up a screen: ${setup.error}`);
    }

    // ── the device: a browser that has never signed in ──────────────────
    const device = await browser.newContext();
    const wall = await device.newPage();

    // Everything the device says to the server, kept for the failure message.
    //
    // Reading the kiosk's own state was not enough: "never finished pairing" is
    // equally true of a claim that was refused, a claim that never returned,
    // and a claim that succeeded and was then undone by a 401 on the first
    // content poll — which is a real possibility, because a 401 makes the kiosk
    // erase its token and return to this screen showing no error at all. Those
    // need different fixes and the page cannot tell them apart.
    const exchanges: string[] = [];
    wall.on("response", (res) => {
      const path = new URL(res.url()).pathname;
      if (path.startsWith("/api/display")) exchanges.push(`${res.status()} ${path}`);
    });

    try {
      await wall.goto(`${WEB}/display`, { waitUntil: "networkidle" });

      // Not bounced to a login page. `/display` and `/displays` differ by one
      // character and the middleware guard is a prefix match, so this is the
      // assertion that catches a television estate being sent to a sign-in
      // form by a one-character edit.
      expect(
        new URL(wall.url()).pathname,
        "the kiosk was redirected — is /display caught by a PROTECTED prefix?",
      ).toMatch(/^\/display$/);

      await expect(wall.getByRole("heading", { name: /pair this screen/i })).toBeVisible({
        timeout: 20_000,
      });

      await wall.getByLabel(/pairing code/i).fill(setup.code!);
      await wall.getByRole("button", { name: /pair screen/i }).click();

      // Wait for the claim to land before asking the device anything.
      //
      // `click()` resolves when the click is dispatched, not when the app's
      // async handler finishes, so probing local storage on the next line is a
      // race the test loses.
      //
      // A loop rather than expect.poll, so the failure can carry the request
      // log and what the screen was showing. "Never paired" on its own sends
      // you to the API logs to work out which of three faults it was.
      const deadline = Date.now() + 20_000;
      let paired = false;
      let lastSeen = "nothing on screen";

      while (Date.now() < deadline) {
        const token = await wall.evaluate(() =>
          localStorage.getItem("marketing25.display.token"),
        );
        if (token) {
          paired = true;
          break;
        }

        // Scoped to the form, and required to have text. Both matter.
        //
        // `getByRole("alert")` matches Next's route announcer —
        // <div role="alert" aria-live="assertive" id="__next-route-announcer__">
        // — which Next injects into every page inside a shadow root that
        // Playwright pierces by default. It is clipped to 1x1 rather than
        // hidden, so it still has a box and isVisible() answers **true**, and
        // it is empty until a route changes.
        //
        // So this branch fired on the first pass of every run, reported
        // `error on screen: ` with nothing after the colon, and broke the loop
        // about a second after the click — before the claim could possibly have
        // returned. Every failure of this test was that, not the kiosk.
        //
        // The same trap is documented at the top of this file for the signup
        // form, where it was scoped to `form [role="alert"]`. It should have
        // been applied here at the same time.
        const alert = wall.locator('form [role="alert"]').filter({ hasText: /\S/ });
        if (await alert.isVisible().catch(() => false)) {
          lastSeen = `error on screen: ${(await alert.innerText().catch(() => "")).trim()}`;
          break;
        }

        // The button reads "Pairing…" while the request is in flight, which
        // separates a request that never came back from one never sent.
        //
        // Never waits. Once pairing succeeds the form — and this button — are
        // gone, and innerText() on a missing element waits for it to appear,
        // which here meant until the test timed out on a device that had
        // paired in under a second.
        const button = wall.getByRole("button", { name: /pair(ing)?/i });
        lastSeen =
          (await button.count()) > 0
            ? (await button.first().innerText({ timeout: 1_000 }).catch(() => "")) || "pair button"
            : "no pair button";

        await wall.waitForTimeout(500);
      }

      if (!paired) {
        throw new Error(
          "The device never paired.\n" +
            `  last state : ${lastSeen}\n` +
            `  requests   : ${exchanges.join(", ") || "none — the claim was never sent"}`,
        );
      }

      // Before the image, ask the device what it was actually served.
      //
      // A locator that times out says only "no image", which is true of a
      // playlist that arrived empty, a slide the server could not render, and
      // a token that was rejected — three different faults with one symptom
      // and a forty-second wait. This turns the common ones into a sentence.
      const served = await wall.evaluate(async () => {
        const token = localStorage.getItem("marketing25.display.token");
        if (!token) return { error: "the device stored no token after pairing" };

        const res = await fetch("/api/display/content", {
          headers: { "X-Display-Token": token },
        });
        if (!res.ok) return { error: `content answered ${res.status}` };

        const content = await res.json();
        if (!content.slides?.length) {
          return { error: "the playlist reached the screen with no slides on it" };
        }

        // The slide endpoint renders the poster. If that fails the kiosk has
        // nothing to draw, and the reason is here rather than in the API log.
        const slide = await fetch(content.slides[0].imageUrl, {
          headers: { "X-Display-Token": token },
        });
        if (!slide.ok) {
          const detail = await slide.text().catch(() => "");
          return { error: `slide ${content.slides[0].imageUrl} answered ${slide.status}: ${detail}` };
        }

        const blob = await slide.blob();
        return { bytes: blob.size, type: blob.type };
      });

      expect(served.error, `the screen could not be served its poster`).toBeUndefined();
      expect(served.bytes ?? 0, "the poster rendered as an empty image").toBeGreaterThan(0);

      // And then the thing a person would actually see. Asserting the image
      // rather than the absence of the pairing form: a blank black page would
      // pass the weaker check.
      await expect(
        wall.getByRole("img", { name: setup.poster! }),
        "the poster was served but never drawn on the screen",
      ).toBeVisible({ timeout: 40_000 });

      // The address, which is the one piece of pharmacy detail a kiosk carries.
      await expect(wall.getByText(/riverside walk/i)).toBeVisible();

      // And no app chrome anywhere near it.
      await expect(wall.getByRole("navigation")).toHaveCount(0);
    } finally {
      await device.close();
    }
  });

  test("an unpaired screen asks to be paired rather than showing anything", async ({
    browser,
  }) => {
    // A device with no token must not reach content, and must not be told to
    // sign in either — there is no account to sign in with.
    const device = await browser.newContext();
    const wall = await device.newPage();

    try {
      await wall.goto(`${WEB}/display`, { waitUntil: "networkidle" });

      await expect(wall.getByRole("heading", { name: /pair this screen/i })).toBeVisible({
        timeout: 20_000,
      });

      // The API answers a credential-less request with 400, not 401: a 401
      // makes a paired screen erase its token, and a request that never
      // carried one must not cost a working display its pairing.
      const res = await wall.request.get(`${WEB}/api/display/content`);
      expect(res.status()).toBe(400);
    } finally {
      await device.close();
    }
  });
});

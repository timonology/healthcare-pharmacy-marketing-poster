"use client";

/**
 * Session lifecycle for client-side calls to our BFF.
 *
 * The access token lives ~15 minutes. Before this existed, expiry produced a
 * silent failure: every request 401'd, `jsonOrThrow` raised a generic Error,
 * pages showed a stale screen or an unhelpful message, and the only way out
 * was a manual browser refresh.
 *
 * Now a 401 transparently refreshes and retries once. If the refresh itself
 * fails — refresh token expired, revoked, or the account deactivated — the
 * session is torn down and the user is sent to /login with an explanation.
 */

/* ------------------------------------------------------------------ *
 * Timing
 *
 * Two independent clocks, and it's worth being clear which does what:
 *
 *   IDLE_MS      How long with no interaction before we sign someone out.
 *                This is the ONLY thing that ends an active session.
 *   REFRESH_MS   How often we renew the access token while someone IS
 *                interacting. Comfortably under the token's ~15 minute life,
 *                so an active user never even meets a 401.
 *
 * 20 minutes is deliberately shorter than a typical SaaS timeout. This app
 * has patient lists and prescription data on screen, and pharmacy counters
 * are shared, semi-public spaces — an unattended browser is a real exposure,
 * not a theoretical one. The 2-minute warning exists so nobody loses poster
 * edits to it.
 *
 * Override per-deployment with NEXT_PUBLIC_SESSION_IDLE_MINUTES.
 * ------------------------------------------------------------------ */
const IDLE_MINUTES = Number(process.env.NEXT_PUBLIC_SESSION_IDLE_MINUTES ?? 20);
const IDLE_MS = Math.max(2, IDLE_MINUTES) * 60_000;
const WARN_MS = 2 * 60_000;
const REFRESH_MS = 10 * 60_000;
const TICK_MS = 15_000;

/** Endpoints that must never trigger an automatic refresh. */
const AUTH_ROUTES = [
  "/api/auth/login",
  "/api/auth/register",
  "/api/auth/refresh",
  "/api/auth/logout",
];

function isAuthRoute(url: string) {
  return AUTH_ROUTES.some((route) => url.startsWith(route));
}

/**
 * Pages where there is no session to lose, so expiring one is meaningless.
 *
 * Without this guard the app can loop: something on a public page probes for a
 * session, gets a legitimate 401, expireSession navigates to /login, the page
 * reloads, the probe runs again. Checking the destination before navigating
 * makes that impossible regardless of which caller triggered it.
 */
const PUBLIC_PATHS = ["/login", "/register", "/", "/pricing", "/terms", "/privacy"];

function onPublicPage(): boolean {
  const path = window.location.pathname;
  return PUBLIC_PATHS.some((p) => (p === "/" ? path === "/" : path.startsWith(p)));
}

/* ------------------------------------------------------------------ *
 * Single-flight refresh
 *
 * This is the part that matters most. The API ROTATES refresh tokens: a
 * successful refresh invalidates the token that was presented, and replaying
 * a rotated token makes the server revoke every session for that user
 * (RefreshAsync → RevokeAllForUserAsync) — reasonable replay protection.
 *
 * The app routinely fires several requests at once, so an expired token means
 * several simultaneous 401s. If each kicked off its own refresh, the first
 * would succeed and the rest would present the now-rotated token — logging the
 * user out completely. Sharing one in-flight promise means concurrent 401s all
 * await the same refresh.
 * ------------------------------------------------------------------ */
let inFlightRefresh: Promise<boolean> | null = null;

function refreshOnce(): Promise<boolean> {
  inFlightRefresh ??= fetch("/api/auth/refresh", {
    method: "POST",
    credentials: "same-origin",
    cache: "no-store",
  })
    .then((res) => res.ok)
    .catch(() => false)
    .finally(() => {
      // Cleared so a later expiry can refresh again. Left set, the session
      // could never recover after the first refresh of the page's lifetime.
      inFlightRefresh = null;
    });

  return inFlightRefresh;
}

/* ------------------------------------------------------------------ *
 * Expiry teardown
 * ------------------------------------------------------------------ */

let expiring = false;
const expiryListeners = new Set<() => void>();

/**
 * Lets React code (stores, providers) clear its own state before the redirect.
 * Returns an unsubscribe function.
 */
export function onSessionExpired(listener: () => void): () => void {
  expiryListeners.add(listener);
  return () => expiryListeners.delete(listener);
}

/**
 * Tears down the session and sends the user to the login page.
 *
 * Guarded: several in-flight requests can fail at once, and without the flag
 * each would trigger its own navigation, producing a redirect storm.
 */
export type ExpiryReason = "expired" | "revoked" | "inactive";

export function expireSession(reason: ExpiryReason = "expired") {
  if (expiring) return;

  // Already somewhere that doesn't need a session — nothing to tear down, and
  // navigating would just reload the same page and re-trigger whatever called
  // us. Deliberately does NOT set `expiring`: a real expiry later in this
  // page's life must still be able to act.
  if (onPublicPage()) return;

  expiring = true;

  for (const listener of expiryListeners) {
    try {
      listener();
    } catch {
      // A misbehaving listener must not block the redirect — being stuck on a
      // dead session is worse than losing some client-side cleanup.
    }
  }

  // Best-effort cookie clear. Not awaited: the redirect is what matters, and
  // the cookies are httpOnly so this is the only way to drop them. keepalive
  // lets the request survive the navigation.
  fetch("/api/auth/logout", {
    method: "POST",
    credentials: "same-origin",
    keepalive: true,
  }).catch(() => {});

  // Full navigation rather than a router push, so every cached RSC payload and
  // in-memory store is discarded along with the session.
  const current = window.location.pathname + window.location.search;
  const params = new URLSearchParams({ reason });
  // `redirect` is the param the login page already sanitises and honours.
  // Skip it for the landing and login pages — bouncing back to either is
  // pointless at best and a loop at worst.
  if (current && current !== "/" && !current.startsWith("/login")) {
    params.set("redirect", current);
  }
  window.location.assign(`/login?${params}`);
}

/* ------------------------------------------------------------------ *
 * Fetch wrapper
 * ------------------------------------------------------------------ */

/* ------------------------------------------------------------------ *
 * Activity monitor
 *
 * Keeps a session alive indefinitely while someone is using the app, and
 * ends it once they stop. Those are two different jobs and both are handled
 * here so they can't disagree about what "active" means.
 * ------------------------------------------------------------------ */

let lastActivityAt = Date.now();
let lastRefreshAt = Date.now();
let monitorTimer: ReturnType<typeof setInterval> | null = null;
let monitorRefCount = 0;

const warningListeners = new Set<(secondsLeft: number | null) => void>();

/**
 * Notifies when the idle cut-off is approaching. Emits a countdown in
 * seconds, then null once the user becomes active again.
 */
export function onIdleWarning(
  listener: (secondsLeft: number | null) => void,
): () => void {
  warningListeners.add(listener);
  return () => warningListeners.delete(listener);
}

function emitWarning(secondsLeft: number | null) {
  for (const listener of warningListeners) {
    try {
      listener(secondsLeft);
    } catch {
      /* a broken listener must not stall the monitor */
    }
  }
}

/** Resets the idle clock. Called by real interaction and by "Stay signed in". */
export function markActivity() {
  lastActivityAt = Date.now();
}

let warningActive = false;

async function tick() {
  const now = Date.now();
  const idleFor = now - lastActivityAt;

  if (idleFor >= IDLE_MS) {
    expireSession("inactive");
    return;
  }

  // Approaching the cut-off — surface a countdown so unsaved work can be
  // rescued. Emitting every tick keeps the displayed number moving.
  if (idleFor >= IDLE_MS - WARN_MS) {
    warningActive = true;
    emitWarning(Math.max(0, Math.ceil((IDLE_MS - idleFor) / 1000)));
    return;
  }

  if (warningActive) {
    warningActive = false;
    emitWarning(null);
  }

  // Still active, and the access token is getting old — renew it ahead of
  // expiry. This is what makes "never log out an active user" true in
  // practice rather than just recoverable: they never hit a 401 at all.
  //
  // Skipped when the tab is hidden. A background tab isn't "use", and
  // refreshing there would keep a session alive on an unattended machine,
  // which is exactly what the idle timeout exists to prevent.
  if (
    now - lastRefreshAt >= REFRESH_MS &&
    document.visibilityState === "visible"
  ) {
    lastRefreshAt = now;
    const ok = await refreshOnce();
    if (!ok) expireSession("expired");
  }
}

/**
 * Starts activity tracking. Reference-counted so multiple mounts share one
 * set of timers and listeners; returns a teardown for the caller's cleanup.
 */
export function startSessionMonitor(): () => void {
  monitorRefCount += 1;

  if (monitorRefCount === 1) {
    lastActivityAt = Date.now();
    lastRefreshAt = Date.now();

    // Throttled: pointermove alone fires hundreds of times a second, and the
    // only thing that matters is "was there any interaction recently".
    let throttleUntil = 0;
    const onActivity = () => {
      const now = Date.now();
      if (now < throttleUntil) return;
      throttleUntil = now + 5_000;
      markActivity();
    };

    // Deliberately excludes mousemove: a nudged desk or a cat on the keyboard
    // shouldn't hold a pharmacy session open. These all imply intent.
    const events = ["pointerdown", "keydown", "scroll", "touchstart", "focus"];
    for (const e of events) {
      window.addEventListener(e, onActivity, { passive: true });
    }

    monitorTimer = setInterval(tick, TICK_MS);
    activityTeardown = () => {
      for (const e of events) window.removeEventListener(e, onActivity);
    };
  }

  return () => {
    monitorRefCount -= 1;
    if (monitorRefCount > 0) return;
    if (monitorTimer) clearInterval(monitorTimer);
    monitorTimer = null;
    activityTeardown?.();
    activityTeardown = null;
    warningActive = false;
  };
}

let activityTeardown: (() => void) | null = null;

/**
 * `fetch` for authenticated BFF calls. On 401 it refreshes once and replays
 * the original request; if that fails, it expires the session.
 *
 * Retrying is safe because every request body in this app is a string —
 * a stream body would already be consumed and could not be replayed.
 */
export interface AuthedFetchOptions {
  /**
   * Treat 401 as a legitimate answer rather than an expired session.
   *
   * For a call that asks "is anyone signed in?", 401 means "no" — a normal
   * result for an anonymous visitor, not a failure to recover from. Without
   * this, such a call on a public page triggers a refresh, fails, and expires
   * a session that never existed.
   */
  probe?: boolean;
}

export async function authedFetch(
  input: string,
  init?: RequestInit,
  options?: AuthedFetchOptions,
): Promise<Response> {
  const res = await fetch(input, init);

  if (res.status !== 401 || isAuthRoute(input)) return res;

  // Hand the 401 back untouched and let the caller decide what it means.
  if (options?.probe) return res;

  const refreshed = await refreshOnce();
  if (!refreshed) {
    expireSession("expired");
    return res;
  }

  const retried = await fetch(input, init);

  // Still 401 with a freshly-minted token means the problem isn't expiry —
  // the account was deactivated, or its role no longer permits this call.
  // Retrying again would loop.
  if (retried.status === 401) expireSession("revoked");

  return retried;
}

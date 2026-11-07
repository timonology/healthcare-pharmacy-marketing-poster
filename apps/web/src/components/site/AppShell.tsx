"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { Menu, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ThemeToggle } from "@/components/theme-toggle";
import { AppSidebar, NAV_GROUPS, isActive } from "@/components/site/AppSidebar";
import { WorkspaceSwitcher } from "@/components/site/WorkspaceSwitcher";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { onSessionExpired, startSessionMonitor } from "@/lib/session";
import { SessionTimeoutDialog } from "@/components/site/SessionTimeoutDialog";
import { FinishSetupNotice } from "@/components/site/FinishSetupNotice";
import { useAuthStore } from "@/store/auth-store";
import { useSubscriptionStore } from "@/store/subscription-store";
import { LogoMark } from "@/components/brand/LogoMark";

/**
 * Pages where somebody is getting into, out of, or set up with an account.
 *
 * <b>These never render the app shell, whatever the client store believes.</b>
 * They used to be lumped in with the public pages below, behind a single
 * `isMarketing && !user` condition — so the sidebar appeared on /login whenever
 * a stale user object survived the trip there. Any soft navigation to /login
 * does that: `router.replace` keeps the store, and so the signed-out login page
 * rendered inside the signed-in chrome, complete with a navigation menu for an
 * account that was no longer signed in.
 *
 * Being signed out is not something to infer from client state that may be a
 * step behind. On these routes the answer is fixed by the route itself.
 */
const AUTH_PATTERNS: RegExp[] = [
  /^\/login\/?$/,
  /^\/register\/?$/,
  /^\/onboarding\/?$/,
  /^\/forgot-password\/?$/,
  /^\/reset-password\/?$/,
  /^\/verify-email\/?$/,
  /^\/activate\/?$/,
  // Opened by a patient from a campaign email (R2-06): never the app's chrome.
  /^\/unsubscribe\/?$/,
];

/**
 * Public content, which a signed-in person may legitimately visit.
 *
 * Unlike the pages above, these keep the app shell when there is a session —
 * following "Plan & billing" out of the account menu should not feel like being
 * thrown out of the app.
 */
const PUBLIC_PATTERNS: RegExp[] = [
  /^\/pricing\/?$/,
  /^\/terms\/?$/,
  /^\/privacy\/?$/,
  /^\/legal(\/|$)/,
];

/** Pages that already ship their own nav — no global chrome at all. */
const NO_CHROME_PATTERNS: RegExp[] = [
  /^\/$/, // landing has <LandingNav />
  /^\/posters\/[^/]+\/?$/, // poster editor has its own toolbar
  // A screen on a pharmacy wall. No header, no sidebar, no account menu — and
  // no auth either, so this has to be matched before the signed-out branch
  // below, which would otherwise wrap a kiosk in a marketing header.
  /^\/display(\/|$)/,
];

const COLLAPSE_KEY = "sonar.sidebar.collapsed";

export function AppShell({
  children,
  initialSignedIn = false,
}: {
  children: React.ReactNode;
  initialSignedIn?: boolean;
}) {
  const pathname = usePathname() ?? "";
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const subscription = useSubscriptionStore((s) => s.current);
  const setSubscription = useSubscriptionStore((s) => s.setCurrent);

  // Derived from the route alone, so it is trustworthy before any request
  // resolves. Used by the effects below as well as the render, which is why it
  // is computed up here rather than beside the other route checks.
  const isSignInPage = /^\/(login|register)\/?$/.test(pathname);

  // Optimistically treat the visitor as signed in until /api/auth/me answers.
  // The server passes this from cookies so the first paint already matches the
  // eventual state instead of flashing the marketing header.
  const [assumeSignedIn, setAssumeSignedIn] = useState(initialSignedIn);
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);

  // Restore the rail preference. Read in an effect (not lazy state) so SSR and
  // the first client render agree — otherwise React logs a hydration mismatch.
  useEffect(() => {
    try {
      setCollapsed(window.localStorage.getItem(COLLAPSE_KEY) === "1");
    } catch {
      /* private mode — fall back to expanded */
    }
  }, []);

  const toggleCollapsed = useCallback(() => {
    setCollapsed((v) => {
      const next = !v;
      try {
        window.localStorage.setItem(COLLAPSE_KEY, next ? "1" : "0");
      } catch {
        /* ignore */
      }
      return next;
    });
  }, []);

  // Hooks run before this component's early returns, so this effect fires on
  // marketing pages too — including /login. It therefore uses probeMe(), where
  // a 401 means "nobody is signed in" rather than "the session expired".
  // api.me() here would refresh, fail, expire, navigate to /login, and probe
  // again in a loop.
  useEffect(() => {
    // Never probe from the sign-in pages. The effect below clears the store on
    // those routes, so a probe that found a session would set a user, that
    // would be cleared, and this effect would fire again — a request loop
    // against /api/auth/me for as long as the page stayed open. Middleware
    // already redirects a signed-in visitor away from both, so there is nothing
    // here worth asking about.
    if (isSignInPage) {
      setAssumeSignedIn(false);
      return;
    }
    if (user) return;
    let cancelled = false;
    (async () => {
      const me = await api.probeMe().catch(() => null);
      if (cancelled) return;
      if (me) setUser(me);
      else setAssumeSignedIn(false);
    })();
    return () => {
      cancelled = true;
    };
  }, [user, setUser, isSignInPage]);

  useEffect(() => {
    if (!user || subscription) return;
    let cancelled = false;
    (async () => {
      try {
        const s = await api.getCurrentSubscription();
        if (!cancelled) setSubscription(s);
      } catch {
        /* ignore */
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [user, subscription, setSubscription]);

  // Close the drawer on route change and lock body scroll while it's open.
  useEffect(() => {
    setMobileOpen(false);
  }, [pathname]);

  useEffect(() => {
    if (!mobileOpen) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setMobileOpen(false);
    }
    document.addEventListener("keydown", onKey);
    return () => {
      document.body.style.overflow = prev;
      document.removeEventListener("keydown", onKey);
    };
  }, [mobileOpen]);

  const logout = useCallback(async () => {
    await api.logout();
    setUser(null);
    setSubscription(null);
    window.location.assign("/login");
  }, [setUser, setSubscription]);

  // Wipe client state the moment a session expires, before the redirect
  // fires. Without this the stores briefly hold a signed-in user for a
  // now-dead session, which can flash protected UI on the way out.
  useEffect(() => {
    return onSessionExpired(() => {
      setUser(null);
      setSubscription(null);
    });
  }, [setUser, setSubscription]);

  // Landing on the sign-in or sign-up page means the session is over, so the
  // store should stop describing one.
  //
  // Fixing the chrome alone would have left the app *believing* somebody was
  // signed in while showing them a login form — the session monitor below would
  // keep renewing a dead token, and any soft navigation would render protected
  // UI before the server got a word in. Restricted to these two routes because
  // middleware already turns a genuinely signed-in visitor away from both;
  // /onboarding is an auth route too, but a real session is exactly what it
  // needs.
  useEffect(() => {
    if (isSignInPage && user) {
      setUser(null);
      setSubscription(null);
    }
  }, [isSignInPage, user, setUser, setSubscription]);

  // Activity tracking. Keeps the token renewed for as long as someone is
  // actually using the app, and ends the session once they stop. Only runs
  // for signed-in users — there's no session to nurse otherwise.
  useEffect(() => {
    if (isSignInPage) return;
    if (!user && !assumeSignedIn) return;
    return startSessionMonitor();
  }, [user, assumeSignedIn, isSignInPage]);

  /* ---- Chrome-less routes ---- */
  if (NO_CHROME_PATTERNS.some((re) => re.test(pathname))) {
    return <>{children}</>;
  }

  /* ---- Public routes ---- */
  const isAuthPage = AUTH_PATTERNS.some((re) => re.test(pathname));
  const isPublic = PUBLIC_PATTERNS.some((re) => re.test(pathname));
  const signedOut = !user && !assumeSignedIn;

  // `isAuthPage` is deliberately unconditional — not `isAuthPage && !user`.
  // That is the whole fix: on a sign-in page the route decides, because the
  // store can still be holding the person who just signed out.
  if (isAuthPage || (isPublic && !user) || signedOut) {
    return (
      <>
        <MarketingHeader />
        {children}
      </>
    );
  }

  /* ---- Signed-in app shell ---- */
  const loading = !user && assumeSignedIn;
  const title = pageTitle(pathname);

  return (
    <>
      {/* Sibling of the shell, not a child — inside the flex row it would
          become a column alongside the sidebar. */}
      <SessionTimeoutDialog />

      <div className="flex min-h-screen bg-muted/30">
      {/* Desktop sidebar — sticky so it stays put while content scrolls. */}
      <aside
        className={cn(
          // Height and offset both account for the manage-as banner. `h-screen`
          // under a banner is a full viewport *starting below one*, so the
          // sidebar overran the bottom of the window and took its account menu
          // with it — the one control that lives at the very bottom.
          "sticky top-[var(--banner-h,0px)] h-app-screen",
          "hidden shrink-0 border-r border-border/60 bg-card transition-[width] duration-200 ease-out lg:block",
          collapsed ? "w-[68px]" : "w-64",
        )}
      >
        <AppSidebar
          pathname={pathname}
          user={user}
          collapsed={collapsed}
          onToggleCollapsed={toggleCollapsed}
          loading={loading}
          onLogout={logout}
        />
      </aside>

      {/* Mobile drawer */}
      {mobileOpen && (
        <div className="fixed inset-0 z-50 lg:hidden">
          <div
            className="absolute inset-0 bg-foreground/40 backdrop-blur-sm"
            onClick={() => setMobileOpen(false)}
            aria-hidden
          />
          <div className="absolute inset-y-0 left-0 w-[min(17rem,85vw)] border-r border-border/60 shadow-xl">
            <button
              type="button"
              onClick={() => setMobileOpen(false)}
              aria-label="Close menu"
              className="absolute right-3 top-4 z-10 inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
            >
              <X className="h-4 w-4" />
            </button>
            <AppSidebar
              variant="mobile"
              pathname={pathname}
              user={user}
              loading={loading}
              onLogout={logout}
              onNavigate={() => setMobileOpen(false)}
            />
          </div>
        </div>
      )}

      {/* Content column */}
      <div className="flex min-w-0 flex-1 flex-col">
        {/* Mobile top bar — the only header left on small screens. */}
        <header className="sticky top-[var(--banner-h,0px)] z-30 flex h-14 shrink-0 items-center gap-3 border-b border-border/60 bg-background/85 px-4 backdrop-blur supports-[backdrop-filter]:bg-background/70 lg:hidden">
          <button
            type="button"
            onClick={() => setMobileOpen(true)}
            aria-label="Open menu"
            className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-border/70 text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
          >
            <Menu className="h-4 w-4" />
          </button>
          <span className="truncate text-sm font-semibold tracking-tight">
            {title}
          </span>
          <div className="ml-auto flex items-center gap-2">
            {/* The mobile sidebar is behind a menu button, so the chip has to
                appear here too — otherwise on a phone there is no persistent
                sign of which pharmacy is current. */}
            <WorkspaceSwitcher />
            <ThemeToggle />
          </div>
        </header>

        {/* Only shows for somebody who skipped onboarding, and only until they
            finish it. Inside the content column rather than above the whole
            shell so it scrolls away with the page — it is a reminder, not a
            warning about the current session like the manage-as banner. */}
        <FinishSetupNotice />

        {/* Plain div, not <main> — the pages each render their own <main>
            and nesting landmarks is invalid HTML. */}
        <div className="min-w-0 flex-1">{children}</div>
      </div>
      </div>
    </>
  );
}

/** Best-effort label for the mobile bar — falls back to the brand name. */
function pageTitle(pathname: string) {
  for (const group of NAV_GROUPS) {
    for (const item of group.items) {
      if (isActive(pathname, item.href)) return item.label;
    }
  }
  if (pathname.startsWith("/profile")) return "Profile";
  return "Marketing25";
}

/* -------------------- Marketing header -------------------- */

function MarketingHeader() {
  return (
    <header className="sticky top-[var(--banner-h,0px)] z-30 flex h-14 w-full items-center gap-4 border-b border-border/40 bg-background/80 px-4 backdrop-blur">
      <Link
        href="/"
        className="flex items-center gap-2 font-semibold tracking-tight"
      >
        <LogoMark className="h-7 w-7" />
        Marketing25
      </Link>
      <nav className="ml-6 hidden items-center gap-4 text-sm text-muted-foreground sm:flex">
        <Link href="/pricing" className="transition-colors hover:text-foreground">
          Pricing
        </Link>
        <Link
          href="/templates"
          className="transition-colors hover:text-foreground"
        >
          Templates
        </Link>
      </nav>
      <div className="ml-auto flex items-center gap-2">
        <ThemeToggle />
        <Button asChild size="sm" variant="ghost">
          <Link href="/login">Sign in</Link>
        </Button>
        <Button asChild size="sm" className="bg-brand-gradient text-primary-foreground">
          <Link href="/register">Get started</Link>
        </Button>
      </div>
    </header>
  );
}

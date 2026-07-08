"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import {
  ChevronDown,
  LogOut,
  Menu,
  Pill,
  Sparkles,
  UserCircle2,
  X,
} from "lucide-react";
import type { SubscriptionTier } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { ThemeToggle } from "@/components/theme-toggle";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import { useSubscriptionStore } from "@/store/subscription-store";

/** Pages where no header (or marketing-only header) should render. */
const MARKETING_PATTERNS: RegExp[] = [
  /^\/login\/?$/,
  /^\/register\/?$/,
  /^\/onboarding\/?$/,
];
/** Pages that already ship their own nav — don't render the global header. */
const NO_HEADER_PATTERNS: RegExp[] = [
  /^\/$/,                        // landing has <LandingNav />
  /^\/posters\/[^/]+\/?$/,       // poster editor has its own toolbar
];

const NAV = [
  { href: "/dashboard", label: "Dashboard" },
  { href: "/templates", label: "Templates" },
  { href: "/posters", label: "My Posters" },
  { href: "/patients", label: "Patients" },
  { href: "/campaigns", label: "Campaigns" },
  { href: "/brand-kit", label: "Brand Kit" },
  { href: "/pricing", label: "Pricing" },
] as const;

export function SiteHeader({ initialSignedIn = false }: { initialSignedIn?: boolean }) {
  const pathname = usePathname() ?? "";
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const subscription = useSubscriptionStore((s) => s.current);
  const setSubscription = useSubscriptionStore((s) => s.setCurrent);
  // Optimistically treat the visitor as signed in until the /api/auth/me
  // response arrives. The server passes this from cookies, so the FIRST paint
  // already matches the eventual state instead of flashing MarketingHeader.
  const [assumeSignedIn, setAssumeSignedIn] = useState(initialSignedIn);

  useEffect(() => {
    if (user) return;
    let cancelled = false;
    (async () => {
      try {
        const me = await api.me();
        if (!cancelled) setUser(me);
      } catch {
        // Fetch failed → clear the optimistic "signed in" flag so we can fall
        // back to the marketing header.
        if (!cancelled) setAssumeSignedIn(false);
      }
    })();
    return () => { cancelled = true; };
  }, [user, setUser]);

  useEffect(() => {
    if (!user || subscription) return;
    let cancelled = false;
    (async () => {
      try {
        const s = await api.getCurrentSubscription();
        if (!cancelled) setSubscription(s);
      } catch {
        // ignore
      }
    })();
    return () => { cancelled = true; };
  }, [user, subscription, setSubscription]);

  if (NO_HEADER_PATTERNS.some((re) => re.test(pathname))) return null;

  const isMarketing = MARKETING_PATTERNS.some((re) => re.test(pathname));
  // Marketing pages always get the marketing header. Outside of those,
  // unauthenticated users (e.g. guests browsing /templates) also get it —
  // showing the protected nav would just bounce them through login.
  if (isMarketing && !user) return <MarketingHeader />;

  // If we have a valid session cookie, render the app-header shell
  // immediately even before /api/auth/me resolves — that's what prevents the
  // marketing-header flash on hard refresh.
  if (!user && !assumeSignedIn) return <MarketingHeader />;

  return (
    <AppHeader
      pathname={pathname}
      user={user}
      setUser={setUser}
      setSubscription={setSubscription}
      loading={!user && assumeSignedIn}
    />
  );
}

/* -------------------- Marketing variant -------------------- */

function MarketingHeader() {
  return (
    <header className="sticky top-0 z-30 flex h-14 w-full items-center gap-4 border-b border-border/40 bg-background/80 px-4 backdrop-blur">
      <Link href="/" className="flex items-center gap-2 font-semibold tracking-tight">
        <span className="flex h-7 w-7 items-center justify-center rounded-lg bg-gradient-to-br from-primary to-emerald-500 text-primary-foreground">
          <Pill className="h-4 w-4" />
        </span>
        Sonar Marketing25
      </Link>
      <div className="ml-auto flex items-center gap-2">
        <ThemeToggle />
        <Button asChild size="sm" variant="ghost">
          <Link href="/login">Sign in</Link>
        </Button>
        <Button asChild size="sm" className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground">
          <Link href="/register">Get started</Link>
        </Button>
      </div>
    </header>
  );
}

/* -------------------- Logged-in app header -------------------- */

function AppHeader({
  pathname,
  user,
  setUser,
  setSubscription,
  loading = false,
}: {
  pathname: string;
  user: ReturnType<typeof useAuthStore.getState>["user"];
  setUser: ReturnType<typeof useAuthStore.getState>["setUser"];
  setSubscription: ReturnType<typeof useSubscriptionStore.getState>["setCurrent"];
  loading?: boolean;
}) {
  const [mobileOpen, setMobileOpen] = useState(false);

  async function logout() {
    await api.logout();
    setUser(null);
    setSubscription(null);
    window.location.assign("/login");
  }

  return (
    <header className="sticky top-0 z-30 w-full border-b border-border/60 bg-background/85 backdrop-blur supports-[backdrop-filter]:bg-background/65">
      <div className="flex h-14 items-center gap-3 px-4 lg:px-6">
        <Link href="/dashboard" className="flex items-center gap-2 font-semibold tracking-tight">
          <span className="flex h-7 w-7 items-center justify-center rounded-lg bg-gradient-to-br from-primary to-emerald-500 text-primary-foreground shadow-sm">
            <Pill className="h-4 w-4" />
          </span>
          <span className="hidden sm:inline">Sonar Marketing25</span>
        </Link>

        <Separator orientation="vertical" className="hidden h-6 lg:block" />

        {/* Desktop nav */}
        <nav className="ml-1 hidden flex-1 items-center gap-0.5 lg:flex">
          {NAV.map((item) => (
            <NavLink key={item.href} href={item.href} label={item.label} pathname={pathname} />
          ))}
        </nav>

        {/* Mobile menu trigger */}
        <button
          type="button"
          aria-label={mobileOpen ? "Close menu" : "Open menu"}
          onClick={() => setMobileOpen((v) => !v)}
          className="ml-auto inline-flex h-9 w-9 items-center justify-center rounded-md border border-border/70 text-muted-foreground hover:bg-accent hover:text-accent-foreground lg:hidden"
        >
          {mobileOpen ? <X className="h-4 w-4" /> : <Menu className="h-4 w-4" />}
        </button>

        <div className="ml-auto hidden items-center gap-2 lg:flex">
          {user && <TierBadge tier={user.tier} />}
          <ThemeToggle />
          {user ? (
            <UserMenu user={user} onLogout={logout} />
          ) : loading ? (
            // Placeholder — same footprint as UserMenu so the layout doesn't
            // shift when the user data arrives a moment later.
            <div
              aria-hidden
              className="h-8 w-32 animate-pulse rounded-full border border-border/60 bg-muted/50"
            />
          ) : (
            <Button asChild size="sm" variant="outline">
              <Link href="/login">Sign in</Link>
            </Button>
          )}
        </div>
      </div>

      {/* Mobile nav drawer */}
      {mobileOpen && (
        <div className="border-t border-border/60 bg-background/95 lg:hidden">
          <nav className="grid gap-0.5 px-2 py-3">
            {NAV.map((item) => (
              <Link
                key={item.href}
                href={item.href}
                onClick={() => setMobileOpen(false)}
                className={cn(
                  "rounded-md px-3 py-2 text-sm font-medium transition-colors",
                  isActive(pathname, item.href)
                    ? "bg-secondary text-secondary-foreground"
                    : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
                )}
              >
                {item.label}
              </Link>
            ))}
          </nav>
          <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border/60 px-4 py-3">
            <div className="flex items-center gap-2">
              {user && <TierBadge tier={user.tier} />}
              <ThemeToggle />
            </div>
            {user ? (
              <div className="flex items-center gap-2">
                <Button asChild size="sm" variant="ghost">
                  <Link href="/profile">
                    <UserCircle2 className="mr-1.5 h-4 w-4" />
                    Profile
                  </Link>
                </Button>
                <Button size="sm" variant="ghost" onClick={logout}>
                  <LogOut className="mr-1.5 h-4 w-4" />
                  Sign out
                </Button>
              </div>
            ) : loading ? (
              <div aria-hidden className="h-8 w-24 animate-pulse rounded-md bg-muted/50" />
            ) : (
              <Button asChild size="sm" variant="outline">
                <Link href="/login">Sign in</Link>
              </Button>
            )}
          </div>
        </div>
      )}
    </header>
  );
}

function NavLink({
  href,
  label,
  pathname,
}: {
  href: string;
  label: string;
  pathname: string;
}) {
  const active = isActive(pathname, href);
  return (
    <Link
      href={href}
      className={cn(
        "relative rounded-md px-3 py-1.5 text-sm font-medium transition-colors",
        active
          ? "text-foreground"
          : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
      )}
    >
      {label}
      {active && (
        <span
          aria-hidden
          className="absolute inset-x-3 -bottom-[15px] h-0.5 rounded-full bg-gradient-to-r from-primary to-emerald-500"
        />
      )}
    </Link>
  );
}

function isActive(pathname: string, href: string) {
  if (href === "/dashboard") return pathname === "/dashboard";
  return pathname === href || pathname.startsWith(`${href}/`);
}

/* -------------------- User avatar menu -------------------- */

function UserMenu({
  user,
  onLogout,
}: {
  user: { displayName: string; email: string };
  onLogout: () => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onClick(e: MouseEvent) {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onClick);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onClick);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  const initials = (user.displayName || user.email).slice(0, 2).toUpperCase();

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className="inline-flex items-center gap-2 rounded-full border border-border/60 bg-background px-1.5 py-1 text-sm transition hover:bg-accent"
      >
        <span className="flex h-7 w-7 items-center justify-center rounded-full bg-gradient-to-br from-primary to-emerald-500 text-xs font-semibold text-primary-foreground">
          {initials}
        </span>
        <span className="hidden max-w-[120px] truncate text-muted-foreground md:inline">
          {user.displayName}
        </span>
        <ChevronDown className="h-3.5 w-3.5 text-muted-foreground" />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 mt-2 w-56 overflow-hidden rounded-lg border border-border/60 bg-popover text-popover-foreground shadow-lg"
        >
          <div className="border-b border-border/60 px-3 py-2 text-xs">
            <p className="truncate font-medium text-foreground">{user.displayName}</p>
            <p className="truncate text-muted-foreground">{user.email}</p>
          </div>
          <Link
            href="/profile"
            role="menuitem"
            onClick={() => setOpen(false)}
            className="flex items-center gap-2 px-3 py-2 text-sm hover:bg-accent"
          >
            <UserCircle2 className="h-4 w-4 text-muted-foreground" />
            Profile
          </Link>
          <Link
            href="/pricing"
            role="menuitem"
            onClick={() => setOpen(false)}
            className="flex items-center gap-2 px-3 py-2 text-sm hover:bg-accent"
          >
            <Sparkles className="h-4 w-4 text-muted-foreground" />
            Upgrade plan
          </Link>
          <button
            role="menuitem"
            onClick={() => { setOpen(false); onLogout(); }}
            className="flex w-full items-center gap-2 border-t border-border/60 px-3 py-2 text-sm hover:bg-accent"
          >
            <LogOut className="h-4 w-4 text-muted-foreground" />
            Sign out
          </button>
        </div>
      )}
    </div>
  );
}

/* -------------------- Tier badge -------------------- */

function TierBadge({ tier }: { tier: SubscriptionTier }) {
  if (tier === "Pro") {
    return (
      <Badge className="gap-1 bg-primary/15 text-primary hover:bg-primary/15">
        <Sparkles className="h-3 w-3" />
        Pro
      </Badge>
    );
  }
  if (tier === "Starter") {
    return <Badge variant="secondary">Starter</Badge>;
  }
  return (
    <Link href="/pricing" title="Upgrade your plan">
      <Badge variant="outline" className="cursor-pointer hover:bg-accent">
        Free
      </Badge>
    </Link>
  );
}

"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect } from "react";
import { LogOut, Sparkles } from "lucide-react";
import type { SubscriptionTier } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { ThemeToggle } from "@/components/theme-toggle";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import { useSubscriptionStore } from "@/store/subscription-store";

const NAV = [
  { href: "/templates", label: "Templates" },
  { href: "/posters", label: "My Posters" },
  { href: "/brand-kit", label: "Brand Kit" },
  { href: "/pricing", label: "Pricing" },
] as const;

const HIDE_PATTERNS: RegExp[] = [
  /^\/$/,
  /^\/login\/?$/,
  /^\/register\/?$/,
  /^\/posters\/[^/]+\/?$/,
];

export function SiteHeader() {
  const pathname = usePathname() ?? "";
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const subscription = useSubscriptionStore((s) => s.current);
  const setSubscription = useSubscriptionStore((s) => s.setCurrent);

  useEffect(() => {
    if (user) return;
    let cancelled = false;
    (async () => {
      try {
        const me = await api.me();
        if (!cancelled) setUser(me);
      } catch {
        // not signed in
      }
    })();
    return () => {
      cancelled = true;
    };
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
    return () => {
      cancelled = true;
    };
  }, [user, subscription, setSubscription]);

  if (HIDE_PATTERNS.some((re) => re.test(pathname))) return null;

  async function logout() {
    await api.logout();
    setUser(null);
    setSubscription(null);
    window.location.assign("/login");
  }

  return (
    <header className="sticky top-0 z-30 flex h-14 w-full items-center gap-4 border-b bg-background/90 px-4 backdrop-blur">
      <Link href="/" className="font-semibold tracking-tight">
        Pharmacy Poster
      </Link>
      <Separator orientation="vertical" className="h-6" />
      <nav className="flex items-center gap-1">
        {NAV.map((item) => {
          const active =
            pathname === item.href || pathname.startsWith(`${item.href}/`);
          return (
            <Link
              key={item.href}
              href={item.href}
              className={cn(
                "rounded-md px-3 py-1.5 text-sm transition-colors",
                active
                  ? "bg-secondary text-secondary-foreground"
                  : "text-muted-foreground hover:bg-accent hover:text-accent-foreground",
              )}
            >
              {item.label}
            </Link>
          );
        })}
      </nav>

      <div className="ml-auto flex items-center gap-2">
        <ThemeToggle />
        {user ? (
          <>
            <TierBadge tier={user.tier} />
            <span className="hidden text-sm text-muted-foreground sm:inline">
              {user.displayName}
            </span>
            <Button
              size="sm"
              variant="ghost"
              onClick={logout}
              aria-label="Sign out"
              title="Sign out"
            >
              <LogOut className="mr-1.5 h-4 w-4" />
              Sign out
            </Button>
          </>
        ) : (
          <Button asChild size="sm" variant="outline">
            <Link href="/login">Sign in</Link>
          </Button>
        )}
      </div>
    </header>
  );
}

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

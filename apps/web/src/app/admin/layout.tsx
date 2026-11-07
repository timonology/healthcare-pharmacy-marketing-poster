"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ShieldAlert, Loader2 } from "lucide-react";
import { isStaffRole } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";

/**
 * Client-side guard for /admin/*.
 *
 * This is a convenience, not a security control. It exists so a pharmacy user
 * who follows a stray link sees a clear message instead of a page of failed
 * requests. Every admin endpoint independently enforces role and per-row scope
 * server-side, so bypassing this in devtools reveals an empty shell and a
 * series of 403s.
 */
export default function AdminLayout({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const pathname = usePathname() ?? "";
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const [checking, setChecking] = useState(!user);

  useEffect(() => {
    if (user) {
      setChecking(false);
      return;
    }
    let cancelled = false;
    (async () => {
      // probeMe rather than me(): a 401 here is handled by authedFetch's
      // expiry path already. Calling router.replace as well would race two
      // navigations to /login and flash the page.
      const me = await api.probeMe().catch(() => null);
      if (cancelled) return;
      if (me) setUser(me);
      else router.replace("/login");
      setChecking(false);
    })();
    return () => {
      cancelled = true;
    };
  }, [user, setUser, router]);

  if (checking) {
    return (
      <div className="flex min-h-[60vh] items-center justify-center">
        <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    );
  }

  if (!isStaffRole(user?.role)) {
    return (
      <main className="container flex max-w-md flex-col items-center py-24 text-center">
        <ShieldAlert className="h-10 w-10 text-muted-foreground" />
        <h1 className="mt-6 text-xl font-semibold tracking-tight">
          You don&apos;t have access to this area
        </h1>
        <p className="mt-2 text-sm text-muted-foreground">
          The admin console is limited to platform administrators.
        </p>
        <Button asChild className="mt-6">
          <Link href="/dashboard">Back to dashboard</Link>
        </Button>
      </main>
    );
  }

  const isSuperAdmin = user?.role === "SuperAdmin";

  const tabs = [
    { href: "/admin", label: "Overview", exact: true },
    { href: "/admin/users", label: "Users" },
    { href: "/admin/pharmacies", label: "Pharmacies" },
    { href: "/admin/subscriptions", label: "Subscriptions" },
    ...(isSuperAdmin ? [{ href: "/admin/admins", label: "Administrators" }] : []),
    // SuperAdmin only: inviting creates an account and emails a sign-in link.
    // Named for both things it does — "Invitations" alone hid the CSV import
    // from the people looking for it.
    ...(isSuperAdmin ? [{ href: "/admin/invitations", label: "Invite & import" }] : []),
    // SuperAdmin only: a general group appears in every pharmacy's list.
    ...(isSuperAdmin ? [{ href: "/admin/global-groups", label: "General groups" }] : []),
    // Ordinary Admins see this too: reviewing access requests is support work,
    // and the API allows any staff role. Restricting the tab to SuperAdmin
    // would hide a queue they are permitted — and expected — to clear.
    { href: "/admin/access-requests", label: "Access requests" },
    { href: "/admin/audit", label: "Audit log" },
    // Hidden from ordinary Admins because every setting behind it is global.
    // The tab is a courtesy — the API refuses them regardless of the nav.
    ...(isSuperAdmin ? [{ href: "/admin/settings", label: "Settings" }] : []),
  ];

  return (
    <main className="container max-w-7xl py-8">
      <header className="mb-6">
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-2xl font-semibold tracking-tight">Admin console</h1>
          <span
            className={cn(
              "rounded-full px-2 py-0.5 text-[11px] font-medium",
              isSuperAdmin
                ? "bg-primary/10 text-primary"
                : "bg-secondary text-secondary-foreground",
            )}
          >
            {user?.role}
          </span>
        </div>
        <p className="mt-1 text-sm text-muted-foreground">
          {isSuperAdmin
            ? "Full platform access."
            : "You can manage every pharmacy account. Administrator accounts are SuperAdmin-only."}
        </p>
      </header>

      <nav className="mb-6 flex flex-wrap gap-1 border-b border-border/60">
        {tabs.map((tab) => {
          const active = tab.exact
            ? pathname === tab.href
            : pathname === tab.href || pathname.startsWith(`${tab.href}/`);
          return (
            <Link
              key={tab.href}
              href={tab.href}
              className={cn(
                "-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors",
                active
                  ? "border-primary text-foreground"
                  : "border-transparent text-muted-foreground hover:text-foreground",
              )}
            >
              {tab.label}
            </Link>
          );
        })}
      </nav>

      {children}
    </main>
  );
}

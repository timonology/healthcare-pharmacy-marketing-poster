"use client";

import Link from "next/link";
import {
  ArrowLeft,
  BarChart3,
  Building2,
  ChevronsLeft,
  ChevronsRight,
  CreditCard,
  Gauge,
  Images,
  LayoutDashboard,
  LayoutTemplate,
  LogOut,
  Megaphone,
  Monitor,
  Palette,
  ScrollText,
  Share2,
  ShieldCheck,
  Sparkles,
  UserCircle2,
  UserCog,
  Users,
  Video,
  type LucideIcon,
} from "lucide-react";
import { isStaffRole, type SubscriptionTier, type UserRole } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { ThemeToggle } from "@/components/theme-toggle";
import { WorkspaceSwitcher } from "@/components/site/WorkspaceSwitcher";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";
import { LogoMark } from "@/components/brand/LogoMark";

/* ------------------------------------------------------------------ *
 * Navigation model
 *
 * Grouped rather than flat: with 9+ destinations a single list reads as
 * an undifferentiated wall. The groups mirror the user's mental model —
 * make something → find it again → who it goes to → how it performed.
 * ------------------------------------------------------------------ */

export type NavItem = {
  href: string;
  label: string;
  icon: LucideIcon;
};

/**
 * Navigation for the admin console.
 *
 * When someone is inside /admin the sidebar switches to this entirely rather
 * than appending it to the pharmacy nav. Mixing the two makes it ambiguous
 * which hat you're wearing — and "am I about to change my own pharmacy's data
 * or a customer's?" is not a question an admin should have to ask.
 *
 * Hiding these is presentation, not protection. /admin/* has its own layout
 * guard and every endpoint behind it is role-checked server-side.
 */
export const ADMIN_NAV_GROUPS = (isSuperAdmin: boolean): NavGroup[] => [
  {
    label: "Console",
    items: [
      { href: "/admin", label: "Overview", icon: Gauge },
      { href: "/admin/users", label: "Users", icon: UserCog },
      { href: "/admin/pharmacies", label: "Pharmacies", icon: Building2 },
    ],
  },
  {
    label: "Content",
    items: [
      { href: "/admin/templates", label: "Templates", icon: LayoutTemplate },
    ],
  },
  {
    label: "Insights",
    items: [
      { href: "/admin/analytics", label: "Analytics", icon: BarChart3 },
    ],
  },
  {
    label: "Commercial",
    items: [
      { href: "/admin/subscriptions", label: "Subscriptions", icon: CreditCard },
    ],
  },
  {
    label: "Governance",
    items: [
      ...(isSuperAdmin
        ? [{ href: "/admin/admins", label: "Administrators", icon: ShieldCheck }]
        : []),
      { href: "/admin/audit", label: "Audit log", icon: ScrollText },
    ],
  },
];

export interface NavGroup {
  label: string;
  items: NavItem[];
}

export const NAV_GROUPS: { label: string; items: NavItem[] }[] = [
  {
    label: "Create",
    items: [
      { href: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
      { href: "/templates", label: "Templates", icon: LayoutTemplate },
    ],
  },
  {
    label: "Library",
    items: [
      { href: "/posters", label: "My Posters", icon: Images },
      { href: "/videos", label: "Videos", icon: Video },
    ],
  },
  {
    label: "Audience",
    items: [
      { href: "/patients", label: "Patients", icon: Users },
      { href: "/campaigns", label: "Campaigns", icon: Megaphone },
      { href: "/social", label: "Social & Ads", icon: Share2 },
      { href: "/displays", label: "Screens", icon: Monitor },
    ],
  },
  {
    label: "Insights",
    items: [{ href: "/analytics", label: "Analytics", icon: BarChart3 }],
  },
  {
    label: "Workspace",
    items: [
      { href: "/brand-kit", label: "Brand Kit", icon: Palette },
      { href: "/pharmacies", label: "Branches", icon: Building2 },
      { href: "/pricing", label: "Plan & Billing", icon: Sparkles },
    ],
  },
];

export function isActive(pathname: string, href: string) {
  // /dashboard is an exact match — otherwise it would light up for every
  // nested route that happens to share the prefix.
  if (href === "/dashboard") return pathname === "/dashboard";
  return pathname === href || pathname.startsWith(`${href}/`);
}

/* ------------------------------------------------------------------ *
 * Sidebar
 * ------------------------------------------------------------------ */

export function AppSidebar({
  pathname,
  user,
  collapsed,
  onToggleCollapsed,
  onNavigate,
  loading = false,
  onLogout,
  variant = "desktop",
}: {
  pathname: string;
  user: { displayName: string; email: string; tier: SubscriptionTier; role: UserRole } | null;
  collapsed?: boolean;
  onToggleCollapsed?: () => void;
  /** Called after any nav click — the mobile drawer uses this to close. */
  onNavigate?: () => void;
  loading?: boolean;
  onLogout: () => void;
  variant?: "desktop" | "mobile";
}) {
  // The mobile drawer is always full-width; only the desktop rail collapses.
  const isCollapsed = variant === "desktop" && !!collapsed;

  const isStaff = isStaffRole(user?.role);
  const inAdmin = pathname === "/admin" || pathname.startsWith("/admin/");
  const adminMode = isStaff && inAdmin;

  const groups = adminMode
    ? ADMIN_NAV_GROUPS(user?.role === "SuperAdmin")
    : NAV_GROUPS;

  return (
    <div className="flex h-full flex-col gap-1 bg-card">
      {/* ---- Brand ---- */}
      <div
        className={cn(
          "flex h-16 shrink-0 items-center border-b border-border/60",
          isCollapsed ? "justify-center px-2" : "gap-2.5 px-4",
        )}
      >
        <Link
          href="/dashboard"
          onClick={onNavigate}
          className="flex items-center gap-2.5 overflow-hidden font-semibold tracking-tight"
        >
          <LogoMark className="h-8 w-8" />
          {!isCollapsed && (
            <span className="truncate text-[15px] leading-none">
              Marketing25
            </span>
          )}
        </Link>

        {variant === "desktop" && !isCollapsed && (
          <button
            type="button"
            onClick={onToggleCollapsed}
            aria-label="Collapse sidebar"
            title="Collapse sidebar"
            className="ml-auto inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
          >
            <ChevronsLeft className="h-4 w-4" />
          </button>
        )}
      </div>

      {/* Which pharmacy this session is working as.
          High in the chrome on purpose: everything below it — posters,
          campaigns, contacts — belongs to whichever pharmacy this names, and
          until now nothing on screen said which one that was. Renders nothing
          for somebody with a single pharmacy who is inside it. */}
      {!isCollapsed && (
        <div className="px-3 pt-3">
          <WorkspaceSwitcher />
        </div>
      )}

      {/* Expand affordance for the collapsed rail — sits under the mark so
          the click target never overlaps the logo link. */}
      {variant === "desktop" && isCollapsed && (
        <button
          type="button"
          onClick={onToggleCollapsed}
          aria-label="Expand sidebar"
          title="Expand sidebar"
          className="mx-auto mt-2 inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
        >
          <ChevronsRight className="h-4 w-4" />
        </button>
      )}

      {/* ---- Mode switch ----
          Only rendered for staff. Doubles as the signal for which mode you're
          in: an admin acting on a customer's account should never be in any
          doubt that that's what they're doing. */}
      {isStaff && (
        <div className={cn("shrink-0 pt-3", isCollapsed ? "px-2" : "px-3")}>
          <Link
            href={adminMode ? "/dashboard" : "/admin"}
            onClick={onNavigate}
            title={isCollapsed ? (adminMode ? "Back to app" : "Admin console") : undefined}
            className={cn(
              "flex items-center rounded-lg border text-sm font-medium transition-colors",
              isCollapsed ? "h-10 w-10 justify-center" : "gap-2.5 px-3 py-2.5",
              adminMode
                ? "border-border/60 text-muted-foreground hover:bg-accent hover:text-foreground"
                : "border-primary/30 bg-primary/5 text-primary hover:bg-primary/10",
            )}
          >
            {adminMode ? (
              <ArrowLeft className="h-[18px] w-[18px] shrink-0" />
            ) : (
              <ShieldCheck className="h-[18px] w-[18px] shrink-0" />
            )}
            {!isCollapsed && (
              <span className="min-w-0 flex-1 truncate">
                {adminMode ? "Back to my pharmacy" : "Admin console"}
              </span>
            )}
          </Link>
        </div>
      )}

      {/* ---- Nav ---- */}
      <nav
        className={cn(
          "flex-1 space-y-5 overflow-y-auto py-4",
          isCollapsed ? "px-2" : "px-3",
        )}
      >
        {groups.map((group) => (
          <div key={group.label} className="space-y-1">
            {!isCollapsed && (
              <p className="px-3 pb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground/70">
                {group.label}
              </p>
            )}
            {group.items.map((item) => {
              const active = isActive(pathname, item.href);
              const Icon = item.icon;
              return (
                <Link
                  key={item.href}
                  href={item.href}
                  onClick={onNavigate}
                  title={isCollapsed ? item.label : undefined}
                  aria-current={active ? "page" : undefined}
                  className={cn(
                    "group relative flex items-center rounded-lg text-sm font-medium transition-all",
                    isCollapsed
                      ? "h-10 w-10 justify-center"
                      : "gap-3 px-3 py-2",
                    active
                      ? "bg-primary/10 text-primary"
                      : "text-muted-foreground hover:bg-accent hover:text-foreground",
                  )}
                >
                  {/* Active rail — a 3px bar hugging the left edge reads as
                      "you are here" faster than colour alone. */}
                  {active && (
                    <span
                      aria-hidden
                      className={cn(
                        "absolute left-0 top-1/2 h-5 w-[3px] -translate-y-1/2 rounded-r-full bg-brand-gradient",
                        isCollapsed && "-left-2",
                      )}
                    />
                  )}
                  <Icon
                    className={cn(
                      "h-[18px] w-[18px] shrink-0 transition-colors",
                      active
                        ? "text-primary"
                        : "text-muted-foreground group-hover:text-foreground",
                    )}
                  />
                  {!isCollapsed && <span className="truncate">{item.label}</span>}
                </Link>
              );
            })}
          </div>
        ))}
      </nav>

      {/* ---- Footer: plan, theme, account ---- */}
      <div
        className={cn(
          "shrink-0 space-y-2 border-t border-border/60 py-3",
          isCollapsed ? "px-2" : "px-3",
        )}
      >
        {!isCollapsed && user && <UpgradeCard tier={user.tier} />}

        <div
          className={cn(
            "flex items-center gap-2",
            isCollapsed && "flex-col-reverse",
          )}
        >
          {user ? (
            <AccountMenu
              user={user}
              collapsed={isCollapsed}
              onLogout={onLogout}
              onNavigate={onNavigate}
            />
          ) : loading ? (
            <div
              aria-hidden
              className={cn(
                "animate-pulse rounded-lg bg-muted/60",
                isCollapsed ? "h-9 w-9" : "h-11 flex-1",
              )}
            />
          ) : null}
          <ThemeToggle />
        </div>
      </div>
    </div>
  );
}

/* ------------------------------------------------------------------ *
 * Plan card — nudges Free users, reassures paid ones.
 * ------------------------------------------------------------------ */

function UpgradeCard({ tier }: { tier: SubscriptionTier }) {
  if (tier === "Pro") {
    return (
      <div className="flex items-center gap-2 rounded-lg border border-primary/25 bg-primary/5 px-3 py-2">
        <Sparkles className="h-3.5 w-3.5 shrink-0 text-primary" />
        <span className="text-xs font-medium text-primary">Pro plan</span>
      </div>
    );
  }

  if (tier === "Starter") {
    return (
      <Link
        href="/pricing"
        className="flex items-center gap-2 rounded-lg border border-border/60 px-3 py-2 transition-colors hover:bg-accent"
      >
        <Badge variant="secondary" className="h-5 px-1.5 text-[10px]">
          Starter
        </Badge>
        <span className="text-xs text-muted-foreground">Upgrade →</span>
      </Link>
    );
  }

  return (
    <Link
      href="/pricing"
      className="block rounded-lg border border-border/60 bg-primary/5 p-3 transition-colors hover:border-primary/40 hover:bg-primary/10"
    >
      <p className="text-xs font-semibold text-foreground">You&apos;re on Free</p>
      <p className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">
        Unlock unlimited posters, video, and campaigns.
      </p>
      <span className="mt-2 inline-flex items-center gap-1 text-[11px] font-medium text-primary">
        <Sparkles className="h-3 w-3" />
        See plans
      </span>
    </Link>
  );
}

/* ------------------------------------------------------------------ *
 * Account menu — opens upward out of the sidebar footer.
 * ------------------------------------------------------------------ */

function AccountMenu({
  user,
  collapsed,
  onLogout,
  onNavigate,
}: {
  user: { displayName: string; email: string };
  collapsed: boolean;
  onLogout: () => void;
  onNavigate?: () => void;
}) {
  const initials = (user.displayName || user.email).slice(0, 2).toUpperCase();

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          title={collapsed ? user.displayName : undefined}
          className={cn(
            "flex items-center rounded-lg transition-colors hover:bg-accent",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            collapsed ? "h-9 w-9 justify-center" : "w-full min-w-0 flex-1 gap-2.5 px-2 py-1.5",
          )}
        >
          <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-brand-gradient text-xs font-semibold text-primary-foreground">
            {initials}
          </span>
          {!collapsed && (
            <span className="min-w-0 flex-1 text-left">
              <span className="block truncate text-xs font-medium text-foreground">
                {user.displayName}
              </span>
              <span className="block truncate text-[11px] text-muted-foreground">
                {user.email}
              </span>
            </span>
          )}
        </button>
      </DropdownMenuTrigger>

      {/* Opens upward: the trigger sits at the bottom of the sidebar, so a menu
          below it would be off-screen. Radix flips automatically when there is
          no room, and `side="top"` makes that the intent rather than luck. */}
      <DropdownMenuContent side="top" align="start" className="w-[14rem]">
        <div className="border-b border-border/60 px-2.5 pb-2 pt-1.5">
          <p className="truncate text-xs font-medium text-foreground">{user.displayName}</p>
          <p className="truncate text-[11px] text-muted-foreground">{user.email}</p>
        </div>

        <DropdownMenuItem asChild>
          <Link href="/profile" onClick={() => onNavigate?.()} className="items-center">
            <UserCircle2 className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            Profile
          </Link>
        </DropdownMenuItem>

        <DropdownMenuItem asChild>
          <Link href="/pricing" onClick={() => onNavigate?.()} className="items-center">
            <Sparkles className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            Plan &amp; billing
          </Link>
        </DropdownMenuItem>

        <DropdownMenuSeparator />

        {/* Theme lives here rather than only in the mobile bar, so it is
            reachable on desktop where there is no top bar at all. */}
        <div className="flex items-center justify-between px-2.5 py-1.5">
          <span className="text-sm">Theme</span>
          <ThemeToggle />
        </div>

        <DropdownMenuSeparator />

        <DropdownMenuItem onSelect={() => onLogout()} className="items-center">
          <LogOut className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
          Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

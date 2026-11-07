"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { type AdminStats, type AuditEvent } from "@acme/shared-types";
import {
  Building2,
  Images,
  ScrollText,
  ShieldCheck,
  Sparkles,
  UserCog,
} from "lucide-react";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import { BarList, DonutChart } from "@/components/admin/AdminCharts";
import {
  EmptyState,
  ErrorNote,
  ListSkeleton,
  Panel,
  StatCard,
  StatCardSkeleton,
  ChartSkeleton,
  formatDate,
} from "@/components/admin/AdminUi";
import { cn } from "@/lib/utils";

/** Staff actions an auditor scans for first: privilege and impersonation. */
const PRIVILEGED = ["admin.impersonate", "admin.user.role", "admin.admin.", "admin.subscription", "admin.credits"];

export default function AdminOverviewPage() {
  const [stats, setStats] = useState<AdminStats | null>(null);
  const [audit, setAudit] = useState<AuditEvent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const role = useAuthStore((s) => s.user?.role);
  const isSuperAdmin = role === "SuperAdmin";

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [s, a] = await Promise.all([
          api.admin.stats(),
          // The feed is secondary — a failure here shouldn't blank the whole
          // dashboard, so it degrades to an empty list.
          // Staff changes from the audit console, which applies the caller's
          // visibility. The legacy feed is SuperAdmin-only and would leave an
          // Admin's dashboard silently empty.
          api.admin
            .auditEvents({ tab: "Staff", stream: "Changes", take: 8 })
            .catch(() => null),
        ]);
        if (cancelled) return;
        setStats(s);
        setAudit(a?.items ?? []);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load.");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (error) return <ErrorNote message={error} />;

  const gbp = (n: number) =>
    `£${n.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;

  // Staff is derived: the API counts admins and super admins separately, and
  // both are staff for the purpose of "who isn't a pharmacy". Total =
  // pharmacies + staff, which is what makes the cards add up against the
  // Pharmacies page.
  const staffCount = (stats?.admins ?? 0) + (stats?.superAdmins ?? 0);

  const planData = [
    { label: "Free", value: stats?.freeTier ?? 0 },
    { label: "Starter", value: stats?.starterTier ?? 0 },
    { label: "Pro", value: stats?.proTier ?? 0 },
  ];

  const outputData = [
    { label: "Posters created", value: stats?.totalPosters ?? 0 },
    { label: "Videos generated", value: stats?.totalVideos ?? 0 },
    { label: "Campaigns sent", value: stats?.totalCampaignsSent ?? 0 },
  ];

  return (
    <div className="space-y-6">
      <QuickActions isSuperAdmin={isSuperAdmin} />

      {/*
        Account composition. "Total users" alone invited the question the
        Pharmacies page then appeared to answer wrongly: that page lists only
        PharmacyUser accounts, so with mostly-staff data it shows far fewer rows
        than the total. Showing the split makes the pages reconcile at a glance.
      */}
      <Section title="Accounts">
        {loading ? (
          <SkeletonRow />
        ) : (
          <>
            <StatCard
              label="Total accounts"
              value={stats?.totalUsers ?? 0}
              hint="Pharmacies + staff"
            />
            {/*
              pharmacyAccounts, not pharmacyUsers — this card has to match the
              row count on the Pharmacies page, and that page lists accounts
              with a pharmacy rather than accounts with the PharmacyUser role.
              The two differ whenever a pharmacy owner also holds a staff role.
            */}
            <StatCard
              label="Pharmacies"
              value={stats?.pharmacyAccounts ?? 0}
              hint={
                stats && stats.pharmacyAccounts > stats.pharmacyUsers
                  ? `${stats.pharmacyAccounts - stats.pharmacyUsers} also hold a staff role`
                  : "Customer accounts"
              }
            />
            <StatCard label="Staff" value={staffCount} hint="Admins + super admins" />
            <StatCard
              label="New this month"
              value={stats?.newUsersThisMonth ?? 0}
              tone={stats && stats.newUsersThisMonth > 0 ? "positive" : "default"}
            />
          </>
        )}
      </Section>

      <Section title="Activity">
        {loading ? (
          <SkeletonRow />
        ) : (
          <>
            <StatCard
              label="Posters created"
              value={stats?.totalPosters ?? 0}
              hint={`${(stats?.postersThisMonth ?? 0).toLocaleString()} this month`}
            />
            <StatCard
              label="Videos generated"
              value={stats?.totalVideos ?? 0}
              hint={`${(stats?.videosThisMonth ?? 0).toLocaleString()} this month`}
            />
            <StatCard
              label="Campaigns sent"
              value={stats?.totalCampaignsSent ?? 0}
              hint={`${(stats?.campaignsSentThisMonth ?? 0).toLocaleString()} this month`}
            />
            <StatCard
              label="Recipients reached"
              value={stats?.recipientsReached ?? 0}
              hint="Messages delivered, all time"
            />
          </>
        )}
      </Section>

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel
          title="Plan distribution"
          description="How pharmacy accounts are spread across the tiers."
        >
          {loading ? (
            <ChartSkeleton />
          ) : (
            <div className="p-4">
              <DonutChart
                data={planData}
                total={stats?.pharmacyAccounts ?? 0}
                centerLabel="pharmacies"
              />
            </div>
          )}
        </Panel>

        <Panel
          title="What's being made"
          description="Total output across the platform."
        >
          {loading ? (
            <ListSkeleton rows={3} />
          ) : (
            <div className="p-4">
              <BarList
                data={outputData}
                emptyMessage="No posters, videos or campaigns yet."
              />
            </div>
          )}
        </Panel>
      </div>

      <Section title="Health">
        {loading ? (
          <SkeletonRow />
        ) : (
          <>
            <StatCard label="Active" value={stats?.activeUsers ?? 0} tone="positive" />
            <StatCard
              label="Deactivated"
              value={stats?.inactiveUsers ?? 0}
              tone={stats && stats.inactiveUsers > 0 ? "warning" : "default"}
            />
            <StatCard
              label="Active last 30 days"
              value={stats?.activeLast30Days ?? 0}
              hint="Signed in recently"
            />
            <StatCard
              label="Est. monthly revenue"
              value={gbp(stats?.estimatedMonthlyRevenueGbp ?? 0)}
              hint="List price, excl. discounts"
            />
          </>
        )}
      </Section>

      {isSuperAdmin && (
        <Section title="Super admin">
          {loading ? (
            <SkeletonRow />
          ) : (
            <>
              <StatCard
                label="Administrators"
                value={stats?.admins ?? 0}
                hint="Excludes super admins"
              />
              <StatCard label="Super admins" value={stats?.superAdmins ?? 0} />
              <StatCard
                label="Videos this month"
                value={stats?.videosThisMonth ?? 0}
              />
              <StatCard
                label="Est. annual run rate"
                value={gbp(stats?.estimatedAnnualRunRateGbp ?? 0)}
                hint="Monthly estimate × 12"
              />
            </>
          )}
        </Section>
      )}

      <Panel
        title="Recent activity"
        description="Recent staff changes you can see."
      >
        {loading ? (
          <ListSkeleton rows={5} />
        ) : audit.length === 0 ? (
          <EmptyState
            icon={ScrollText}
            title="No administrative actions yet"
            message="Changes to accounts, plans and templates will appear here as they happen."
          />
        ) : (
          <>
            <ul className="divide-y divide-border/60">
              {audit.map((entry) => (
                <li
                  key={entry.id}
                  className="flex flex-wrap items-baseline gap-x-2 gap-y-1 px-4 py-2.5 text-sm"
                >
                  <span
                    className={cn(
                      "rounded-full px-2 py-0.5 font-mono text-[11px] font-medium",
                      entry.result !== "ok"
                        ? "bg-destructive/10 text-destructive"
                        : PRIVILEGED.some((p) => entry.action.startsWith(p))
                          ? "bg-primary/10 text-primary"
                          : "bg-muted text-muted-foreground",
                    )}
                  >
                    {entry.action}
                  </span>
                  {entry.entityType && (
                    <span className="text-xs text-muted-foreground">{entry.entityType}</span>
                  )}
                  {entry.meta.detail && (
                    <span className="min-w-0 truncate text-xs text-muted-foreground">
                      — {entry.meta.detail}
                    </span>
                  )}
                  <span className="ml-auto shrink-0 text-xs text-muted-foreground">
                    {entry.actorEmail ?? entry.actorId} · {formatDate(entry.atUtc)}
                  </span>
                </li>
              ))}
            </ul>
            <div className="border-t border-border/60 px-4 py-2 text-right">
              <Link
                href="/admin/audit"
                className="text-xs text-primary hover:underline"
              >
                View full audit log →
              </Link>
            </div>
          </>
        )}
      </Panel>
    </div>
  );
}

/** Consistent heading + 4-up responsive grid for a band of metrics. */
function Section({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section className="space-y-2">
      <h2 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">
        {title}
      </h2>
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">{children}</div>
    </section>
  );
}

function SkeletonRow() {
  return (
    <>
      {[0, 1, 2, 3].map((i) => (
        <StatCardSkeleton key={i} />
      ))}
    </>
  );
}

/**
 * Entry points for the things admins actually come here to do.
 *
 * Placed above the metrics: the dashboard is read for orientation but visited
 * to take an action, and burying the actions under four bands of numbers makes
 * every task start with a scroll.
 */
function QuickActions({ isSuperAdmin }: { isSuperAdmin: boolean }) {
  const actions = [
    { href: "/admin/templates/new", label: "New template", icon: Sparkles, primary: true },
    { href: "/admin/users", label: "Users", icon: UserCog },
    { href: "/admin/pharmacies", label: "Pharmacies", icon: Building2 },
    { href: "/admin/templates", label: "Templates", icon: Images },
    ...(isSuperAdmin
      ? [{ href: "/admin/admins", label: "Administrators", icon: ShieldCheck }]
      : []),
  ];

  return (
    <div className="flex flex-wrap gap-2">
      {actions.map(({ href, label, icon: Icon, primary }) => (
        <Link
          key={href}
          href={href}
          className={cn(
            "inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-xs font-medium transition-colors",
            primary
              ? "border-primary bg-primary text-primary-foreground hover:bg-primary/90"
              : "border-border/60 hover:bg-accent",
          )}
        >
          <Icon className="h-3.5 w-3.5" aria-hidden />
          {label}
        </Link>
      ))}
    </div>
  );
}

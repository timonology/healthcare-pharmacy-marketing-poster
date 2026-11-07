"use client";

import { useCallback, useEffect, useState } from "react";
import type { AdminAnalytics } from "@acme/shared-types";
import { BarChart3 } from "lucide-react";
import dynamic from "next/dynamic";
import { api } from "@/lib/api";
import { BarList } from "@/components/admin/AdminCharts";
import {
  EmptyState,
  ErrorNote,
  ListSkeleton,
  Panel,
  StatCard,
  StatCardSkeleton,
  Skeleton,
  TableEmpty,
  TableLoading,
} from "@/components/admin/AdminUi";
import { cn } from "@/lib/utils";

/**
 * Recharts is ~450KB and only this page uses it, so it loads on demand.
 * ssr:false because the chart measures its container to lay out — there is no
 * width on the server, and rendering it there produces a warning plus a flash
 * of empty space before hydration corrects it.
 */
const ActivityChart = dynamic(
  () => import("@/components/admin/ActivityChart").then((m) => m.ActivityChart),
  {
    ssr: false,
    loading: () => <Skeleton className="h-[280px] w-full" />,
  },
);

const RANGES = [
  { days: 7, label: "7 days" },
  { days: 30, label: "30 days" },
  { days: 90, label: "90 days" },
];

export default function AdminAnalyticsPage() {
  const [days, setDays] = useState(30);
  const [data, setData] = useState<AdminAnalytics | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setData(await api.admin.analytics(days));
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load analytics.");
    } finally {
      setLoading(false);
    }
  }, [days]);

  useEffect(() => {
    void load();
  }, [load]);

  const hasActivity =
    data !== null &&
    data.postersInPeriod + data.videosInPeriod + data.campaignsInPeriod > 0;

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-sm font-medium">Platform analytics</h1>
        <div className="flex gap-1.5">
          {RANGES.map((r) => (
            <button
              key={r.days}
              type="button"
              onClick={() => setDays(r.days)}
              className={cn(
                "rounded-md border px-2.5 py-1 text-xs transition-colors",
                days === r.days
                  ? "border-primary bg-primary/10 text-primary"
                  : "border-input hover:bg-accent",
              )}
            >
              {r.label}
            </button>
          ))}
        </div>
      </div>

      <section className="grid grid-cols-2 gap-3 lg:grid-cols-3">
        {loading ? (
          <>
            <StatCardSkeleton />
            <StatCardSkeleton />
            <StatCardSkeleton />
          </>
        ) : (
          <>
            <StatCard
              label="Posters created"
              value={data?.postersInPeriod ?? 0}
              hint={`Last ${data?.days ?? days} days`}
            />
            <StatCard
              label="Videos generated"
              value={data?.videosInPeriod ?? 0}
              hint={`Last ${data?.days ?? days} days`}
            />
            <StatCard
              label="Campaigns sent"
              value={data?.campaignsInPeriod ?? 0}
              hint={`Last ${data?.days ?? days} days`}
            />
          </>
        )}
      </section>

      <Panel
        title="Activity over time"
        description="Posters, videos and campaigns produced across the platform each day."
      >
        <div className="p-4">
          {loading ? (
            <Skeleton className="h-[280px] w-full" />
          ) : !hasActivity ? (
            <EmptyState
              icon={BarChart3}
              title="No activity in this period"
              message="Try a longer range, or check back once pharmacies start creating content."
            />
          ) : (
            <ActivityChart series={data?.series ?? []} />
          )}
        </div>
      </Panel>

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel
          title="Most active pharmacies"
          description={`Combined output over the last ${data?.days ?? days} days.`}
        >
          {loading ? (
            <ListSkeleton rows={5} />
          ) : (data?.topPharmacies.length ?? 0) === 0 ? (
            <EmptyState
              icon={BarChart3}
              title="No pharmacy activity yet"
              message="Accounts appear here once they create posters, videos or campaigns."
            />
          ) : (
            <div className="p-4">
              <BarList
                data={(data?.topPharmacies ?? []).map((p) => ({
                  // The account, not its name. Two pharmacies can share a
                  // name; they cannot share a user id.
                  id: p.userId,
                  label: p.pharmacyName,
                  value: p.total,
                }))}
              />
            </div>
          )}
        </Panel>

        <Panel
          title="Breakdown"
          description="What each of those pharmacies produced."
        >
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-4 py-2 font-medium">Pharmacy</th>
                  <th className="px-4 py-2 text-right font-medium">Posters</th>
                  <th className="px-4 py-2 text-right font-medium">Videos</th>
                  <th className="px-4 py-2 text-right font-medium">Campaigns</th>
                  <th className="px-4 py-2 text-right font-medium">Total</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border/60">
                {loading ? (
                  <TableLoading colSpan={5} />
                ) : (data?.topPharmacies.length ?? 0) === 0 ? (
                  <TableEmpty colSpan={5} message="Nothing to show for this period." />
                ) : (
                  data?.topPharmacies.map((p) => (
                    <tr key={p.userId} className="hover:bg-accent/40">
                      <td className="px-4 py-2.5">
                        <p className="font-medium">{p.pharmacyName}</p>
                        <p className="text-xs text-muted-foreground">{p.email}</p>
                      </td>
                      <td className="px-4 py-2.5 text-right tabular-nums">{p.posters}</td>
                      <td className="px-4 py-2.5 text-right tabular-nums">{p.videos}</td>
                      <td className="px-4 py-2.5 text-right tabular-nums">{p.campaigns}</td>
                      <td className="px-4 py-2.5 text-right font-medium tabular-nums">
                        {p.total}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </Panel>
      </div>

      <p className="text-xs text-muted-foreground">
        Campaigns are counted on the day they were sent, not created. Days with
        no activity are included so gaps read as gaps rather than as a straight
        line between active days.
      </p>
    </div>
  );
}

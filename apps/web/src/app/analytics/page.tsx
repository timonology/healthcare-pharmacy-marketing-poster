"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import {
  AlertCircle,
  ArrowUpRight,
  Film,
  FileImage,
  Loader2,
  MousePointerClick,
  Send,
  Sparkles,
  TrendingUp,
  Users,
} from "lucide-react";
import type { AnalyticsOverview } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";

export default function AnalyticsPage() {
  const [data, setData] = useState<AnalyticsOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const d = await api.getAnalyticsOverview();
        if (!cancelled) setData(d);
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : "Failed to load analytics.");
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  if (loading) {
    return (
      <main className="container max-w-6xl py-10">
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading analytics…
        </div>
      </main>
    );
  }

  if (error) {
    // The API returns 403 when a Free/Starter user hits this route. Surface
    // it as an upgrade CTA instead of a scary red error.
    const isForbidden = /403|analytics is a pro feature/i.test(error);
    return (
      <main className="container max-w-4xl py-10">
        <Card className={cn(isForbidden ? "border-primary/40 bg-primary/5" : "border-destructive/40")}>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            {isForbidden ? (
              <Sparkles className="h-8 w-8 text-primary" />
            ) : (
              <AlertCircle className="h-8 w-8 text-destructive" />
            )}
            <h3 className="text-lg font-semibold">
              {isForbidden ? "Analytics is a Pro feature" : "Couldn't load analytics"}
            </h3>
            <p className="max-w-md text-sm text-muted-foreground">{error}</p>
            {isForbidden && (
              <Button asChild className="mt-2 bg-brand-gradient text-primary-foreground">
                <Link href="/pricing">Upgrade to Pro</Link>
              </Button>
            )}
          </CardContent>
        </Card>
      </main>
    );
  }

  if (!data) return null;

  return (
    <main className="container max-w-6xl py-10">
      <header className="flex flex-col gap-1 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <div className="flex items-center gap-2">
            <h1 className="text-3xl font-bold tracking-tight">Analytics</h1>
            <Badge className="gap-1 bg-primary/15 text-primary hover:bg-primary/15">
              <Sparkles className="h-3 w-3" />
              Pro
            </Badge>
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            How your pharmacy's posters, videos, and campaigns are performing this month.
          </p>
        </div>
      </header>

      {/* Usage cards */}
      <section className="mt-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard
          icon={<FileImage className="h-4 w-4" />}
          label="Posters this month"
          value={data.usage.postersThisMonth}
          hint={`${data.usage.postersTotal} total`}
        />
        <StatCard
          icon={<Film className="h-4 w-4" />}
          label="Videos generated"
          value={data.usage.videosThisMonth}
          hint={`${data.usage.videosTotal} total`}
        />
        <StatCard
          icon={<Send className="h-4 w-4" />}
          label="Campaigns sent"
          value={data.usage.campaignsThisMonth}
          hint={`${data.usage.recipientsThisMonth} recipients`}
        />
        <StatCard
          icon={<Users className="h-4 w-4" />}
          label="Patients"
          value={data.usage.patientsTotal}
          hint="on your list"
        />
      </section>

      {/* Engagement + timeseries */}
      <section className="mt-6 grid grid-cols-1 gap-4 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader className="pb-2">
            <CardTitle className="text-base">Campaigns · last 8 weeks</CardTitle>
            <CardDescription className="text-xs">
              Number of campaigns dispatched, weekly.
            </CardDescription>
          </CardHeader>
          <CardContent className="pt-2">
            <BarChart data={data.campaignsSeries} />
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-base">Email engagement</CardTitle>
            <CardDescription className="text-xs">
              Estimated — real tracking lands with webhook wiring.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4 pt-2">
            <Rate
              icon={<Send className="h-4 w-4" />}
              label="Emails sent"
              value={data.engagement.emailsSent.toLocaleString()}
              sub="across all campaigns"
            />
            <Rate
              icon={<TrendingUp className="h-4 w-4" />}
              label="Open rate"
              value={`${Math.round(data.engagement.openRate * 100)}%`}
              sub={`~${Math.round(data.engagement.emailsSent * data.engagement.openRate)} opens`}
              accent
            />
            <Rate
              icon={<MousePointerClick className="h-4 w-4" />}
              label="Click rate"
              value={`${Math.round(data.engagement.clickRate * 100)}%`}
              sub={`~${Math.round(data.engagement.emailsSent * data.engagement.clickRate)} clicks`}
            />
            <Rate
              icon={<Film className="h-4 w-4" />}
              label="Video views"
              value={data.engagement.videosViewed.toLocaleString()}
              sub="from campaign links"
            />
          </CardContent>
        </Card>
      </section>

      {/* Top posters + videos */}
      <section className="mt-6 grid grid-cols-1 gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-base">Top posters</CardTitle>
            <CardDescription className="text-xs">
              Ranked by campaign send count.
            </CardDescription>
          </CardHeader>
          <CardContent className="pt-2">
            <TopList items={data.topPosters} emptyHint="Attach a poster to a campaign to see it here." />
          </CardContent>
        </Card>
        <Card>
          <CardHeader className="pb-2">
            <CardTitle className="text-base">Top videos</CardTitle>
            <CardDescription className="text-xs">
              Ready videos, most recent first.
            </CardDescription>
          </CardHeader>
          <CardContent className="pt-2">
            <TopList items={data.topVideos} emptyHint="Generate a video from the Videos page." />
          </CardContent>
        </Card>
      </section>

      <p className="mt-8 text-center text-xs text-muted-foreground">
        Open/click rates are estimates. Real per-message tracking activates when your
        pharmacy connects a Mailjet webhook.
      </p>
    </main>
  );
}

function StatCard({
  icon,
  label,
  value,
  hint,
}: {
  icon: React.ReactNode;
  label: string;
  value: number | string;
  hint?: string;
}) {
  return (
    <Card>
      <CardContent className="pt-5">
        <div className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
          {icon}
          {label}
        </div>
        <p className="mt-1.5 text-2xl font-bold tracking-tight">
          {typeof value === "number" ? value.toLocaleString() : value}
        </p>
        {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
      </CardContent>
    </Card>
  );
}

function Rate({
  icon,
  label,
  value,
  sub,
  accent,
}: {
  icon: React.ReactNode;
  label: string;
  value: string;
  sub?: string;
  accent?: boolean;
}) {
  return (
    <div className="flex items-center justify-between gap-3">
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        {icon}
        <span>{label}</span>
      </div>
      <div className="text-right">
        <p className={cn(
          "text-base font-semibold tabular-nums",
          accent && "text-primary",
        )}>
          {value}
        </p>
        {sub && <p className="text-[10px] text-muted-foreground">{sub}</p>}
      </div>
    </div>
  );
}

function TopList({
  items,
  emptyHint,
}: {
  items: AnalyticsOverview["topPosters"];
  emptyHint: string;
}) {
  if (items.length === 0) {
    return <p className="py-6 text-center text-xs text-muted-foreground">{emptyHint}</p>;
  }
  const maxScore = Math.max(...items.map(i => i.score), 1);
  return (
    <ul className="space-y-2.5">
      {items.map((item) => (
        <li key={`${item.kind}-${item.id}`} className="space-y-1">
          <div className="flex items-center justify-between gap-2 text-sm">
            <Link
              href={item.kind === "poster" ? `/posters/${item.id}` : "/videos"}
              className="line-clamp-1 flex-1 truncate hover:text-primary"
            >
              {item.name}
            </Link>
            <span className="flex items-center gap-0.5 text-xs tabular-nums text-muted-foreground">
              {item.score}
              <ArrowUpRight className="h-3 w-3" />
            </span>
          </div>
          <div className="h-1.5 w-full overflow-hidden rounded-full bg-muted">
            <div
              className={cn(
                "h-full rounded-full transition-all",
                item.kind === "video" ? "bg-primary" : "bg-emerald-500",
              )}
              style={{ width: `${(item.score / maxScore) * 100}%` }}
            />
          </div>
        </li>
      ))}
    </ul>
  );
}

/**
 * Inline SVG bar chart. Deliberately dependency-free — recharts would add
 * ~90 KB and this dataset is <= 8 points. `viewBox` scales the chart to the
 * container width; bar colours follow the theme via `currentColor` on the
 * parent, which is set to the primary hue below.
 */
function BarChart({ data }: { data: AnalyticsOverview["campaignsSeries"] }) {
  if (data.length === 0) {
    return (
      <p className="py-8 text-center text-xs text-muted-foreground">
        No campaigns sent in the last 8 weeks.
      </p>
    );
  }
  const max = Math.max(...data.map(d => d.count), 1);
  const width = 100;
  const height = 40;
  const barGap = 2;
  const barWidth = (width - barGap * (data.length - 1)) / data.length;

  return (
    <div className="text-primary">
      <svg viewBox={`0 0 ${width} ${height + 12}`} preserveAspectRatio="none" className="h-32 w-full">
        {data.map((d, i) => {
          const h = (d.count / max) * height;
          const x = i * (barWidth + barGap);
          const y = height - h;
          return (
            <g key={d.label}>
              <rect
                x={x}
                y={y}
                width={barWidth}
                height={h}
                rx={0.5}
                className="fill-current opacity-90"
              />
              <text
                x={x + barWidth / 2}
                y={height + 10}
                textAnchor="middle"
                className="fill-muted-foreground text-[3px]"
              >
                {d.label}
              </text>
            </g>
          );
        })}
      </svg>
      <div className="mt-2 flex items-center justify-between text-[10px] text-muted-foreground">
        <span>{data[0].label}</span>
        <span>peak: {max} campaign{max === 1 ? "" : "s"}</span>
        <span>{data[data.length - 1].label}</span>
      </div>
    </div>
  );
}

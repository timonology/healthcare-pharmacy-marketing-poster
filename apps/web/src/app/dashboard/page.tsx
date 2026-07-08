"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import {
  ArrowRight,
  Layers,
  Mail,
  PlusCircle,
  Send,
  Settings,
  Sparkles,
  UserCog,
} from "lucide-react";
import type {
  Campaign,
  CurrentSubscription,
  Me,
  PosterSummary,
} from "@acme/shared-types";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { isUnlimited } from "@acme/shared-types";

export default function DashboardPage() {
  const [me, setMe] = useState<Me | null>(null);
  const [subscription, setSubscription] = useState<CurrentSubscription | null>(null);
  const [posters, setPosters] = useState<PosterSummary[]>([]);
  const [campaigns, setCampaigns] = useState<Campaign[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [m, s, p, c] = await Promise.all([
          api.getMe(),
          api.getCurrentSubscription(),
          api.listPosters({ take: 6 }),
          api.listCampaigns().catch(() => [] as Campaign[]),
        ]);
        if (cancelled) return;
        // Middleware already gates this — only redirect if the API explicitly
        // says onboarding is incomplete (avoids loops on stale/partial data).
        if (m.profile.onboardingCompleted === false) {
          window.location.assign("/onboarding");
          return;
        }
        setMe(m);
        setSubscription(s);
        setPosters(p.items);
        setCampaigns(c);
      } catch {
        // Middleware will redirect to login if unauthenticated.
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (loading) {
    return (
      <main className="container max-w-6xl py-12 text-sm text-muted-foreground">
        Loading dashboard…
      </main>
    );
  }
  if (!me || !subscription) return null;

  const plan = subscription.plan;
  const usage = subscription.usage;

  return (
    <main className="container max-w-6xl py-10">
      <header className="flex flex-col gap-2 md:flex-row md:items-end md:justify-between">
        <div>
          <p className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
            Welcome back
          </p>
          <h1 className="mt-1 text-3xl font-bold tracking-tight">
            {me.profile.pharmacyName || me.displayName}
          </h1>
          {me.profile.sonarFCode && (
            <p className="mt-1 text-xs text-muted-foreground">
              Sonar FCode <span className="font-mono">{me.profile.sonarFCode}</span>
            </p>
          )}
        </div>
        <Button asChild className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground">
          <Link href="/templates">
            <PlusCircle className="mr-1.5 h-4 w-4" />
            Create new poster
          </Link>
        </Button>
      </header>

      {/* Stats */}
      <section className="mt-8 grid grid-cols-2 gap-4 md:grid-cols-4">
        <Stat
          label="Posters"
          value={`${usage.posters}${isUnlimited(plan.maxPosters) ? "" : ` / ${plan.maxPosters}`}`}
          hint={isUnlimited(plan.maxPosters) ? "Unlimited" : `${plan.displayName} plan`}
        />
        <Stat
          label="Patients reached"
          value={`${usage.campaignRecipientsThisMonth}${isUnlimited(plan.maxCampaignRecipientsPerMonth) ? "" : ` / ${plan.maxCampaignRecipientsPerMonth}`}`}
          hint="This month"
        />
        <Stat
          label="Patients"
          value={`${usage.patients ?? 0}${isUnlimited(plan.maxPatients) ? "" : ` / ${plan.maxPatients}`}`}
          hint={`${campaigns.filter((c) => c.status === "Sent").length} campaigns sent`}
        />
        <Stat
          label="Current plan"
          value={plan.displayName}
          hint={plan.monthlyPriceGbp === 0 ? "Free forever" : `£${plan.monthlyPriceGbp}/mo`}
          accent={plan.tier === "Pro" ? "primary" : undefined}
        />
      </section>

      {/* Quick actions */}
      <section className="mt-8 grid grid-cols-1 gap-4 md:grid-cols-4">
        <QuickAction href="/templates" icon={Layers} label="Browse templates" />
        <QuickAction href="/patients" icon={UserCog} label="Manage patients" />
        <QuickAction href="/campaigns" icon={Send} label="View campaigns" />
        <QuickAction href="/pricing" icon={Sparkles} label="Upgrade plan" />
      </section>

      {/* Recent posters */}
      <section className="mt-12">
        <div className="flex items-end justify-between">
          <div>
            <h2 className="text-xl font-semibold tracking-tight">Recent posters</h2>
            <p className="text-sm text-muted-foreground">
              Open any poster to edit, share, or export.
            </p>
          </div>
          <Button asChild variant="ghost" size="sm">
            <Link href="/posters">
              View all
              <ArrowRight className="ml-1 h-4 w-4" />
            </Link>
          </Button>
        </div>

        {posters.length === 0 ? (
          <Card className="mt-4">
            <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
              <h3 className="text-base font-semibold">No posters yet</h3>
              <p className="text-sm text-muted-foreground">
                Start with a template tuned for pharmacy marketing.
              </p>
              <Button asChild className="mt-2">
                <Link href="/templates">Browse templates</Link>
              </Button>
            </CardContent>
          </Card>
        ) : (
          <div className="mt-4 grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-6">
            {posters.map((p) => (
              <Link
                key={p.id}
                href={`/posters/${p.id}`}
                className="group overflow-hidden rounded-xl border border-border/60 bg-card transition-all hover:-translate-y-0.5 hover:shadow-lg"
              >
                <div className="aspect-[1/1.414] w-full overflow-hidden bg-muted">
                  <CanvasPreview doc={p.canvas} className="h-full w-full" />
                </div>
                <div className="p-2.5">
                  <p className="truncate text-xs font-medium">{p.name}</p>
                  <p className="mt-0.5 text-[10px] text-muted-foreground">
                    {new Date(p.updatedAtUtc).toLocaleDateString()}
                  </p>
                </div>
              </Link>
            ))}
          </div>
        )}
      </section>

      {/* Profile snapshot */}
      <section className="mt-12 grid grid-cols-1 gap-6 lg:grid-cols-3">
        <Card className="lg:col-span-2">
          <CardHeader className="flex flex-row items-start justify-between space-y-0">
            <div>
              <CardTitle>Pharmacy profile</CardTitle>
              <CardDescription>
                The details we stamp onto your posters.
              </CardDescription>
            </div>
            <Button asChild variant="outline" size="sm">
              <Link href="/profile">
                <Settings className="mr-1.5 h-4 w-4" />
                Edit
              </Link>
            </Button>
          </CardHeader>
          <Separator />
          <CardContent className="grid grid-cols-1 gap-4 pt-6 sm:grid-cols-2">
            <ProfileField label="Pharmacy" value={me.profile.pharmacyName} />
            <ProfileField label="Address" value={me.profile.address} />
            <ProfileField label="Postcode" value={me.profile.postCode || "—"} mono />
            <ProfileField label="Contact" value={me.profile.contactName} />
            <ProfileField label="Phone" value={me.profile.contactPhone} />
            <ProfileField
              label="ODS / F code"
              value={me.profile.sonarFCode ?? "Not linked"}
              mono
            />
            <ProfileField label="Email" value={me.email} />
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Mail className="h-4 w-4 text-primary" />
              Campaigns
            </CardTitle>
            <CardDescription>
              Send your posters to patients by email or SMS.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {campaigns.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                You haven't sent any campaigns yet.
              </p>
            ) : (
              <ul className="space-y-3">
                {campaigns.slice(0, 4).map((c) => (
                  <li key={c.id} className="flex items-start justify-between gap-3">
                    <div className="min-w-0">
                      <p className="truncate text-sm font-medium">{c.name}</p>
                      <p className="text-xs text-muted-foreground">
                        {c.channel} · {c.recipientCount} recipient
                        {c.recipientCount === 1 ? "" : "s"}
                      </p>
                    </div>
                    <Badge variant={c.status === "Sent" ? "default" : "outline"} className="shrink-0">
                      {c.status}
                    </Badge>
                  </li>
                ))}
              </ul>
            )}
            <Button asChild size="sm" variant="outline" className="mt-4 w-full">
              <Link href="/campaigns">Open campaigns</Link>
            </Button>
          </CardContent>
        </Card>
      </section>
    </main>
  );
}

function Stat({
  label,
  value,
  hint,
  accent,
}: {
  label: string;
  value: string;
  hint?: string;
  accent?: "primary";
}) {
  return (
    <div
      className={cn(
        "rounded-xl border bg-card p-4",
        accent === "primary" ? "border-primary/40 ring-1 ring-primary/15" : "border-border/60",
      )}
    >
      <p className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
        {label}
      </p>
      <p className="mt-2 text-2xl font-bold tracking-tight">{value}</p>
      {hint && <p className="mt-1 text-xs text-muted-foreground">{hint}</p>}
    </div>
  );
}

function QuickAction({
  href,
  icon: Icon,
  label,
}: {
  href: string;
  icon: React.ComponentType<{ className?: string }>;
  label: string;
}) {
  return (
    <Link
      href={href}
      className="group flex items-center gap-3 rounded-xl border border-border/60 bg-card p-4 transition-all hover:-translate-y-0.5 hover:shadow-md"
    >
      <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary/10 text-primary">
        <Icon className="h-4 w-4" />
      </span>
      <span className="flex-1 text-sm font-medium">{label}</span>
      <ArrowRight className="h-4 w-4 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
    </Link>
  );
}

function ProfileField({
  label,
  value,
  mono,
}: {
  label: string;
  value: string;
  mono?: boolean;
}) {
  return (
    <div>
      <p className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
        {label}
      </p>
      <p className={cn("mt-1 text-sm", mono && "font-mono", !value && "text-muted-foreground")}>
        {value || "Not set"}
      </p>
    </div>
  );
}

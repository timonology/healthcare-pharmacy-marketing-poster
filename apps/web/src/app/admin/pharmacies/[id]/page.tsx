"use client";

import { Suspense, useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { ArrowLeft } from "lucide-react";
import {
  SUPPORT_TABS,
  type PharmacySupportOverview,
  type SupportTab,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { ErrorNote, StatCardSkeleton, StatusDot, TierBadge } from "@/components/admin/AdminUi";
import { OverviewTab } from "@/components/admin/support/OverviewTab";
import { PatientsTab } from "@/components/admin/support/PatientsTab";
import { GroupsTab } from "@/components/admin/support/GroupsTab";
import { PostersTab } from "@/components/admin/support/PostersTab";
import { CampaignsTab } from "@/components/admin/support/CampaignsTab";
import { VideosTab } from "@/components/admin/support/VideosTab";
import { cn } from "@/lib/utils";

/**
 * Everything one pharmacy has, for answering a question about it.
 *
 * The active tab lives in the URL rather than in component state. That is the
 * difference between "I'll look into it" and "here, look" — an administrator
 * can paste the address of the exact delivery report they are describing into
 * a ticket, and the person who opens it lands on the same thing rather than on
 * an overview they have to navigate from.
 *
 * Nothing on this page changes the pharmacy's data. Account actions — plan,
 * status, assignment — stay on the pharmacies list, where the guard rails and
 * the confirmations already live.
 */
export default function PharmacySupportPage() {
  // useParams rather than the `params` prop: the prop is a Promise from
  // Next 15, and the hook reads the same value in both versions.
  const { id } = useParams<{ id: string }>();
  // useSearchParams opts the subtree out of static rendering, and Next refuses
  // to build a page that reads it without a boundary. Matches the pattern the
  // login page already uses.
  return (
    <Suspense fallback={<LoadingCards />}>
      <PharmacySupportView id={id} />
    </Suspense>
  );
}

function LoadingCards() {
  return (
    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
      {Array.from({ length: 4 }).map((_, i) => (
        <StatCardSkeleton key={i} />
      ))}
    </div>
  );
}

function PharmacySupportView({ id }: { id: string }) {
  const router = useRouter();
  const searchParams = useSearchParams();

  const requested = searchParams?.get("tab");
  const active: SupportTab = SUPPORT_TABS.some((t) => t.key === requested)
    ? (requested as SupportTab)
    : "overview";

  const [overview, setOverview] = useState<PharmacySupportOverview | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.admin.support
      .overview(id)
      .then((o) => {
        if (!cancelled) setOverview(o);
      })
      .catch((err: unknown) => {
        if (!cancelled)
          setError(
            err instanceof Error ? err.message : "Could not load this pharmacy.",
          );
      });
    return () => {
      cancelled = true;
    };
  }, [id]);

  const selectTab = useCallback(
    (tab: SupportTab) => {
      // replace, not push: flicking between tabs while reading should not bury
      // the pharmacies list under six history entries.
      router.replace(`/admin/pharmacies/${id}?tab=${tab}`, {
        scroll: false,
      });
    },
    [router, id],
  );

  const name = overview?.pharmacyName || overview?.contactName || "Pharmacy";

  return (
    <div className="space-y-4">
      <div>
        <Button
          asChild
          variant="ghost"
          size="sm"
          className="-ml-2 h-7 gap-1 text-xs text-muted-foreground"
        >
          <Link href="/admin/pharmacies">
            <ArrowLeft className="h-3.5 w-3.5" />
            All pharmacies
          </Link>
        </Button>
      </div>

      {error && <ErrorNote message={error} />}

      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h1 className="flex flex-wrap items-center gap-2 text-xl font-semibold tracking-tight">
            {overview ? name : "Loading…"}
            {overview && <StatusDot active={overview.isActive} />}
          </h1>
          {overview && (
            <p className="mt-1 flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
              <span>{overview.email}</span>
              <TierBadge tier={overview.tier} />
            </p>
          )}
        </div>
      </header>

      {/*
        Plain buttons rather than the Radix Tabs primitive. Tabs owns its own
        selected state; here the URL owns it, and reconciling the two produces
        a control that fights the back button. A row of links is what this
        actually is.
      */}
      <nav
        aria-label="Pharmacy sections"
        className="flex flex-wrap gap-1 border-b border-border/60"
      >
        {SUPPORT_TABS.map((tab) => (
          <button
            key={tab.key}
            type="button"
            onClick={() => selectTab(tab.key)}
            aria-current={active === tab.key ? "page" : undefined}
            className={cn(
              "-mb-px border-b-2 px-3 py-2 text-sm transition-colors",
              active === tab.key
                ? "border-primary font-medium text-foreground"
                : "border-transparent text-muted-foreground hover:text-foreground",
            )}
          >
            {tab.label}
          </button>
        ))}
      </nav>

      {!overview && !error ? (
        <LoadingCards />
      ) : overview ? (
        <>
          {active === "overview" && <OverviewTab data={overview} />}
          {active === "patients" && (
            <PatientsTab pharmacyId={id} pharmacyName={name} />
          )}
          {active === "groups" && (
            <GroupsTab pharmacyId={id} pharmacyName={name} />
          )}
          {active === "posters" && (
            <PostersTab pharmacyId={id} pharmacyName={name} />
          )}
          {active === "campaigns" && (
            <CampaignsTab pharmacyId={id} pharmacyName={name} />
          )}
          {active === "videos" && (
            <VideosTab pharmacyId={id} pharmacyName={name} />
          )}
        </>
      ) : null}
    </div>
  );
}

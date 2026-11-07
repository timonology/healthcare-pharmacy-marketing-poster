"use client";

import { Suspense, useCallback } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ConnectionsTab } from "@/components/social/ConnectionsTab";
import { PostsTab } from "@/components/social/PostsTab";
import { AdsTab } from "@/components/social/AdsTab";
import { RecurringTab } from "@/components/social/RecurringTab";
import { cn } from "@/lib/utils";

const TABS = [
  { key: "accounts", label: "Connected accounts" },
  { key: "scheduled", label: "Scheduled" },
  { key: "recurring", label: "Recurring" },
  { key: "history", label: "Published" },
  { key: "ads", label: "Google Ads" },
] as const;

type TabKey = (typeof TABS)[number]["key"];

export default function SocialPage() {
  // useSearchParams opts the subtree out of static rendering and Next refuses
  // to build without a boundary. Same pattern as the login page.
  return (
    <Suspense fallback={<p className="p-6 text-sm text-muted-foreground">Loading…</p>}>
      <SocialView />
    </Suspense>
  );
}

function SocialView() {
  const router = useRouter();
  const params = useSearchParams();

  const requested = params?.get("tab");
  const active: TabKey = TABS.some((t) => t.key === requested)
    ? (requested as TabKey)
    : "accounts";

  // The tab lives in the URL so the publish dialog can link straight to
  // ?tab=scheduled after queuing something, and so a link in a support
  // conversation lands where it was meant to.
  const select = useCallback(
    (tab: TabKey) => router.replace(`/social?tab=${tab}`, { scroll: false }),
    [router],
  );

  return (
    <main className="container max-w-5xl py-8">
      <header className="mb-6">
        <h1 className="text-2xl font-semibold tracking-tight">Social &amp; Ads</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Publish your posters and videos to your own channels, and run local
          ads from the same designs.
        </p>
      </header>

      <nav
        aria-label="Sections"
        className="mb-5 flex flex-wrap gap-1 border-b border-border/60"
      >
        {TABS.map((tab) => (
          <button
            key={tab.key}
            type="button"
            onClick={() => select(tab.key)}
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

      {active === "accounts" && <ConnectionsTab />}
      {active === "scheduled" && <PostsTab filter="upcoming" />}
      {active === "recurring" && <RecurringTab />}
      {active === "history" && <PostsTab filter="history" />}
      {active === "ads" && <AdsTab />}
    </main>
  );
}

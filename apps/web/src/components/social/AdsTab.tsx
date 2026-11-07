"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { AlertTriangle, Megaphone, Pause, Play, Plus, RefreshCw } from "lucide-react";
import type { GoogleAdsCampaign, SocialConnection } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { CreateCampaignDialog } from "./CreateCampaignDialog";

/**
 * Google Ads campaigns built from the pharmacy's own posters and videos.
 *
 * Metrics are shown with their "as of" time, always. Google's figures move for
 * days as invalid traffic is filtered, so a bare number invites someone to
 * reconcile it against the Ads console and conclude one of them is broken.
 */
export function AdsTab() {
  const [campaigns, setCampaigns] = useState<GoogleAdsCampaign[] | null>(null);
  const [connections, setConnections] = useState<SocialConnection[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    try {
      const [list, conns] = await Promise.all([
        api.ads.campaigns(),
        api.social.connections(),
      ]);
      setCampaigns(list);
      setConnections(conns);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load campaigns.");
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const adsConnections = connections.filter(
    (c) => c.platform === "GoogleAds" && c.status === "Active",
  );
  const adsConnected = adsConnections.length > 0;

  async function act(id: string, fn: () => Promise<unknown>) {
    setBusy(id);
    setError(null);
    try {
      await fn();
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "That did not work.");
    } finally {
      setBusy(null);
    }
  }

  function toggle(c: GoogleAdsCampaign) {
    const enabling = c.status !== "Enabled";
    // Enabling starts spending real money against a live budget, so it is
    // never one click. Pausing stops spending and needs no confirmation.
    if (
      enabling &&
      !window.confirm(
        `Start "${c.name}"? Google will begin spending up to ${c.currencyCode} ` +
          `${c.budget.toFixed(2)} ${c.budgetKind === "Daily" ? "a day" : "in total"} ` +
          "until you pause it.",
      )
    )
      return;
    void act(c.id, () => api.ads.setCampaignEnabled(c.id, enabling));
  }

  return (
    <div className="space-y-4">
      {error && (
        <p className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
          {error}
        </p>
      )}

      {/*
        Not decoration. UK law prohibits advertising prescription-only medicines
        to the public, and Google disapproves uncertified pharmaceutical
        campaigns outright rather than merely limiting them. Better to say so
        beside the create button than to let someone find out via a rejection.
      */}
      <div className="flex items-start gap-2.5 rounded-md border border-amber-500/30 bg-amber-500/10 p-3">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-700 dark:text-amber-400" />
        <div className="text-xs text-amber-900 dark:text-amber-200">
          <p className="font-medium">Healthcare advertising rules apply</p>
          <p className="mt-0.5">
            Advertise services — flu jabs, blood pressure checks, travel clinics
            — not prescription medicines. Advertising prescription-only medicines
            to the public is not permitted in the UK, and Google disapproves
            uncertified pharmaceutical ads. You are the advertiser of record.
          </p>
        </div>
      </div>

      {!adsConnected && (
        <div className="rounded-lg border border-border/60 bg-card p-4">
          <p className="text-sm font-medium">No Google Ads account connected</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Connect Google under Connected accounts to create campaigns.
          </p>
          <Button asChild size="sm" variant="outline" className="mt-3">
            <Link href="/social?tab=accounts">Connect Google</Link>
          </Button>
        </div>
      )}

      {adsConnected && (
        <div className="flex justify-end">
          <Button size="sm" className="gap-1.5" onClick={() => setCreating(true)}>
            <Plus className="h-3.5 w-3.5" />
            New campaign
          </Button>
        </div>
      )}

      <CreateCampaignDialog
        open={creating}
        onClose={() => setCreating(false)}
        connections={adsConnections}
        onCreated={() => void load()}
      />

      {campaigns === null ? (
        <p className="p-6 text-sm text-muted-foreground">Loading…</p>
      ) : campaigns.length === 0 ? (
        <div className="flex flex-col items-center gap-2 rounded-lg border border-border/60 bg-card px-6 py-12 text-center">
          <div className="rounded-full bg-muted p-3">
            <Megaphone className="h-5 w-5 text-muted-foreground" aria-hidden />
          </div>
          <p className="text-sm font-medium">No campaigns yet</p>
          <p className="max-w-sm text-xs text-muted-foreground">
            Campaigns are created from a poster or video in your library, and
            always start paused so you can check the targeting before spending.
          </p>
        </div>
      ) : (
        <ul className="space-y-3">
          {campaigns.map((c) => (
            <li key={c.id} className="rounded-lg border border-border/60 bg-card">
              <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border/40 px-4 py-3">
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium">{c.name}</p>
                  <p className="mt-0.5 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                    <StatusPill status={c.status} />
                    <span>{c.campaignType}</span>
                    <span>
                      {c.currencyCode} {c.budget.toFixed(2)}{" "}
                      {c.budgetKind === "Daily" ? "/ day" : "total"}
                    </span>
                  </p>
                  {c.lastError && (
                    <p className="mt-1 text-[11px] text-destructive">{c.lastError}</p>
                  )}
                </div>

                <div className="flex gap-1.5">
                  {c.externalCampaignId && (
                    <>
                      <Button
                        size="sm"
                        variant="outline"
                        className="h-7 gap-1 text-xs"
                        disabled={busy === c.id}
                        onClick={() => act(c.id, () => api.ads.syncCampaign(c.id))}
                      >
                        <RefreshCw className="h-3.5 w-3.5" />
                        Refresh
                      </Button>
                      <Button
                        size="sm"
                        variant={c.status === "Enabled" ? "outline" : "default"}
                        className="h-7 gap-1 text-xs"
                        disabled={busy === c.id}
                        onClick={() => toggle(c)}
                      >
                        {c.status === "Enabled" ? (
                          <>
                            <Pause className="h-3.5 w-3.5" />
                            Pause
                          </>
                        ) : (
                          <>
                            <Play className="h-3.5 w-3.5" />
                            Enable
                          </>
                        )}
                      </Button>
                    </>
                  )}
                </div>
              </header>

              <dl className="grid grid-cols-2 gap-3 px-4 py-3 sm:grid-cols-4">
                <Metric label="Impressions" value={c.metrics.impressions.toLocaleString()} />
                <Metric label="Clicks" value={c.metrics.clicks.toLocaleString()} />
                <Metric
                  label="Cost"
                  value={`${c.currencyCode} ${c.metrics.cost.toFixed(2)}`}
                />
                <Metric label="CTR" value={`${c.metrics.clickThroughRate.toFixed(2)}%`} />
              </dl>

              <p className="border-t border-border/40 px-4 py-2 text-[11px] text-muted-foreground">
                Figures as of {new Date(c.metrics.lastSyncedAtUtc).toLocaleString()}.
                Google keeps adjusting these for a few days as invalid traffic is
                filtered.
              </p>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-[11px] text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-sm font-semibold tabular-nums">{value}</dd>
    </div>
  );
}

function StatusPill({ status }: { status: GoogleAdsCampaign["status"] }) {
  const tone =
    status === "Enabled"
      ? "bg-primary/10 text-primary"
      : status === "Failed"
        ? "bg-destructive/10 text-destructive"
        : status === "Paused"
          ? "bg-amber-500/15 text-amber-700 dark:text-amber-400"
          : "bg-muted text-muted-foreground";

  return (
    <span className={cn("rounded-full px-2 py-0.5 text-[11px] font-medium", tone)}>
      {status}
    </span>
  );
}

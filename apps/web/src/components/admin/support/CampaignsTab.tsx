"use client";

import { useCallback, useEffect, useState } from "react";
import { Mail, RefreshCw } from "lucide-react";
import type {
  SupportCampaign,
  SupportCampaignDetail,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { api } from "@/lib/api";
import {
  EmptyState,
  ErrorNote,
  ListSkeleton,
  Panel,
  formatDate,
} from "../AdminUi";
import { CampaignStatusPill, Masked } from "./SupportUi";

/**
 * The pharmacy's campaigns, and the per-recipient delivery report behind each
 * one.
 *
 * This is the tab most support queries end at: "we sent it and they say it
 * never arrived" is answered by the report, which records what the provider
 * said about each address at the time.
 */
export function CampaignsTab({
  pharmacyId,
  pharmacyName,
}: {
  pharmacyId: string;
  pharmacyName: string;
}) {
  const [campaigns, setCampaigns] = useState<SupportCampaign[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setCampaigns(await api.admin.support.campaigns(pharmacyId));
      setError(null);
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Could not load campaigns.",
      );
    }
  }, [pharmacyId]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title={campaigns ? `Campaigns (${campaigns.length})` : "Campaigns"}
        description="Open one to see what happened to each recipient."
      >
        {!campaigns ? (
          <div className="p-4">
            <ListSkeleton rows={4} />
          </div>
        ) : campaigns.length === 0 ? (
          <EmptyState
            icon={Mail}
            title="No campaigns"
            message={`${pharmacyName} has not sent anything yet.`}
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-4 py-2 font-medium">Campaign</th>
                  <th className="px-4 py-2 font-medium">Poster</th>
                  <th className="px-4 py-2 font-medium">Status</th>
                  <th className="px-4 py-2 font-medium">Delivered</th>
                  <th className="px-4 py-2 font-medium">Sent</th>
                </tr>
              </thead>
              <tbody>
                {campaigns.map((c) => (
                  <tr
                      key={c.id}
                      onClick={() => setOpenId(c.id)}
                      className="cursor-pointer border-b border-border/40 transition-colors hover:bg-accent/50"
                    >
                      <td className="px-4 py-2">
                        <span className="font-medium">{c.name}</span>
                        <span className="ml-2 text-[11px] text-muted-foreground">
                          {c.channel}
                        </span>
                      </td>
                      <td className="px-4 py-2 text-xs text-muted-foreground">
                        {c.posterName}
                      </td>
                      <td className="px-4 py-2">
                        <CampaignStatusPill status={c.status} />
                      </td>
                      <td className="px-4 py-2 text-xs">
                        <span className="font-medium">{c.sentCount}</span>
                        <span className="text-muted-foreground">
                          {" "}
                          / {c.recipientCount}
                        </span>
                        {c.failedCount > 0 && (
                          <span className="ml-2 text-destructive">
                            {c.failedCount} failed
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-2 text-xs text-muted-foreground">
                        {c.sentAtUtc ? formatDate(c.sentAtUtc) : "not sent"}
                      </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>

      <CampaignDialog
        pharmacyId={pharmacyId}
        campaignId={openId}
        onClose={() => setOpenId(null)}
        onResent={load}
      />
    </div>
  );
}

function CampaignDialog({
  pharmacyId,
  campaignId,
  onClose,
  onResent,
}: {
  pharmacyId: string;
  campaignId: string | null;
  onClose: () => void;
  onResent: () => void;
}) {
  const [detail, setDetail] = useState<SupportCampaignDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!campaignId) return;
    setDetail(null);
    setError(null);
    let cancelled = false;
    api.admin.support
      .campaign(pharmacyId, campaignId)
      .then((d) => {
        if (!cancelled) setDetail(d);
      })
      .catch((err: unknown) => {
        if (!cancelled)
          setError(
            err instanceof Error ? err.message : "Could not load the campaign.",
          );
      });
    return () => {
      cancelled = true;
    };
  }, [pharmacyId, campaignId]);

  if (!campaignId) return null;

  async function resend() {
    if (!campaignId) return;
    setBusy(true);
    setError(null);
    try {
      setDetail(await api.admin.support.resendCampaign(pharmacyId, campaignId));
      onResent();
    } catch (err) {
      setError(err instanceof Error ? err.message : "The re-send failed.");
    } finally {
      setBusy(false);
    }
  }

  const failed = detail?.deliveryReport.filter((r) => r.status === "Failed") ?? [];

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{detail?.name ?? "Campaign"}</DialogTitle>
          <DialogDescription>
            {detail
              ? `${detail.channel} · ${detail.recipientCount} recipient${detail.recipientCount === 1 ? "" : "s"} · ${detail.posterName}`
              : "Loading…"}
          </DialogDescription>
        </DialogHeader>

        {error && <ErrorNote message={error} />}

        {!detail ? (
          <ListSkeleton rows={5} />
        ) : (
          <div className="space-y-4">
            <div className="flex flex-wrap items-center gap-3 text-xs">
              <CampaignStatusPill status={detail.status} />
              <span className="text-muted-foreground">
                {detail.sentCount} delivered
              </span>
              {failed.length > 0 && (
                <span className="text-destructive">{failed.length} failed</span>
              )}
              {detail.sentAtUtc && (
                <span className="text-muted-foreground">
                  sent {formatDate(detail.sentAtUtc)}
                </span>
              )}
            </div>

            {detail.note && (
              <p className="rounded-md bg-muted px-3 py-2 text-xs text-muted-foreground">
                {detail.note}
              </p>
            )}

            {detail.canResend && (
              <div className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-border/60 p-3">
                <p className="text-xs text-muted-foreground">
                  Re-sending contacts this pharmacy&apos;s patients again, from
                  the pharmacy&apos;s name. It is recorded against yours.
                </p>
                <Button size="sm" disabled={busy} onClick={resend} className="gap-1.5">
                  <RefreshCw className="h-3.5 w-3.5" />
                  {busy ? "Sending…" : "Re-send"}
                </Button>
              </div>
            )}

            <div>
              <h3 className="mb-2 text-xs font-semibold text-muted-foreground">
                Delivery report
              </h3>
              {detail.deliveryReport.length === 0 ? (
                <p className="rounded-md bg-muted px-3 py-2 text-xs text-muted-foreground">
                  No per-recipient report was recorded for this campaign — it
                  predates delivery reporting, or it never left the draft state.
                </p>
              ) : (
                <div className="max-h-72 overflow-y-auto rounded-md border border-border/60">
                  <table className="w-full text-sm">
                    <thead className="sticky top-0 border-b border-border/60 bg-card text-left text-xs text-muted-foreground">
                      <tr>
                        <th className="px-3 py-2 font-medium">Recipient</th>
                        <th className="px-3 py-2 font-medium">Result</th>
                        <th className="px-3 py-2 font-medium">Reason</th>
                      </tr>
                    </thead>
                    <tbody>
                      {/* Failures first: the reason someone opened this dialog
                          is almost never the ninety addresses that worked. */}
                      {[...detail.deliveryReport]
                        .sort((a, b) =>
                          a.status === b.status
                            ? 0
                            : a.status === "Failed"
                              ? -1
                              : b.status === "Failed"
                                ? 1
                                : 0,
                        )
                        .map((r, i) => (
                          <tr
                            key={`${r.maskedRecipient}-${i}`}
                            className="border-b border-border/40 last:border-0"
                          >
                            <td className="px-3 py-1.5">
                              <Masked value={r.maskedRecipient} />
                            </td>
                            <td className="px-3 py-1.5 text-xs">
                              <span
                                className={
                                  r.status === "Failed"
                                    ? "text-destructive"
                                    : "text-muted-foreground"
                                }
                              >
                                {r.status}
                              </span>
                            </td>
                            <td className="px-3 py-1.5 text-xs text-muted-foreground">
                              {r.reason ?? "—"}
                            </td>
                          </tr>
                        ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

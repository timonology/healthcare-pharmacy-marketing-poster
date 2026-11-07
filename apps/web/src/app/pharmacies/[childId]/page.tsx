"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import {
  ArrowLeft,
  Eye,
  Loader2,
  ShieldCheck,
} from "lucide-react";
import type { PharmacySupportOverview } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { api } from "@/lib/api";

/**
 * One branch's records, for the head office that runs it.
 *
 * <b>Opening this page is recorded.</b> A head office can see across its
 * branches without switching into them, and switching was what used to leave
 * the audit trail — so the read writes its own entry, naming who looked at
 * which branch and when. The notice at the bottom says so, because an audit
 * trail nobody knows about deters nothing; the point is that people can see
 * their access is answerable.
 *
 * The numbers come from the same code that produces them for Marketing25
 * support staff, so a head office and an administrator on the phone to them
 * never see different figures for the same branch.
 */
export default function BranchDetailPage() {
  const params = useParams<{ childId: string }>();
  const childId = params?.childId ?? "";

  const [branch, setBranch] = useState<PharmacySupportOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [switching, setSwitching] = useState(false);

  useEffect(() => {
    if (!childId) return;
    let cancelled = false;

    (async () => {
      try {
        const data = await api.pharmacies.branchOverview(childId);
        if (!cancelled) setBranch(data);
      } catch (err) {
        if (!cancelled) {
          // The API answers not-found for a branch that is not yours, on
          // purpose — a distinguishable "exists but isn't yours" lets somebody
          // probe for other pharmacies' ids. So the message here says the same
          // thing for both cases, because they are the same answer.
          setError(
            err instanceof Error ? err.message : "That branch could not be opened.",
          );
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [childId]);

  async function manage() {
    if (!branch) return;
    setSwitching(true);
    try {
      await api.pharmacies.startManageAs(branch.userId);
      // Full reload: every list and store in the app was loaded for the
      // current pharmacy, and reconciling them in place is a lot of machinery
      // to get subtly wrong.
      window.location.href = "/dashboard";
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not open that branch.");
      setSwitching(false);
    }
  }

  return (
    <main className="container max-w-4xl py-8">
      <Button asChild variant="ghost" size="sm" className="mb-4 gap-1.5">
        <Link href="/pharmacies">
          <ArrowLeft className="h-4 w-4" />
          Back to branches
        </Link>
      </Button>

      {loading && (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading branch…
        </p>
      )}

      {error && !loading && (
        <p
          role="alert"
          className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {error}
        </p>
      )}

      {branch && !loading && (
        <>
          <header className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <h1 className="text-2xl font-semibold tracking-tight">
                  {branch.pharmacyName || branch.contactName}
                </h1>
                <Badge variant={branch.isActive ? "secondary" : "outline"}>
                  {branch.isActive ? "Active" : "Not activated"}
                </Badge>
                {!branch.onboardingCompleted && (
                  <Badge variant="outline">Setup unfinished</Badge>
                )}
              </div>
              <p className="mt-1 text-sm text-muted-foreground">
                {branch.email}
                {branch.postCode ? ` · ${branch.postCode}` : ""}
              </p>
            </div>

            <Button
              className="gap-1.5"
              disabled={!branch.isActive || switching}
              title={
                branch.isActive
                  ? "Work as this branch"
                  : "They need to activate their account first"
              }
              onClick={manage}
            >
              <Eye className="h-4 w-4" />
              {switching ? "Opening…" : "Work as this branch"}
            </Button>
          </header>

          <section className="mt-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
            <Stat label="Patients" value={branch.patientCount} />
            <Stat label="Groups" value={branch.groupCount} />
            <Stat
              label="Posters"
              value={branch.posterCount}
              hint={`${branch.publishedPosterCount} published`}
            />
            <Stat label="Videos" value={branch.videoCount} />
            <Stat
              label="Campaigns"
              value={branch.campaignCount}
              hint={`${branch.campaignsSentCount} sent`}
            />
            <Stat
              label="Recipients reached"
              value={branch.recipientsReached}
              hint="messages delivered"
            />
            <Stat
              label="Failed campaigns"
              value={branch.campaignsFailedCount}
              // The usual reason a head office opens a branch at all.
              tone={branch.campaignsFailedCount > 0 ? "warn" : "plain"}
            />
            <Stat label="Plan" text={branch.tier} hint={branch.billingStatus} />
          </section>

          <Card className="mt-6">
            <CardHeader className="pb-2">
              <CardTitle className="text-sm">Account</CardTitle>
            </CardHeader>
            <CardContent className="grid gap-2 text-sm sm:grid-cols-2">
              <Row label="Contact" value={branch.contactName || "—"} />
              <Row
                label="Last signed in"
                value={
                  branch.lastLoginAtUtc
                    ? new Date(branch.lastLoginAtUtc).toLocaleString()
                    : "Never"
                }
              />
              <Row
                label="Last campaign"
                value={
                  branch.lastCampaignAtUtc
                    ? new Date(branch.lastCampaignAtUtc).toLocaleString()
                    : "None sent"
                }
              />
              <Row
                label="Joined"
                value={new Date(branch.createdAtUtc).toLocaleDateString()}
              />
            </CardContent>
          </Card>

          {/*
            Said out loud, on the screen where the access happens.

            The read is recorded either way; telling somebody is what turns a
            log into a deterrent and a reassurance. The branch's patients are
            personal data the branch is accountable for, and a head office
            looking at them should know the looking is answerable.
          */}
          <p className="mt-6 flex items-start gap-2 text-xs text-muted-foreground">
            <ShieldCheck className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
            <span>
              Opening a branch&apos;s records is recorded against your name, with
              the date and the branch. Marketing25 keeps this log so that access
              to patient data can be accounted for.
            </span>
          </p>
        </>
      )}
    </main>
  );
}

function Stat({
  label,
  value,
  text,
  hint,
  tone = "plain",
}: {
  label: string;
  value?: number;
  text?: string;
  hint?: string;
  tone?: "plain" | "warn";
}) {
  return (
    <div className="rounded-lg border border-border/60 p-3">
      <p className="text-[11px] uppercase tracking-wide text-muted-foreground">
        {label}
      </p>
      <p
        className={
          tone === "warn"
            ? "mt-0.5 text-xl font-semibold text-destructive"
            : "mt-0.5 text-xl font-semibold"
        }
      >
        {text ?? value ?? 0}
      </p>
      {hint && <p className="text-[11px] text-muted-foreground">{hint}</p>}
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <p className="flex justify-between gap-3 border-b border-border/40 pb-1.5">
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right">{value}</span>
    </p>
  );
}

"use client";

import type { PharmacySupportOverview } from "@acme/shared-types";
import { Panel, StatCard, StatusDot, TierBadge, formatDate } from "../AdminUi";
import { Fact } from "./SupportUi";

/**
 * What an administrator reads first on a support call.
 *
 * Ordered by what a query is usually about: how much they have created, then
 * whether anything is broken, then the account facts that explain a limit
 * being hit. Failed campaigns get their own card with a warning tone even when
 * the count is zero — a card that only appears when there is bad news trains
 * people not to look for it.
 */
export function OverviewTab({ data }: { data: PharmacySupportOverview }) {
  return (
    <div className="space-y-4">
      <Panel
        title="Activity"
        description="Everything this pharmacy has created on the platform."
      >
        <div className="grid gap-3 p-4 sm:grid-cols-2 lg:grid-cols-4">
          <StatCard
            label="Patients"
            value={data.patientCount}
            hint={`in ${data.groupCount.toLocaleString()} group${data.groupCount === 1 ? "" : "s"}`}
          />
          <StatCard
            label="Posters"
            value={data.posterCount}
            hint={`${data.publishedPosterCount.toLocaleString()} published`}
          />
          <StatCard
            label="Campaigns sent"
            value={data.campaignsSentCount}
            hint={
              data.lastCampaignAtUtc
                ? `last ${formatDate(data.lastCampaignAtUtc)}`
                : "none yet"
            }
          />
          <StatCard
            label="Failed campaigns"
            value={data.campaignsFailedCount}
            tone={data.campaignsFailedCount > 0 ? "warning" : "default"}
            hint={
              data.campaignsFailedCount > 0
                ? "see the Campaigns tab"
                : "nothing outstanding"
            }
          />
          <StatCard
            label="Messages delivered"
            value={data.recipientsReached}
            hint="not the same as patients on file"
          />
          <StatCard label="Videos" value={data.videoCount} />
          <StatCard
            label="Video credits"
            value={data.videoCredits}
            hint="remaining balance"
          />
          <StatCard
            label="Total campaigns"
            value={data.campaignCount}
            hint="including drafts"
          />
        </div>
      </Panel>

      <Panel
        title="Account"
        description="The details that explain most plan and access questions."
      >
        <dl className="grid gap-4 p-4 sm:grid-cols-2 lg:grid-cols-3">
          <Fact label="Contact" value={data.contactName || "—"} />
          <Fact label="Email" value={data.email} />
          <Fact label="Post code" value={data.postCode || "—"} />
          <Fact label="Plan" value={<TierBadge tier={data.tier} />} />
          <Fact label="Billing" value={data.billingStatus || "—"} />
          <Fact
            label="Status"
            value={
              <span className="inline-flex items-center gap-1.5">
                <StatusDot active={data.isActive} />
              </span>
            }
          />
          <Fact
            label="Onboarding"
            value={data.onboardingCompleted ? "Complete" : "Not finished"}
          />
          <Fact
            label="Account manager"
            value={data.managedByAdminName ?? "Unassigned"}
          />
          <Fact label="Last sign-in" value={formatDate(data.lastLoginAtUtc)} />
          <Fact label="Joined" value={formatDate(data.createdAtUtc)} />
        </dl>
      </Panel>

      <HierarchyPanel data={data} />
    </div>
  );
}

/**
 * The pharmacy group this account belongs to.
 *
 * Hidden entirely for a standalone pharmacy, which is most of them — an empty
 * "no group" panel on every support call is noise. When it does appear it
 * answers the question support is usually about to ask: who else can see this
 * pharmacy's patients.
 */
function HierarchyPanel({ data }: { data: PharmacySupportOverview }) {
  // Read defensively. `hierarchy` is newer than the deployed API, and an API
  // that has not been redeployed yet returns an overview without it — a bare
  // destructure here would throw and take the whole support page down, tabs
  // included, over a panel that is optional by nature.
  const parentId = data.hierarchy?.parentId ?? null;
  const parentName = data.hierarchy?.parentName ?? null;
  const branches = data.hierarchy?.branches ?? [];

  if (!parentId && branches.length === 0) return null;

  return (
    <Panel
      title="Pharmacy group"
      description={
        parentId
          ? "This is a branch of another pharmacy."
          : "Branches this pharmacy manages."
      }
    >
      {parentId ? (
        <div className="p-4">
          <p className="text-sm">
            Part of{" "}
            <a
              href={`/admin/pharmacies/${parentId}`}
              className="font-medium underline underline-offset-2"
            >
              {parentName}
            </a>
          </p>
          <p className="mt-1 text-xs text-muted-foreground">
            The parent can sign in to manage this branch, and any change they
            make here is recorded against their name in the audit log.
          </p>
        </div>
      ) : (
        <ul className="divide-y">
          {branches.map((b) => (
            <li
              key={b.userId}
              className="flex flex-wrap items-center justify-between gap-2 px-4 py-2.5"
            >
              <div className="min-w-0">
                <a
                  href={`/admin/pharmacies/${b.userId}`}
                  className="text-sm font-medium underline underline-offset-2"
                >
                  {b.pharmacyName}
                </a>
                <p className="truncate text-xs text-muted-foreground">{b.email}</p>
              </div>
              <span className="text-xs text-muted-foreground">
                {b.awaitingActivation ? (
                  <span className="rounded-full bg-amber-500/15 px-2 py-0.5 font-medium text-amber-700 dark:text-amber-400">
                    Invited — not activated
                  </span>
                ) : (
                  <span className="inline-flex items-center gap-1.5">
                    <StatusDot active={b.isActive} />
                    {b.isActive ? "Active" : "Disabled"}
                  </span>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

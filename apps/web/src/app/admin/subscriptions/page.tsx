"use client";

import { useCallback, useEffect, useState } from "react";
import type { AdminUser, SubscriptionTier } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";
import {
  ErrorNote,
  Pagination,
  Panel,
  StatusDot,
  TableEmpty,
  TableLoading,
  TierBadge,
  formatDate,
} from "@/components/admin/AdminUi";

const TAKE = 25;
const TIERS: SubscriptionTier[] = ["Free", "Starter", "Pro"];

export default function AdminSubscriptionsPage() {
  const [rows, setRows] = useState<AdminUser[]>([]);
  const [total, setTotal] = useState(0);
  const [skip, setSkip] = useState(0);
  const [search, setSearch] = useState("");
  const [tierFilter, setTierFilter] = useState<SubscriptionTier | "">("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<AdminUser | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const page = await api.admin.listUsers({
        skip,
        take: TAKE,
        search: search.trim() || undefined,
        tier: tierFilter || undefined,
        // Same reasoning as the Pharmacies page: a pharmacy owner who also
        // holds a staff role still has a plan and still gets billed, so
        // filtering by role would hide their subscription from the only page
        // that manages subscriptions.
        pharmacyAccounts: true,
      });
      setRows(page.items);
      setTotal(page.total);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load.");
    } finally {
      setLoading(false);
    }
  }, [skip, search, tierFilter]);

  useEffect(() => {
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load]);

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <div className="rounded-lg border border-amber-500/30 bg-amber-500/5 p-3 text-xs text-muted-foreground">
        <span className="font-medium text-foreground">Overrides are temporary.</span>{" "}
        Changing a plan here does not touch Stripe. If the pharmacy has an active
        subscription, the next Stripe webhook will restore the tier they are
        actually paying for. Use this for comped accounts and support recovery,
        not to move someone onto a paid plan.
      </div>

      <Panel title="Subscriptions" description="Plans held by pharmacy accounts.">
        <div className="flex flex-wrap gap-2 border-b border-border/60 p-3">
          <Input
            value={search}
            onChange={(e) => {
              setSkip(0);
              setSearch(e.target.value);
            }}
            placeholder="Search pharmacy or email"
            className="h-9 max-w-xs"
          />
          <select
            value={tierFilter}
            onChange={(e) => {
              setSkip(0);
              setTierFilter(e.target.value as SubscriptionTier | "");
            }}
            className="h-9 rounded-md border border-border/60 bg-background px-2 text-sm"
          >
            <option value="">All plans</option>
            {TIERS.map((t) => (
              <option key={t} value={t}>{t}</option>
            ))}
          </select>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <tr>
                <th className="px-4 py-2 font-medium">Pharmacy</th>
                <th className="px-4 py-2 font-medium">Plan</th>
                <th className="px-4 py-2 font-medium">Billing</th>
                <th className="px-4 py-2 font-medium">Renews</th>
                <th className="px-4 py-2 font-medium">Credits</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/60">
              {loading ? (
                <TableLoading colSpan={7} />
              ) : rows.length === 0 ? (
                <TableEmpty colSpan={7} message="No pharmacies match these filters." />
              ) : (
                rows.map((u) => (
                  <tr key={u.id} className="hover:bg-accent/40">
                    <td className="px-4 py-2.5">
                      <p className="font-medium">{u.pharmacyName || u.displayName}</p>
                      <p className="text-xs text-muted-foreground">{u.email}</p>
                    </td>
                    <td className="px-4 py-2.5"><TierBadge tier={u.tier} /></td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {u.billingStatus === "None" ? "No subscription" : u.billingStatus}
                    </td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {formatDate(u.currentPeriodEndUtc)}
                    </td>
                    <td className="px-4 py-2.5">{u.videoCredits}</td>
                    <td className="px-4 py-2.5"><StatusDot active={u.isActive} /></td>
                    <td className="px-4 py-2.5 text-right">
                      <Button size="sm" variant="ghost" onClick={() => setEditing(u)}>
                        Adjust
                      </Button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        <Pagination skip={skip} take={TAKE} total={total} onChange={setSkip} />
      </Panel>

      {editing && (
        <AdjustDialog
          user={editing}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await load();
          }}
          onError={setError}
        />
      )}
    </div>
  );
}

function AdjustDialog({
  user,
  onClose,
  onSaved,
  onError,
}: {
  user: AdminUser;
  onClose: () => void;
  onSaved: () => void;
  onError: (message: string) => void;
}) {
  const [tier, setTier] = useState<SubscriptionTier>(user.tier);
  const [credits, setCredits] = useState("");
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);

  async function save() {
    setSaving(true);
    try {
      if (tier !== user.tier) {
        await api.admin.overrideTier(user.id, tier, reason || undefined);
      }
      const n = Number(credits);
      if (Number.isFinite(n) && n > 0) {
        await api.admin.grantCredits(user.id, Math.floor(n), reason || undefined);
      }
      onSaved();
    } catch (err) {
      onError(err instanceof Error ? err.message : "Could not save changes.");
      setSaving(false);
    }
  }

  const hasStripeSubscription =
    user.billingStatus !== "None" && user.billingStatus !== "Canceled";

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-foreground/40 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-md rounded-lg border border-border/60 bg-card p-5 shadow-xl">
        <h2 className="text-sm font-semibold tracking-tight">
          Adjust {user.pharmacyName || user.displayName}
        </h2>
        <p className="mt-0.5 text-xs text-muted-foreground">{user.email}</p>

        {hasStripeSubscription && (
          <p className="mt-3 rounded-md border border-amber-500/30 bg-amber-500/5 p-2 text-xs">
            This pharmacy has an active Stripe subscription ({user.billingStatus}).
            A tier change here will be reverted by the next webhook.
          </p>
        )}

        <div className="mt-4 space-y-3">
          <div className="space-y-1.5">
            <label className="text-xs font-medium" htmlFor="tier">Plan</label>
            <select
              id="tier"
              value={tier}
              onChange={(e) => setTier(e.target.value as SubscriptionTier)}
              className="h-9 w-full rounded-md border border-border/60 bg-background px-2 text-sm"
            >
              {TIERS.map((t) => (
                <option key={t} value={t}>{t}</option>
              ))}
            </select>
          </div>

          <div className="space-y-1.5">
            <label className="text-xs font-medium" htmlFor="credits">
              Grant video credits (optional)
            </label>
            <Input
              id="credits"
              type="number"
              min={0}
              max={1000}
              value={credits}
              onChange={(e) => setCredits(e.target.value)}
              placeholder="0"
              className="h-9"
            />
          </div>

          <div className="space-y-1.5">
            <label className="text-xs font-medium" htmlFor="reason">
              Reason (recorded in the audit log)
            </label>
            <Input
              id="reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder="e.g. goodwill after failed render"
              className="h-9"
            />
          </div>
        </div>

        <div className="mt-5 flex justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button size="sm" onClick={save} disabled={saving}>
            {saving ? "Saving…" : "Save changes"}
          </Button>
        </div>
      </div>
    </div>
  );
}

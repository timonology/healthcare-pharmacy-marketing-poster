"use client";

import { useEffect, useId, useState } from "react";
import Link from "next/link";
import { FolderSearch } from "lucide-react";
import type {
  AdminSummary,
  AdminUser,
  SubscriptionTier,
  UserRole,
} from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { api } from "@/lib/api";
import { RoleBadge, StatusDot, TierBadge, formatDate } from "./AdminUi";
import { cn } from "@/lib/utils";

const TIERS: SubscriptionTier[] = ["Free", "Starter", "Pro"];

/**
 * Everything about one account, with every action that applies to it.
 *
 * The row actions in the table are the common cases; this is where the rest
 * live. Grouping them against the full record matters — changing someone's
 * plan or granting credits are decisions you make *after* looking at what they
 * already have, and a menu on a table row shows you none of that.
 *
 * Every action re-checks its own guard rails server-side. The disabled states
 * here are guidance so an admin isn't invited to click something that will be
 * refused; they are not the enforcement.
 */
export function AccountDrawer({
  user,
  admins,
  actorRole,
  actorId,
  onClose,
  onChanged,
}: {
  user: AdminUser | null;
  admins: AdminSummary[];
  actorRole: UserRole | undefined;
  actorId: string | undefined;
  onClose: () => void;
  /** Called after any successful mutation so the caller can refetch. */
  onChanged: () => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  /**
   * The last failure, and which action produced it.
   *
   * The action name is carried so the message can be shown beside the control
   * that failed. It used to be a bare string rendered at the top of the
   * dialog — and the dialog scrolls, so a refusal from an action near the
   * bottom appeared somewhere the operator could not see. The observed
   * behaviour was three clicks in a row with no visible response, and a server
   * that refused all three with a perfectly clear explanation each time.
   */
  const [error, setError] = useState<{ action: string; message: string } | null>(null);
  const [credits, setCredits] = useState("10");

  // Reset transient state when a different account is opened, otherwise an
  // error from the previous one lingers over the new record.
  useEffect(() => {
    setError(null);
    setBusy(null);
    setCredits("10");
  }, [user?.id]);

  if (!user) return null;

  const isSuperAdmin = actorRole === "SuperAdmin";
  const isSelf = actorId === user.id;
  const targetIsStaff = user.role === "Admin" || user.role === "SuperAdmin";

  // An Admin may manage any pharmacy but not another staff account. Mirrors
  // AdminService.CanActOn, which is what actually enforces it.
  const canAct = isSuperAdmin || !targetIsStaff;

  async function run(name: string, fn: () => Promise<unknown>) {
    setBusy(name);
    setError(null);
    try {
      await fn();
      onChanged();
    } catch (err) {
      setError({
        action: name,
        message: err instanceof Error ? err.message : "That action failed.",
      });
    } finally {
      setBusy(null);
    }
  }

  /** The failure message for one action, or undefined if it was not the one. */
  const errorFor = (name: string) =>
    error?.action === name ? error.message : undefined;

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            {user.pharmacyName || user.displayName}
            <StatusDot active={user.isActive} />
          </DialogTitle>
          <DialogDescription>{user.email}</DialogDescription>
        </DialogHeader>

        {!canAct && (
          <p className="rounded-md bg-muted px-3 py-2 text-xs text-muted-foreground">
            This is a staff account. Only a SuperAdmin can change it.
          </p>
        )}

        <section className="grid grid-cols-2 gap-x-4 gap-y-3 text-sm">
          <Field label="Contact" value={user.displayName} />
          <Field label="Post code" value={user.postCode || "—"} />
          <Field label="Role" value={<RoleBadge role={user.role} />} />
          <Field label="Plan" value={<TierBadge tier={user.tier} />} />
          <Field label="Video credits" value={user.videoCredits.toLocaleString()} />
          <Field label="Billing" value={user.billingStatus || "—"} />
          <Field
            label="Onboarding"
            value={user.onboardingCompleted ? "Complete" : "Incomplete"}
          />
          <Field label="Account manager" value={user.managedByAdminName ?? "Unassigned"} />
          <Field label="Last sign-in" value={formatDate(user.lastLoginAtUtc)} />
          <Field label="Joined" value={formatDate(user.createdAtUtc)} />
          {user.currentPeriodEndUtc && (
            <Field label="Renews" value={formatDate(user.currentPeriodEndUtc)} />
          )}
        </section>

        {/*
          The way out of this drawer and into the read-only support view.
          Pharmacies only — a staff account has no patients or campaigns, and
          offering the link would lead to a page explaining that.
        */}
        {user.role === "PharmacyUser" && (
          <Button asChild variant="outline" size="sm" className="w-full gap-1.5 text-xs">
            <Link href={`/admin/pharmacies/${user.id}`} onClick={onClose}>
              <FolderSearch className="h-3.5 w-3.5" />
              View patients, posters and campaigns
            </Link>
          </Button>
        )}

        {canAct && (
          <div className="space-y-4 border-t border-border/60 pt-4">
            <Action label="Plan" error={errorFor("tier")}>
              <div className="flex gap-1.5">
                {TIERS.map((tier) => (
                  <Button
                    key={tier}
                    size="sm"
                    variant={user.tier === tier ? "default" : "outline"}
                    className="flex-1 text-xs"
                    disabled={busy !== null || user.tier === tier}
                    onClick={() =>
                      run("tier", () => api.admin.overrideTier(user.id, tier))
                    }
                  >
                    {tier}
                  </Button>
                ))}
              </div>
              <Hint>
                Changes what the product unlocks without touching Stripe. On an
                account with a live subscription, the next webhook overwrites it.
              </Hint>
            </Action>

            <Action label="Grant video credits" error={errorFor("credits")}>
              <div className="flex gap-1.5">
                <Input
                  type="number"
                  min={1}
                  max={1000}
                  value={credits}
                  onChange={(e) => setCredits(e.target.value)}
                  className="h-8 w-24 text-xs"
                />
                <Button
                  size="sm"
                  variant="outline"
                  className="text-xs"
                  disabled={busy !== null || !validCredits(credits)}
                  onClick={() =>
                    run("credits", () =>
                      api.admin.grantCredits(user.id, Number(credits)),
                    )
                  }
                >
                  {busy === "credits" ? "Granting…" : "Grant"}
                </Button>
              </div>
              <Hint>Between 1 and 1000. Added to the current balance.</Hint>
            </Action>

            {user.role === "PharmacyUser" && (
              <Action label="Head office" error={errorFor("head-office")}>
                <div className="flex items-center gap-2">
                  <Badge variant={user.isHeadOffice ? "default" : "secondary"}>
                    {user.isHeadOffice ? "Runs branches" : "Single pharmacy"}
                  </Badge>
                  <Button
                    size="sm"
                    variant="outline"
                    className="text-xs"
                    // A branch can never be granted this: the server keeps the
                    // hierarchy one level deep and refuses. Offering the button
                    // anyway produced the exact failure it was meant to
                    // prevent — an administrator pressing Grant three times on
                    // a branch and being told nothing.
                    disabled={busy !== null || user.isChildPharmacy}
                    title={
                      user.isChildPharmacy
                        ? "This pharmacy is a branch of another one."
                        : undefined
                    }
                    onClick={() =>
                      run("head-office", () =>
                        api.admin.setHeadOffice(user.id, !user.isHeadOffice),
                      )
                    }
                  >
                    {busy === "head-office"
                      ? "Saving…"
                      : user.isHeadOffice
                        ? "Withdraw"
                        : "Grant"}
                  </Button>
                </div>
                <Hint>
                  {user.isChildPharmacy
                    ? "This pharmacy is a branch of another one, so it cannot run branches of its own. Detach it from its head office first."
                    : "A head office can create branch accounts, read across them and switch into them. Withdrawing does not detach existing branches — it only stops new ones being added. Both are recorded in the audit log."}
                </Hint>
              </Action>
            )}

            {user.role === "PharmacyUser" && (
              <Action label="Account manager" error={errorFor("assign")}>
                <select
                  value={user.managedByAdminId ?? ""}
                  disabled={busy !== null}
                  onChange={(e) =>
                    run("assign", () =>
                      e.target.value
                        ? api.admin.assignUser(user.id, e.target.value)
                        : api.admin.unassignUser(user.id),
                    )
                  }
                  className="h-8 w-full rounded-md border border-input bg-background px-2 text-xs"
                >
                  <option value="">Unassigned</option>
                  {admins.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.displayName} ({a.managedUserCount})
                    </option>
                  ))}
                </select>
                <Hint>
                  Records who owns the relationship. It does not restrict access —
                  every administrator can act on every pharmacy.
                </Hint>
              </Action>
            )}

            {isSuperAdmin && !isSelf && (
              <Action label="Role" error={errorFor("role")}>
                <div className="flex gap-1.5">
                  {(["PharmacyUser", "Admin"] as UserRole[]).map((role) => (
                    <Button
                      key={role}
                      size="sm"
                      variant={user.role === role ? "default" : "outline"}
                      className="flex-1 text-xs"
                      disabled={busy !== null || user.role === role}
                      onClick={() =>
                        run("role", () => api.admin.changeRole(user.id, role))
                      }
                    >
                      {role === "PharmacyUser" ? "Pharmacy" : "Admin"}
                    </Button>
                  ))}
                </div>
                <Hint>
                  An admin with pharmacies still assigned cannot be demoted until
                  those are reassigned.
                </Hint>
              </Action>
            )}

            <Action label="Access" error={errorFor("active")}>
              <Button
                size="sm"
                variant={user.isActive ? "outline" : "default"}
                className={cn(
                  "w-full text-xs",
                  user.isActive && "text-destructive hover:text-destructive",
                )}
                disabled={busy !== null || isSelf}
                onClick={() =>
                  run("active", () =>
                    api.admin.setUserActive(user.id, !user.isActive),
                  )
                }
              >
                {busy === "active"
                  ? "Working…"
                  : user.isActive
                    ? "Deactivate account"
                    : "Reactivate account"}
              </Button>
              <Hint>
                {isSelf
                  ? "You cannot deactivate your own account."
                  : "Blocks sign-in immediately. Nothing is deleted."}
              </Hint>
            </Action>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

function Field({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <p className="text-[11px] uppercase tracking-wide text-muted-foreground">
        {label}
      </p>
      <div className="mt-0.5 truncate">{value}</div>
    </div>
  );
}

/**
 * One labelled control, with the refusal it produced shown underneath it.
 *
 * Putting the message here rather than at the top of the dialog is the whole
 * point: this dialog scrolls, and a message the operator has to scroll up to
 * find is one they will not see. They will press the button again instead.
 */
function Action({
  label,
  error,
  children,
}: {
  label: string;
  error?: string;
  children: React.ReactNode;
}) {
  const labelId = useId();

  return (
    // A labelled group, not a bare div.
    //
    // Two of these contain a button reading exactly "Grant" — head office and
    // video credits — so without a named container neither a screen reader nor
    // a test can say which "Grant" is which. `aria-labelledby` reuses the
    // visible label rather than repeating it in an `aria-label`.
    <div role="group" aria-labelledby={labelId} className="space-y-1.5">
      <Label id={labelId} className="text-xs">
        {label}
      </Label>
      {children}
      {error && (
        <p
          role="alert"
          className="rounded-md bg-destructive/10 px-2.5 py-1.5 text-[11px] text-destructive"
        >
          {error}
        </p>
      )}
    </div>
  );
}

function Hint({ children }: { children: React.ReactNode }) {
  return <p className="text-[11px] text-muted-foreground">{children}</p>;
}

/** Mirrors the server's 1–1000 bound so the button disables before the call. */
function validCredits(raw: string): boolean {
  const n = Number(raw);
  return Number.isInteger(n) && n >= 1 && n <= 1000;
}

"use client";

import { useCallback, useEffect, useState } from "react";
import { Check, Loader2, ShieldQuestion, X } from "lucide-react";
import type { AccessRequestForReview } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api";

/**
 * Deciding who may work as somebody else's pharmacy.
 *
 * Approving one of these lets an account read another pharmacy's patient list,
 * which puts it in the same category as a tier override or a contact reveal —
 * a platform action taken on a customer's behalf. Every decision lands in the
 * audit log with the note written here.
 *
 * The reason the requester gave is shown in full and unabridged, because it is
 * the only thing an administrator has to weigh. Truncating it to fit a row
 * would make the decision worse in exchange for a tidier table.
 */
export default function AccessRequestsPage() {
  const toast = useToast();
  const [items, setItems] = useState<AccessRequestForReview[] | null>(null);
  const [pendingOnly, setPendingOnly] = useState(true);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [notes, setNotes] = useState<Record<string, string>>({});

  const load = useCallback(async () => {
    setItems(await api.accessRequestQueue(pendingOnly).catch(() => []));
  }, [pendingOnly]);

  useEffect(() => {
    void load();
  }, [load]);

  async function decide(r: AccessRequestForReview, approve: boolean) {
    setBusyId(r.id);
    try {
      await api.reviewAccessRequest(r.id, approve, notes[r.id]);
      toast.success(
        approve ? "Access granted" : "Request rejected",
        approve
          ? `${r.userEmail} can now work as ${r.pharmacyName}.`
          : `${r.userEmail} has been told.`,
      );
      await load();
    } catch (e) {
      toast.error(
        "Could not record that",
        e instanceof Error ? e.message : "Please try again.",
      );
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-lg font-medium">Access requests</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            People asking to work as a pharmacy that is not their own. Approving one
            grants access to that pharmacy&rsquo;s contacts and campaigns.
          </p>
        </div>

        <Button variant="outline" size="sm" onClick={() => setPendingOnly((v) => !v)}>
          {pendingOnly ? "Show decided too" : "Show pending only"}
        </Button>
      </header>

      {items === null && (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading…
        </p>
      )}

      {items !== null && items.length === 0 && (
        <div className="rounded-lg border border-dashed border-border/70 px-6 py-12 text-center">
          <ShieldQuestion className="mx-auto h-8 w-8 text-muted-foreground" />
          <p className="mt-3 text-sm font-medium">
            {pendingOnly ? "Nothing waiting" : "No requests yet"}
          </p>
          <p className="mt-1 text-sm text-muted-foreground">
            {pendingOnly
              ? "Every request has been decided."
              : "Nobody has asked for access to another pharmacy."}
          </p>
        </div>
      )}

      <div className="space-y-3">
        {items?.map((r) => (
          <div key={r.id} className="rounded-lg border border-border/60 p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="text-sm">
                  <span className="font-medium">{r.userEmail}</span>
                  <span className="text-muted-foreground"> wants to work as </span>
                  <span className="font-medium">{r.pharmacyName}</span>
                </p>
                <p className="mt-0.5 text-xs text-muted-foreground">
                  {r.pharmacyEmail} · asked{" "}
                  {new Date(r.requestedAtUtc).toLocaleDateString()}
                </p>
              </div>

              <Status status={r.status} />
            </div>

            {/* Shown in full. It is the only thing there is to weigh. */}
            <p className="mt-3 rounded-md bg-muted/50 px-3 py-2 text-sm">{r.reason}</p>

            {r.status === "Pending" ? (
              <div className="mt-3 flex flex-wrap items-center gap-2">
                <Input
                  value={notes[r.id] ?? ""}
                  onChange={(e) =>
                    setNotes((n) => ({ ...n, [r.id]: e.target.value }))
                  }
                  placeholder="Note back to them (optional)"
                  className="h-9 min-w-[16rem] flex-1"
                />
                <Button
                  size="sm"
                  disabled={busyId !== null}
                  onClick={() => void decide(r, true)}
                >
                  {busyId === r.id ? (
                    <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />
                  ) : (
                    <Check className="mr-1.5 h-3.5 w-3.5" />
                  )}
                  Approve
                </Button>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={busyId !== null}
                  onClick={() => void decide(r, false)}
                >
                  <X className="mr-1.5 h-3.5 w-3.5" />
                  Reject
                </Button>
              </div>
            ) : (
              <p className="mt-3 text-xs text-muted-foreground">
                {r.status} by {r.reviewedByEmail ?? "an administrator"}
                {r.reviewedAtUtc
                  ? ` on ${new Date(r.reviewedAtUtc).toLocaleDateString()}`
                  : ""}
                {r.reviewNote ? ` — “${r.reviewNote}”` : ""}
              </p>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

function Status({ status }: { status: string }) {
  if (status === "Approved") return <Badge variant="secondary">Approved</Badge>;
  if (status === "Rejected") return <Badge variant="outline">Rejected</Badge>;
  return <Badge variant="outline">Pending</Badge>;
}

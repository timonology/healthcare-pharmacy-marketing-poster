"use client";

import { useState } from "react";
import type { AdminSummary } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";

export interface BulkResult {
  succeeded: number;
  failed: { id: string; label: string; reason: string }[];
}

/**
 * Runs an action over selected accounts, one request each.
 *
 * Sequential calls to the existing per-account endpoints rather than a new
 * batch endpoint. That is the whole point: every guard rail in AdminService —
 * cannot act on staff as an Admin, cannot deactivate yourself, cannot demote
 * the last SuperAdmin — is enforced per request. A bulk endpoint would have to
 * re-implement those checks, and any divergence becomes a way to do in bulk
 * what is forbidden individually.
 *
 * Failures do not abort the run. Acting on twenty accounts and stopping at the
 * third leaves the operator with no idea which of the twenty changed; instead
 * every item is attempted and the refusals are reported together.
 */
async function runBulk<T extends { id: string }>(
  items: T[],
  label: (item: T) => string,
  action: (item: T) => Promise<unknown>,
): Promise<BulkResult> {
  const result: BulkResult = { succeeded: 0, failed: [] };

  for (const item of items) {
    try {
      await action(item);
      result.succeeded++;
    } catch (err) {
      result.failed.push({
        id: item.id,
        label: label(item),
        reason: err instanceof Error ? err.message : "Refused.",
      });
    }
  }

  return result;
}

export interface BulkTarget {
  id: string;
  label: string;
  isActive: boolean;
}

/**
 * Action bar shown when rows are selected.
 */
export function BulkBar({
  selected,
  admins,
  onDone,
  onClear,
}: {
  selected: BulkTarget[];
  /** Offer "assign manager" when provided. */
  admins?: AdminSummary[];
  onDone: (result: BulkResult) => void;
  onClear: () => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);

  if (selected.length === 0) return null;

  const activeCount = selected.filter((s) => s.isActive).length;
  const inactiveCount = selected.length - activeCount;

  async function perform(
    name: string,
    action: (t: BulkTarget) => Promise<unknown>,
    subset: BulkTarget[] = selected,
  ) {
    setBusy(name);
    try {
      onDone(await runBulk(subset, (t) => t.label, action));
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="flex flex-wrap items-center gap-2 border-b border-border/60 bg-accent/40 px-3 py-2">
      <span className="text-xs font-medium">
        {selected.length} selected
      </span>

      {inactiveCount > 0 && (
        <Button
          size="sm"
          variant="outline"
          className="h-7 text-xs"
          disabled={busy !== null}
          onClick={() =>
            perform(
              "activate",
              (t) => api.admin.setUserActive(t.id, true),
              selected.filter((s) => !s.isActive),
            )
          }
        >
          {busy === "activate" ? "Working…" : `Activate ${inactiveCount}`}
        </Button>
      )}

      {activeCount > 0 && (
        <Button
          size="sm"
          variant="outline"
          className="h-7 text-xs text-destructive hover:text-destructive"
          disabled={busy !== null}
          onClick={() => {
            if (
              !confirm(
                `Deactivate ${activeCount} account${activeCount > 1 ? "s" : ""}? ` +
                  "They will be signed out and unable to sign in again.",
              )
            ) {
              return;
            }
            void perform(
              "deactivate",
              (t) => api.admin.setUserActive(t.id, false),
              selected.filter((s) => s.isActive),
            );
          }}
        >
          {busy === "deactivate" ? "Working…" : `Deactivate ${activeCount}`}
        </Button>
      )}

      {admins && admins.length > 0 && (
        <select
          defaultValue=""
          disabled={busy !== null}
          onChange={(e) => {
            const adminId = e.target.value;
            e.currentTarget.selectedIndex = 0;
            if (!adminId) return;
            void perform("assign", (t) =>
              adminId === "__none"
                ? api.admin.unassignUser(t.id)
                : api.admin.assignUser(t.id, adminId),
            );
          }}
          className="h-7 rounded-md border border-input bg-background px-2 text-xs"
          aria-label="Assign account manager to selected"
        >
          <option value="">Assign manager…</option>
          <option value="__none">Unassign</option>
          {admins.map((a) => (
            <option key={a.id} value={a.id}>
              {a.displayName}
            </option>
          ))}
        </select>
      )}

      <button
        type="button"
        onClick={onClear}
        disabled={busy !== null}
        className="ml-auto text-xs text-muted-foreground hover:underline"
      >
        Clear selection
      </button>
    </div>
  );
}

/**
 * Summary of a bulk run.
 *
 * Refusals are listed individually with the server's own wording. "3 of 20
 * failed" is not actionable; "Ada Lovelace — Only a SuperAdmin can manage
 * administrator accounts" tells you both what happened and why.
 */
export function BulkResultNote({
  result,
  onDismiss,
}: {
  result: BulkResult;
  onDismiss: () => void;
}) {
  const hasFailures = result.failed.length > 0;

  return (
    <div
      className={`rounded-md border px-3 py-2 text-xs ${
        hasFailures
          ? "border-destructive/40 bg-destructive/5"
          : "border-primary/40 bg-primary/5"
      }`}
    >
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1">
          <p className="font-medium">
            {result.succeeded} updated
            {hasFailures && `, ${result.failed.length} refused`}
          </p>
          {hasFailures && (
            <ul className="mt-1 space-y-0.5 text-muted-foreground">
              {result.failed.slice(0, 5).map((f) => (
                <li key={f.id}>
                  <span className="font-medium">{f.label}</span> — {f.reason}
                </li>
              ))}
              {result.failed.length > 5 && (
                <li>…and {result.failed.length - 5} more.</li>
              )}
            </ul>
          )}
        </div>
        <button
          type="button"
          onClick={onDismiss}
          className="shrink-0 text-muted-foreground hover:text-foreground"
          aria-label="Dismiss"
        >
          ×
        </button>
      </div>
    </div>
  );
}

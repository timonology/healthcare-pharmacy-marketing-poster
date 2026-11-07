"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { ArrowRight, Upload } from "lucide-react";
import type {
  AdminSummary,
  AdminUser,
  UserSortOption,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";
import { NoResultsInRange, SortDateControls } from "@/components/lists/SortDateControls";
import { useListWindow } from "@/hooks/useListWindow";
import { hasDateRange, listWindowKey, listWindowParams, type ListWindow } from "@/lib/list-window";
import { useAuthStore } from "@/store/auth-store";
import { AccountDrawer } from "@/components/admin/AccountDrawer";
import {
  BulkBar,
  BulkResultNote,
  type BulkResult,
} from "@/components/admin/BulkBar";
import {
  ErrorNote,
  Pagination,
  Panel,
  RoleBadge,
  SortHeader,
  StatusDot,
  TableEmpty,
  TableLoading,
  TierBadge,
  formatDate,
} from "@/components/admin/AdminUi";

const TAKE = 25;

export default function AdminPharmaciesPage() {
  const role = useAuthStore((s) => s.user?.role);
  const actorId = useAuthStore((s) => s.user?.id);
  const isSuperAdmin = role === "SuperAdmin";

  const [rows, setRows] = useState<AdminUser[]>([]);
  const [admins, setAdmins] = useState<AdminSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [skip, setSkip] = useState(0);
  const [search, setSearch] = useState("");
  const [managedBy, setManagedBy] = useState("");
  const [unassignedOnly, setUnassignedOnly] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const [onboardedFilter, setOnboardedFilter] = useState<"" | "yes" | "no">("");
  // Sort and account-created range: in the URL, applied by the API. A column
  // header overrides the date order until the Sort control is used again.
  const { listWindow, setListWindow, ready } = useListWindow();
  const windowKey = listWindowKey(listWindow);
  const [columnSort, setColumnSort] = useState<UserSortOption | null>(null);
  const sort: UserSortOption =
    columnSort ?? (listWindow.sort === "oldest" ? "OldestFirst" : "NewestFirst");
  const setSort = setColumnSort;

  function changeWindow(next: ListWindow) {
    setSkip(0);
    if (next.sort !== listWindow.sort) setColumnSort(null);
    setListWindow(next);
  }

  /** Every filter back to its default. Sort is a preference and stays. */
  function clearFilters() {
    setSkip(0);
    setSearch("");
    setManagedBy("");
    setUnassignedOnly(false);
    setOnboardedFilter("");
    setListWindow({ ...listWindow, from: null, to: null });
  }
  const [detail, setDetail] = useState<AdminUser | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [bulkResult, setBulkResult] = useState<BulkResult | null>(null);

  /**
   * Reloads the page of pharmacies, and hands back what it just fetched.
   *
   * Returning the rows is not a convenience. A caller that needs the fresh
   * data — the drawer, re-reading the record it has open — cannot get it from
   * `rows` or from a ref that mirrors `rows`, because both are only updated
   * when React re-renders, which has not happened yet on the line after
   * `await load()`. Reading it there gets the *previous* page, which is why
   * every drawer action appeared to need pressing twice.
   */
  const load = useCallback(async (): Promise<AdminUser[]> => {
    setLoading(true);
    try {
      const page = await api.admin.listUsers({
        skip,
        take: TAKE,
        // Not `role: "PharmacyUser"`. Promoting a pharmacy owner to Admin does
        // not close their pharmacy, and filtering by role made those accounts
        // disappear from the only page that lists customers.
        pharmacyAccounts: true,
        sort,
        from: listWindowParams(listWindow).from,
        to: listWindowParams(listWindow).to,
        search: search.trim() || undefined,
        managedBy: managedBy || undefined,
        unassignedOnly: unassignedOnly || undefined,
        onboarded: onboardedFilter ? onboardedFilter === "yes" : undefined,
      });
      setRows(page.items);
      setTotal(page.total);
      setError(null);
      // Per-page selection: ids for rows that are no longer shown must not
      // survive into a bulk action.
      setSelectedIds(new Set());
      return page.items;
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load pharmacies.");
      return [];
    } finally {
      setLoading(false);
    }
    // listWindow is covered by windowKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [skip, search, managedBy, unassignedOnly, onboardedFilter, sort, windowKey]);

  const selectedTargets = rows
    .filter((r) => selectedIds.has(r.id))
    .map((r) => ({
      id: r.id,
      label: r.pharmacyName || r.displayName,
      isActive: r.isActive,
    }));

  const allSelected = rows.length > 0 && selectedIds.size === rows.length;
  const someSelected = selectedIds.size > 0;

  useEffect(() => {
    // Not before the URL is read, so a shared link fetches once.
    if (!ready) return;
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load, ready]);

  useEffect(() => {
    if (!isSuperAdmin) return;
    api.admin.listAdmins().then(setAdmins).catch(() => setAdmins([]));
  }, [isSuperAdmin]);

  async function reassign(userId: string, adminId: string | null) {
    setBusyId(userId);
    setError(null);
    try {
      if (adminId) await api.admin.assignUser(userId, adminId);
      else await api.admin.unassignUser(userId);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not update assignment.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      {/*
        This page lists every account that HAS a pharmacy, not every account
        whose role happens to be PharmacyUser. The two diverge the moment a
        pharmacy owner is given an admin role, and the old role-based filter
        made their pharmacy vanish from the only page that lists customers —
        which reads as data loss, not as a filter working.

        Staff accounts with no pharmacy of their own are still excluded; they
        are under Users.
      */}
      <Panel
        title={loading ? "Pharmacies" : `Pharmacies (${total})`}
        description={
          "Every account with a pharmacy, including any held by a colleague " +
          "who also has an admin role — those are marked. " +
          (isSuperAdmin
            ? "Shows the administrator responsible for each account."
            : "Shows the administrator who owns each relationship.")
        }
        action={
          // Where people look for it. Importing lived only under a tab named
          // "Invitations", and a super admin testing with stakeholders looked
          // here, on the list of pharmacies, and reported it missing.
          isSuperAdmin ? (
            <Button asChild size="sm" className="h-8 gap-1.5 text-xs">
              <Link href="/admin/invitations">
                <Upload className="h-3.5 w-3.5" />
                Add or import pharmacies
              </Link>
            </Button>
          ) : undefined
        }
      >
        <div className="flex flex-wrap items-center gap-2 border-b border-border/60 p-3">
          <Input
            value={search}
            onChange={(e) => {
              setSkip(0);
              setSearch(e.target.value);
            }}
            placeholder="Search pharmacy, contact, or postcode"
            className="h-9 max-w-xs"
          />

          {isSuperAdmin && (
            <>
              <select
                value={managedBy}
                onChange={(e) => {
                  setSkip(0);
                  setUnassignedOnly(false);
                  setManagedBy(e.target.value);
                }}
                className="h-9 rounded-md border border-border/60 bg-background px-2 text-sm"
              >
                <option value="">All administrators</option>
                {admins.map((a) => (
                  <option key={a.id} value={a.id}>{a.displayName}</option>
                ))}
              </select>

              <label className="flex items-center gap-1.5 text-sm text-muted-foreground">
                <input
                  type="checkbox"
                  checked={unassignedOnly}
                  onChange={(e) => {
                    setSkip(0);
                    setManagedBy("");
                    setUnassignedOnly(e.target.checked);
                  }}
                />
                Unassigned only
              </label>
            </>
          )}

          <select
            value={onboardedFilter}
            onChange={(e) => {
              setSkip(0);
              setOnboardedFilter(e.target.value as "" | "yes" | "no");
            }}
            className="h-9 rounded-md border border-border/60 bg-background px-2 text-sm"
            aria-label="Filter by onboarding"
          >
            <option value="">Any onboarding</option>
            <option value="yes">Onboarded</option>
            <option value="no">Not onboarded</option>
          </select>

          <SortDateControls value={listWindow} onChange={changeWindow} dateField="Account created" />
        </div>

        {bulkResult && (
          <div className="p-3">
            <BulkResultNote result={bulkResult} onDismiss={() => setBulkResult(null)} />
          </div>
        )}

        <BulkBar
          selected={selectedTargets}
          admins={isSuperAdmin ? admins : undefined}
          onClear={() => setSelectedIds(new Set())}
          onDone={(r) => {
            setBulkResult(r);
            void load();
          }}
        />

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <tr>
                <th className="w-10 px-4 py-2">
                  <input
                    type="checkbox"
                    aria-label="Select all on this page"
                    checked={allSelected}
                    ref={(el) => {
                      if (el) el.indeterminate = someSelected && !allSelected;
                    }}
                    onChange={(e) =>
                      setSelectedIds(
                        e.target.checked ? new Set(rows.map((r) => r.id)) : new Set(),
                      )
                    }
                    className="h-3.5 w-3.5 cursor-pointer accent-primary"
                  />
                </th>
                <SortHeader
                  label="Pharmacy"
                  asc="NameAsc"
                  desc="NameDesc"
                  current={sort}
                  onSort={(v) => {
                    setSkip(0);
                    setSort(v);
                  }}
                />
                <th className="px-4 py-2 font-medium">Contact</th>
                <SortHeader
                  label="Plan"
                  asc="TierDesc"
                  current={sort}
                  onSort={(v) => {
                    setSkip(0);
                    setSort(v);
                  }}
                />
                <th className="px-4 py-2 font-medium">Administrator</th>
                <th className="px-4 py-2 font-medium">Onboarded</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <SortHeader
                  label="Joined"
                  asc="NewestFirst"
                  desc="OldestFirst"
                  current={sort}
                  onSort={(v) => {
                    setSkip(0);
                    setSort(v);
                  }}
                />
                <th className="px-4 py-2 text-right font-medium">
                  <span className="sr-only">Open</span>
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/60">
              {loading ? (
                <TableLoading colSpan={9} />
              ) : rows.length === 0 && hasDateRange(listWindow) ? (
                <tr>
                  <td colSpan={9} className="p-4">
                    <NoResultsInRange onClear={clearFilters} className="border-0" />
                  </td>
                </tr>
              ) : rows.length === 0 ? (
                <TableEmpty colSpan={9} message="No pharmacies match these filters." />
              ) : (
                rows.map((u) => (
                  <tr key={u.id} className="hover:bg-accent/40">
                    <td className="px-4 py-2.5">
                      <input
                        type="checkbox"
                        aria-label={`Select ${u.pharmacyName || u.displayName}`}
                        checked={selectedIds.has(u.id)}
                        onChange={(e) =>
                          setSelectedIds((prev) => {
                            const next = new Set(prev);
                            if (e.target.checked) next.add(u.id);
                            else next.delete(u.id);
                            return next;
                          })
                        }
                        className="h-3.5 w-3.5 cursor-pointer accent-primary"
                      />
                    </td>
                    <td className="px-4 py-2.5">
                      <div className="flex flex-wrap items-center gap-1.5">
                        <button
                          type="button"
                          onClick={() => setDetail(u)}
                          className="text-left font-medium hover:text-primary hover:underline"
                        >
                          {u.pharmacyName || "—"}
                        </button>
                        {/*
                          A pharmacy whose owner also holds a staff role. Shown
                          rather than hidden: it explains why the same name
                          appears under Users, and it is the thing to check
                          first when someone's permissions look surprising.
                        */}
                        {u.role !== "PharmacyUser" && (
                          <RoleBadge role={u.role} />
                        )}
                      </div>
                      {u.postCode && (
                        <p className="text-xs text-muted-foreground">{u.postCode}</p>
                      )}
                    </td>
                    <td className="px-4 py-2.5">
                      <p>{u.displayName}</p>
                      <p className="text-xs text-muted-foreground">{u.email}</p>
                    </td>
                    <td className="px-4 py-2.5"><TierBadge tier={u.tier} /></td>
                    <td className="px-4 py-2.5">
                      {isSuperAdmin ? (
                        <select
                          value={u.managedByAdminId ?? ""}
                          disabled={busyId === u.id}
                          onChange={(e) => reassign(u.id, e.target.value || null)}
                          className="h-8 rounded-md border border-border/60 bg-background px-2 text-xs"
                          aria-label={`Administrator for ${u.pharmacyName || u.displayName}`}
                        >
                          <option value="">Unassigned</option>
                          {admins
                            .filter((a) => a.isActive || a.id === u.managedByAdminId)
                            .map((a) => (
                              <option key={a.id} value={a.id}>{a.displayName}</option>
                            ))}
                        </select>
                      ) : (
                        <span className="text-xs text-muted-foreground">
                          {u.managedByAdminName ?? "Unassigned"}
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {u.onboardingCompleted ? "Yes" : "Not yet"}
                    </td>
                    <td className="px-4 py-2.5"><StatusDot active={u.isActive} /></td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {formatDate(u.createdAtUtc)}
                    </td>
                    {/*
                      Two different destinations from one row, kept visibly
                      distinct. The name opens the account drawer, which is
                      where things are *changed*. This opens the support view,
                      which only reads. Collapsing them into one click would
                      make "I just wanted to look" and "I changed their plan"
                      the same gesture.
                    */}
                    <td className="px-4 py-2.5 text-right">
                      <Button
                        asChild
                        size="sm"
                        variant="ghost"
                        className="h-7 gap-1 text-xs"
                      >
                        <Link href={`/admin/pharmacies/${u.id}`}>
                          View data
                          <ArrowRight className="h-3.5 w-3.5" />
                        </Link>
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

      {isSuperAdmin && (
        <div className="flex justify-end">
          <Button
            size="sm"
            variant="ghost"
            onClick={() => {
              setSkip(0);
              setManagedBy("");
              setUnassignedOnly(true);
            }}
          >
            Show pharmacies with no administrator →
          </Button>
        </div>
      )}

      <AccountDrawer
        user={detail}
        admins={admins}
        actorRole={role}
        actorId={actorId}
        onClose={() => setDetail(null)}
        onChanged={async () => {
          // Re-seed the open drawer from the rows this reload returned, not
          // from state or a ref — neither has been updated yet at this point.
          const fresh = await load();
          setDetail((current) =>
            current ? fresh.find((r) => r.id === current.id) ?? null : null,
          );
        }}
      />
    </div>
  );
}

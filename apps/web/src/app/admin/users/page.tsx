"use client";

import { useCallback, useEffect, useState } from "react";
import type {
  AdminSummary,
  AdminUser,
  AdminUserFilters,
  SubscriptionTier,
  UserRole,
  UserSortOption,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";
import { NoResultsInRange, SortDateControls } from "@/components/lists/SortDateControls";
import { useListWindow } from "@/hooks/useListWindow";
import { hasDateRange, listWindowKey, listWindowParams, type ListWindow } from "@/lib/list-window";
import { cn } from "@/lib/utils";
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

export default function AdminUsersPage() {
  const role = useAuthStore((s) => s.user?.role);
  const actorId = useAuthStore((s) => s.user?.id);
  const isSuperAdmin = role === "SuperAdmin";

  const [rows, setRows] = useState<AdminUser[]>([]);
  const [total, setTotal] = useState(0);
  const [skip, setSkip] = useState(0);
  const [search, setSearch] = useState("");
  const [roleFilter, setRoleFilter] = useState<UserRole | "">("");
  const [tierFilter, setTierFilter] = useState<SubscriptionTier | "">("");
  const [statusFilter, setStatusFilter] = useState<"" | "active" | "inactive">("");
  const [admins, setAdmins] = useState<AdminSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [roleTarget, setRoleTarget] = useState<AdminUser | null>(null);

  const [onboardedFilter, setOnboardedFilter] = useState<"" | "yes" | "no">("");
  // Sort and account-created range: in the URL, applied by the API. A column
  // header (name, plan, last login) overrides the date order until the Sort
  // control is used again.
  const { listWindow, setListWindow, ready } = useListWindow();
  const windowKey = listWindowKey(listWindow);
  const [columnSort, setColumnSort] = useState<UserSortOption | null>(null);
  const sort: UserSortOption =
    columnSort ?? (listWindow.sort === "oldest" ? "OldestFirst" : "NewestFirst");

  function changeWindow(next: ListWindow) {
    setSkip(0);
    if (next.sort !== listWindow.sort) setColumnSort(null);
    setListWindow(next);
  }

  /** Every filter back to its default. Sort is a preference and stays. */
  function clearFilters() {
    setSkip(0);
    setSearch("");
    setRoleFilter("");
    setTierFilter("");
    setStatusFilter("");
    setOnboardedFilter("");
    setListWindow({ ...listWindow, from: null, to: null });
  }
  const [detail, setDetail] = useState<AdminUser | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [bulkResult, setBulkResult] = useState<BulkResult | null>(null);

  /**
   * Reloads the page of users, and hands back what it just fetched.
   *
   * See the note where the drawer consumes this: a caller on the line after
   * `await load()` cannot read the fresh rows from state or from a ref that
   * mirrors state, because React has not re-rendered yet.
   */
  const load = useCallback(async (): Promise<AdminUser[]> => {
    setLoading(true);
    setError(null);
    try {
      const range = listWindowParams(listWindow);
      const filters: AdminUserFilters = { skip, take: TAKE, sort, from: range.from, to: range.to };
      if (search.trim()) filters.search = search.trim();
      if (roleFilter) filters.role = roleFilter;
      if (tierFilter) filters.tier = tierFilter;
      if (statusFilter) filters.isActive = statusFilter === "active";
      if (onboardedFilter) filters.onboarded = onboardedFilter === "yes";

      const page = await api.admin.listUsers(filters);
      setRows(page.items);
      setTotal(page.total);
      // Selection is per-page: keeping ids that are no longer visible would
      // let a bulk action hit rows the operator can't see.
      setSelectedIds(new Set());
      return page.items;
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load users.");
      return [];
    } finally {
      setLoading(false);
    }
    // listWindow is covered by windowKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [skip, search, roleFilter, tierFilter, statusFilter, onboardedFilter, sort, windowKey]);

  // Debounced so typing in the search box doesn't fire a request per keystroke.
  // Not before the URL is read, so a shared link fetches once.
  useEffect(() => {
    if (!ready) return;
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load, ready]);

  // Every administrator needs this to populate the account-manager picker.
  useEffect(() => {
    api.admin.listAdmins().then(setAdmins).catch(() => setAdmins([]));
  }, []);


  async function act(id: string, fn: () => Promise<unknown>) {
    setBusyId(id);
    setError(null);
    try {
      await fn();
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Action failed.");
    } finally {
      setBusyId(null);
    }
  }

  function resetPaging<T>(setter: (v: T) => void) {
    return (value: T) => {
      setSkip(0);
      setter(value);
    };
  }

  // Bulk targets are derived from the rows currently rendered, so a selected
  // id can never outlive the page it came from.
  const selectedTargets = rows
    .filter((r) => selectedIds.has(r.id))
    .map((r) => ({ id: r.id, label: r.displayName, isActive: r.isActive }));

  const allSelected = rows.length > 0 && selectedIds.size === rows.length;
  const someSelected = selectedIds.size > 0;

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title="Users"
        description="Every account on the platform."
      >
        <div className="flex flex-wrap gap-2 border-b border-border/60 p-3">
          <Input
            value={search}
            onChange={(e) => resetPaging(setSearch)(e.target.value)}
            placeholder="Search name, email, or pharmacy"
            className="h-9 max-w-xs"
          />

          {(
            <Select
              value={roleFilter}
              onChange={(v) => resetPaging(setRoleFilter)(v as UserRole | "")}
              options={[
                { value: "", label: "All roles" },
                { value: "PharmacyUser", label: "Pharmacy" },
                { value: "Admin", label: "Admin" },
                { value: "SuperAdmin", label: "SuperAdmin" },
              ]}
            />
          )}

          <Select
            value={tierFilter}
            onChange={(v) => resetPaging(setTierFilter)(v as SubscriptionTier | "")}
            options={[
              { value: "", label: "All plans" },
              { value: "Free", label: "Free" },
              { value: "Starter", label: "Starter" },
              { value: "Pro", label: "Pro" },
            ]}
          />

          <Select
            value={statusFilter}
            onChange={(v) => resetPaging(setStatusFilter)(v as "" | "active" | "inactive")}
            options={[
              { value: "", label: "Any status" },
              { value: "active", label: "Active" },
              { value: "inactive", label: "Deactivated" },
            ]}
          />

          <Select
            value={onboardedFilter}
            onChange={(v) => resetPaging(setOnboardedFilter)(v as "" | "yes" | "no")}
            options={[
              { value: "", label: "Any onboarding" },
              { value: "yes", label: "Onboarded" },
              { value: "no", label: "Not onboarded" },
            ]}
          />

          <SortDateControls value={listWindow} onChange={changeWindow} dateField="Account created" />
        </div>

        {bulkResult && (
          <div className="p-3">
            <BulkResultNote result={bulkResult} onDismiss={() => setBulkResult(null)} />
          </div>
        )}

        <BulkBar
          selected={selectedTargets}
          admins={admins}
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
                      // Indeterminate can only be set imperatively; it signals
                      // "some but not all", which a bare checkbox cannot.
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
                  label="User"
                  asc="NameAsc"
                  desc="NameDesc"
                  current={sort}
                  onSort={(v) => resetPaging(setColumnSort)(v)}
                />
                <th className="px-4 py-2 font-medium">Pharmacy</th>
                <th className="px-4 py-2 font-medium">Role</th>
                <SortHeader
                  label="Plan"
                  asc="TierDesc"
                  current={sort}
                  onSort={(v) => resetPaging(setColumnSort)(v)}
                />
                <th className="px-4 py-2 font-medium">Managed by</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <SortHeader
                  label="Last login"
                  asc="LastLoginDesc"
                  current={sort}
                  onSort={(v) => resetPaging(setColumnSort)(v)}
                />
                <th className="px-4 py-2 text-right font-medium">Actions</th>
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
                <TableEmpty colSpan={9} message="No users match these filters." />
              ) : (
                rows.map((u) => (
                  <tr key={u.id} className="hover:bg-accent/40">
                    <td className="px-4 py-2.5">
                      <input
                        type="checkbox"
                        aria-label={`Select ${u.displayName}`}
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
                      <button
                        type="button"
                        onClick={() => setDetail(u)}
                        className="text-left font-medium hover:text-primary hover:underline"
                      >
                        {u.displayName}
                      </button>
                      <p className="text-xs text-muted-foreground">{u.email}</p>
                    </td>
                    <td className="px-4 py-2.5">
                      <p>{u.pharmacyName || "—"}</p>
                      {u.postCode && (
                        <p className="text-xs text-muted-foreground">{u.postCode}</p>
                      )}
                    </td>
                    <td className="px-4 py-2.5"><RoleBadge role={u.role} /></td>
                    <td className="px-4 py-2.5">
                      <TierBadge tier={u.tier} />
                      {u.videoCredits > 0 && (
                        <span className="ml-1 text-[11px] text-muted-foreground">
                          +{u.videoCredits}cr
                        </span>
                      )}
                    </td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {u.managedByAdminName ?? (u.role === "PharmacyUser" ? "Unassigned" : "—")}
                    </td>
                    <td className="px-4 py-2.5"><StatusDot active={u.isActive} /></td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {formatDate(u.lastLoginAtUtc)}
                    </td>
                    <td className="px-4 py-2.5">
                      <div className="flex justify-end gap-1">
                        {u.role === "PharmacyUser" && (
                          <AssignControl
                            admins={admins}
                            current={u.managedByAdminId}
                            disabled={busyId === u.id}
                            onAssign={(adminId) =>
                              act(u.id, () =>
                                adminId
                                  ? api.admin.assignUser(u.id, adminId)
                                  : api.admin.unassignUser(u.id),
                              )
                            }
                          />
                        )}
                        {isSuperAdmin && u.role !== "SuperAdmin" && (
                          <Button
                            size="sm"
                            variant="ghost"
                            disabled={busyId === u.id}
                            onClick={() => setRoleTarget(u)}
                          >
                            {u.role === "Admin" ? "Change role" : "Make admin"}
                          </Button>
                        )}
                        {u.role !== "SuperAdmin" && (
                          <Button
                            size="sm"
                            variant="ghost"
                            disabled={busyId === u.id}
                            onClick={() =>
                              act(u.id, () => api.admin.setUserActive(u.id, !u.isActive))
                            }
                          >
                            {u.isActive ? "Deactivate" : "Reactivate"}
                          </Button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        <Pagination skip={skip} take={TAKE} total={total} onChange={setSkip} />
      </Panel>

      <AccountDrawer
        user={detail}
        admins={admins}
        actorRole={role}
        actorId={actorId}
        onClose={() => setDetail(null)}
        onChanged={async () => {
          // Re-read the open record from the rows this reload returned, so the
          // drawer reflects what just changed instead of showing stale values.
          //
          // This used to read a ref that mirrored `rows` from an effect. The
          // ref has the same problem as the state it mirrors: the effect that
          // fills it runs after the next render, which is after this line. So
          // the drawer was always re-seeded with the record from *before* the
          // change, and every action looked like it needed pressing twice.
          const fresh = await load();
          setDetail((current) =>
            current ? fresh.find((r) => r.id === current.id) ?? null : null,
          );
        }}
      />

      {roleTarget && (
        <ChangeRoleDialog
          user={roleTarget}
          onClose={() => setRoleTarget(null)}
          onSaved={async () => {
            setRoleTarget(null);
            await load();
          }}
          onError={setError}
        />
      )}
    </div>
  );
}

/**
 * Role changes get an explicit confirmation rather than an inline dropdown.
 * Granting admin hands someone access to every pharmacy assigned to them —
 * that shouldn't be one mis-click away, and the consequences are worth
 * spelling out at the moment of decision.
 */
function ChangeRoleDialog({
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
  const [role, setRole] = useState<UserRole>(user.role);
  const [saving, setSaving] = useState(false);

  const promoting = user.role === "PharmacyUser" && role === "Admin";
  const demoting = user.role === "Admin" && role === "PharmacyUser";
  const changed = role !== user.role;

  async function save() {
    setSaving(true);
    try {
      await api.admin.changeRole(user.id, role);
      onSaved();
    } catch (err) {
      onError(err instanceof Error ? err.message : "Could not change the role.");
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-foreground/40 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-md rounded-lg border border-border/60 bg-card p-5 shadow-xl">
        <h2 className="text-sm font-semibold tracking-tight">Change role</h2>
        <p className="mt-0.5 text-xs text-muted-foreground">
          {user.displayName} · {user.email}
        </p>

        <div className="mt-4 space-y-2">
          {(["PharmacyUser", "Admin"] as const).map((option) => (
            <label
              key={option}
              className={cn(
                "flex cursor-pointer items-start gap-3 rounded-lg border p-3 transition-colors",
                role === option
                  ? "border-primary/40 bg-primary/5"
                  : "border-border/60 hover:bg-accent",
              )}
            >
              <input
                type="radio"
                name="role"
                className="mt-1"
                checked={role === option}
                onChange={() => setRole(option)}
              />
              <span className="min-w-0">
                <span className="block text-sm font-medium">
                  {option === "Admin" ? "Administrator" : "Pharmacy user"}
                </span>
                <span className="block text-xs text-muted-foreground">
                  {option === "Admin"
                    ? "Can manage the pharmacies assigned to them, including plans and account status."
                    : "Standard customer account. No access to the admin console."}
                </span>
              </span>
            </label>
          ))}
        </div>

        {promoting && (
          <p className="mt-3 rounded-md border border-amber-500/30 bg-amber-500/5 p-2.5 text-xs">
            They&apos;ll be able to deactivate accounts and change plans for any
            pharmacy assigned to them. Assign pharmacies from the Pharmacies tab
            once this is done — a new admin starts with none.
          </p>
        )}

        {demoting && (
          <p className="mt-3 rounded-md border border-amber-500/30 bg-amber-500/5 p-2.5 text-xs">
            If they still manage any pharmacies this will be refused. Reassign
            those first, or the pharmacies would be left with no administrator.
          </p>
        )}

        <div className="mt-5 flex justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button size="sm" onClick={save} disabled={saving || !changed}>
            {saving ? "Saving…" : "Change role"}
          </Button>
        </div>
      </div>
    </div>
  );
}

function AssignControl({
  admins,
  current,
  disabled,
  onAssign,
}: {
  admins: AdminSummary[];
  current: string | null;
  disabled: boolean;
  onAssign: (adminId: string | null) => void;
}) {
  return (
    <select
      value={current ?? ""}
      disabled={disabled}
      onChange={(e) => onAssign(e.target.value || null)}
      className="h-8 rounded-md border border-border/60 bg-background px-2 text-xs"
      aria-label="Assign to administrator"
    >
      <option value="">Unassigned</option>
      {admins
        .filter((a) => a.isActive)
        .map((a) => (
          <option key={a.id} value={a.id}>
            {a.displayName}
          </option>
        ))}
    </select>
  );
}

function Select({
  value,
  onChange,
  options,
}: {
  value: string;
  onChange: (value: string) => void;
  options: { value: string; label: string }[];
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="h-9 rounded-md border border-border/60 bg-background px-2 text-sm"
    >
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}

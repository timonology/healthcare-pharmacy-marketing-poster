"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, FolderPlus, Pencil, ShieldAlert, Trash2 } from "lucide-react";
import type { GlobalGroup } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import { EmptyState, ErrorNote, ListSkeleton, Panel, formatDate } from "@/components/admin/AdminUi";

/**
 * General patient groups — shared definitions every pharmacy can file their
 * own patients under. SuperAdmin only.
 *
 * The shared thing is the *definition*, not the membership. Each pharmacy sees
 * only their own patients in a group and cannot tell whether anyone else uses
 * it. The platform-wide totals shown here never leave this page.
 */
export default function AdminGlobalGroupsPage() {
  const role = useAuthStore((s) => s.user?.role);
  const isSuperAdmin = role === "SuperAdmin";

  const [groups, setGroups] = useState<GlobalGroup[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [editing, setEditing] = useState<GlobalGroup | "new" | null>(null);

  const load = useCallback(async () => {
    try {
      setGroups(await api.admin.globalGroups.list());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load general groups.");
    }
  }, []);

  useEffect(() => {
    if (isSuperAdmin) void load();
  }, [isSuperAdmin, load]);

  async function remove(group: GlobalGroup) {
    // Spelled out rather than a bare "are you sure". This reaches into data
    // owned by other accounts, and the number makes the scale concrete.
    const warning =
      group.totalPatients > 0
        ? `Delete "${group.name}"?\n\nIt will be removed from ${group.totalPatients} patient record(s) across every pharmacy. This cannot be undone.`
        : `Delete "${group.name}"? This cannot be undone.`;

    if (!window.confirm(warning)) return;

    setBusy(group.id);
    try {
      await api.admin.globalGroups.remove(group.id);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not delete that group.");
    } finally {
      setBusy(null);
    }
  }

  if (!isSuperAdmin) {
    return (
      <div className="flex flex-col items-center gap-2 rounded-lg border border-border/60 bg-card px-6 py-16 text-center">
        <ShieldAlert className="h-8 w-8 text-muted-foreground" aria-hidden />
        <p className="mt-2 text-sm font-medium">Super admins only</p>
        <p className="max-w-sm text-xs text-muted-foreground">
          A general group appears in every pharmacy&apos;s list, so only super
          admins can create one.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title={groups ? `General groups (${groups.length})` : "General groups"}
        description="Shared group definitions every pharmacy can use. Each pharmacy files their own patients; nobody sees anyone else's."
        action={
          <Button size="sm" className="gap-1.5" onClick={() => setEditing("new")}>
            <FolderPlus className="h-3.5 w-3.5" />
            New group
          </Button>
        }
      >
        {!groups ? (
          <div className="p-4">
            <ListSkeleton rows={4} />
          </div>
        ) : groups.length === 0 ? (
          <EmptyState
            icon={FolderPlus}
            title="No general groups yet"
            message="Create shared segments like “Diabetes review” or “Over 65s” that every pharmacy can file patients under."
          />
        ) : (
          <ul className="divide-y divide-border/40">
            {groups.map((g) => (
              <li
                key={g.id}
                className="flex flex-wrap items-start justify-between gap-3 px-4 py-3"
              >
                <div className="min-w-0">
                  <p className="text-sm font-medium">{g.name}</p>
                  {g.description && (
                    <p className="mt-0.5 text-xs text-muted-foreground">{g.description}</p>
                  )}
                  <p className="mt-1 text-[11px] text-muted-foreground">
                    {g.totalPatients.toLocaleString()} patient
                    {g.totalPatients === 1 ? "" : "s"} across the platform · created{" "}
                    {formatDate(g.createdAtUtc)}
                  </p>
                </div>

                <div className="flex gap-1">
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 gap-1 text-xs"
                    onClick={() => setEditing(g)}
                  >
                    <Pencil className="h-3.5 w-3.5" />
                    Edit
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    className="h-7 w-7 p-0 text-muted-foreground hover:text-destructive"
                    disabled={busy === g.id}
                    onClick={() => remove(g)}
                    aria-label={`Delete ${g.name}`}
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <GroupDialog
        group={editing}
        onClose={() => setEditing(null)}
        onSaved={async () => {
          setEditing(null);
          await load();
        }}
        onError={setError}
      />
    </div>
  );
}

function GroupDialog({
  group,
  onClose,
  onSaved,
  onError,
}: {
  group: GlobalGroup | "new" | null;
  onClose: () => void;
  onSaved: () => void;
  onError: (message: string) => void;
}) {
  const editingExisting = group !== null && group !== "new";

  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [busy, setBusy] = useState(false);

  // Reset whenever a different group is opened, otherwise the previous one's
  // text is still in the box.
  useEffect(() => {
    setName(editingExisting ? group.name : "");
    setDescription(editingExisting ? group.description : "");
    setBusy(false);
  }, [group, editingExisting]);

  if (!group) return null;

  async function save() {
    setBusy(true);
    try {
      const body = { name: name.trim(), description: description.trim() };
      if (editingExisting) await api.admin.globalGroups.update(group.id, body);
      else await api.admin.globalGroups.create(body);
      onSaved();
    } catch (err) {
      onError(err instanceof Error ? err.message : "Could not save that group.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{editingExisting ? "Edit general group" : "New general group"}</DialogTitle>
          <DialogDescription>
            This appears in every pharmacy&apos;s group list.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-3">
          <div className="space-y-1.5">
            <Label htmlFor="group-name" className="text-xs">
              Name
            </Label>
            <Input
              id="group-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="e.g. Diabetes review"
              maxLength={120}
              autoFocus
            />
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="group-desc" className="text-xs">
              Description
            </Label>
            <Textarea
              id="group-desc"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={3}
              placeholder="What this group is for, so pharmacies file the right patients."
            />
          </div>

          {editingExisting && (
            <p className="flex items-start gap-1.5 rounded-md bg-muted px-3 py-2 text-[11px] text-muted-foreground">
              <AlertTriangle className="mt-0.5 h-3 w-3 shrink-0" />
              <span>
                Renaming changes it for every pharmacy. Their patients stay in
                the group.
              </span>
            </p>
          )}
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <Button variant="outline" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button size="sm" disabled={busy || name.trim().length === 0} onClick={save}>
            {busy ? "Saving…" : editingExisting ? "Save changes" : "Create group"}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

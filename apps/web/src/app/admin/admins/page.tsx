"use client";

import { useCallback, useEffect, useState } from "react";
import type { AdminSummary } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PasswordInput } from "@/components/ui/password-input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api";
import {
  ErrorNote,
  Panel,
  StatusDot,
  TableEmpty,
  TableLoading,
  formatDate,
} from "@/components/admin/AdminUi";

export default function AdminAdminsPage() {
  const [admins, setAdmins] = useState<AdminSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setAdmins(await api.admin.listAdmins());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load administrators.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  async function toggleActive(admin: AdminSummary) {
    setBusyId(admin.id);
    setError(null);
    try {
      await api.admin.updateAdmin(admin.id, { isActive: !admin.isActive });
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Update failed.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title="Administrators"
        description="Admins manage the pharmacies assigned to them."
        action={
          <Button size="sm" onClick={() => setCreating((v) => !v)}>
            {creating ? "Cancel" : "New administrator"}
          </Button>
        }
      >
        {creating && (
          <CreateAdminForm
            onCreated={async () => {
              setCreating(false);
              await load();
            }}
            onError={setError}
          />
        )}

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <tr>
                <th className="px-4 py-2 font-medium">Administrator</th>
                <th className="px-4 py-2 font-medium">Pharmacies</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Last login</th>
                <th className="px-4 py-2 font-medium">Added</th>
                <th className="px-4 py-2 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/60">
              {loading ? (
                <TableLoading colSpan={6} />
              ) : admins.length === 0 ? (
                <TableEmpty colSpan={6} message="No administrators yet." />
              ) : (
                admins.map((a) => (
                  <tr key={a.id} className="hover:bg-accent/40">
                    <td className="px-4 py-2.5">
                      <p className="font-medium">{a.displayName}</p>
                      <p className="text-xs text-muted-foreground">{a.email}</p>
                    </td>
                    <td className="px-4 py-2.5">{a.managedUserCount}</td>
                    <td className="px-4 py-2.5"><StatusDot active={a.isActive} /></td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {formatDate(a.lastLoginAtUtc)}
                    </td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {formatDate(a.createdAtUtc)}
                    </td>
                    <td className="px-4 py-2.5 text-right">
                      <Button
                        size="sm"
                        variant="ghost"
                        disabled={busyId === a.id}
                        onClick={() => toggleActive(a)}
                      >
                        {a.isActive ? "Deactivate" : "Reactivate"}
                      </Button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </Panel>

      <p className="text-xs text-muted-foreground">
        Deactivating an administrator ends their sessions immediately but leaves
        their pharmacies assigned. Reassign those pharmacies before removing the
        administrator permanently.
      </p>
    </div>
  );
}

function CreateAdminForm({
  onCreated,
  onError,
}: {
  onCreated: () => void;
  onError: (message: string) => void;
}) {
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [saving, setSaving] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      await api.admin.createAdmin({ email, displayName, password });
      setEmail("");
      setDisplayName("");
      setPassword("");
      onCreated();
    } catch (err) {
      onError(err instanceof Error ? err.message : "Could not create administrator.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <form onSubmit={submit} className="grid gap-3 border-b border-border/60 p-4 sm:grid-cols-3">
      <div className="space-y-1.5">
        <Label htmlFor="admin-name">Name</Label>
        <Input
          id="admin-name"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          required
          maxLength={80}
        />
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="admin-email">Email</Label>
        <Input
          id="admin-email"
          type="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="admin-password">Password</Label>
        <PasswordInput
          id="admin-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
          minLength={12}
          placeholder="At least 12 characters"
        />
      </div>
      <div className="sm:col-span-3">
        <Button type="submit" size="sm" disabled={saving}>
          {saving ? "Creating…" : "Create administrator"}
        </Button>
        <p className="mt-2 text-xs text-muted-foreground">
          Share this password over a secure channel and ask them to change it on
          first sign-in.
        </p>
      </div>
    </form>
  );
}

"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Download, FolderPlus, MoreHorizontal, Pencil, Plus, Search, Trash2, Upload, UserPlus, Users,
} from "lucide-react";
import type {
  Patient, PatientGroup, UpsertPatientGroupRequest, UpsertPatientRequest,
} from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useAuthStore } from "@/store/auth-store";
import { PatientForm } from "./PatientForm";
import { GroupForm } from "./GroupForm";
import { ImportDialog } from "./ImportDialog";

const PAGE_SIZE = 25;

export default function PatientsPage() {
  const toast = useToast();
  const user = useAuthStore((s) => s.user);

  const [tab, setTab] = useState<"patients" | "groups">("patients");

  // Patients
  const [patients, setPatients] = useState<Patient[]>([]);
  const [total, setTotal] = useState(0);
  const [search, setSearch] = useState("");
  const [groupFilter, setGroupFilter] = useState<string | "">("");
  const [skip, setSkip] = useState(0);
  const [loading, setLoading] = useState(true);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [editing, setEditing] = useState<Patient | null>(null);
  const [patientOpen, setPatientOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);

  // Groups
  const [groups, setGroups] = useState<PatientGroup[]>([]);
  const [editingGroup, setEditingGroup] = useState<PatientGroup | null>(null);
  const [groupOpen, setGroupOpen] = useState(false);

  const reloadGroups = useCallback(async () => {
    try { setGroups(await api.listPatientGroups()); }
    catch (e) { toast.error("Couldn't load groups", e instanceof Error ? e.message : undefined); }
  }, [toast]);

  const reloadPatients = useCallback(async () => {
    setLoading(true);
    try {
      const data = await api.listPatients({
        search: search || undefined,
        groupId: groupFilter || undefined,
        skip,
        take: PAGE_SIZE,
      });
      setPatients(data.items);
      setTotal(data.total);
    } catch (e) {
      toast.error("Couldn't load patients", e instanceof Error ? e.message : undefined);
    } finally {
      setLoading(false);
    }
  }, [search, groupFilter, skip, toast]);

  useEffect(() => { void reloadGroups(); }, [reloadGroups]);
  useEffect(() => { void reloadPatients(); }, [reloadPatients]);

  const groupNameById = useMemo(
    () => new Map(groups.map(g => [g.id, g.name])),
    [groups],
  );

  const allOnPageSelected = patients.length > 0 && patients.every(p => selectedIds.has(p.id));

  function toggleSelectAll() {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (allOnPageSelected) patients.forEach(p => next.delete(p.id));
      else patients.forEach(p => next.add(p.id));
      return next;
    });
  }

  function toggleSelect(id: string) {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  async function savePatient(body: UpsertPatientRequest) {
    if (editing) {
      await api.updatePatient(editing.id, body);
      toast.success("Patient updated");
    } else {
      await api.createPatient(body);
      toast.success("Patient added");
    }
    setEditing(null);
    await Promise.all([reloadPatients(), reloadGroups()]);
  }

  async function saveGroup(body: UpsertPatientGroupRequest) {
    if (editingGroup) {
      await api.updatePatientGroup(editingGroup.id, body);
      toast.success("Group updated");
    } else {
      await api.createPatientGroup(body);
      toast.success("Group created");
    }
    setEditingGroup(null);
    await reloadGroups();
  }

  async function deletePatient(p: Patient) {
    if (!confirm(`Delete ${p.fullName}?`)) return;
    try {
      await api.deletePatient(p.id);
      toast.success("Patient deleted");
      await Promise.all([reloadPatients(), reloadGroups()]);
    } catch (e) {
      toast.error("Delete failed", e instanceof Error ? e.message : undefined);
    }
  }

  async function bulkDelete() {
    const ids = [...selectedIds];
    if (ids.length === 0) return;
    if (!confirm(`Delete ${ids.length} patients?`)) return;
    try {
      await api.bulkDeletePatients({ ids });
      toast.success(`Deleted ${ids.length} patients`);
      setSelectedIds(new Set());
      await Promise.all([reloadPatients(), reloadGroups()]);
    } catch (e) {
      toast.error("Bulk delete failed", e instanceof Error ? e.message : undefined);
    }
  }

  async function bulkAddToGroup(groupId: string) {
    const ids = [...selectedIds];
    if (ids.length === 0) return;
    try {
      const r = await api.bulkAddPatientsToGroup({ patientIds: ids, groupId });
      toast.success(`Added ${r.added} patient${r.added === 1 ? "" : "s"} to group`);
      setSelectedIds(new Set());
      await Promise.all([reloadPatients(), reloadGroups()]);
    } catch (e) {
      toast.error("Add to group failed", e instanceof Error ? e.message : undefined);
    }
  }

  async function deleteGroup(g: PatientGroup) {
    if (!confirm(`Delete group "${g.name}"? Patients are not deleted.`)) return;
    try {
      await api.deletePatientGroup(g.id);
      toast.success("Group deleted");
      await Promise.all([reloadGroups(), reloadPatients()]);
    } catch (e) {
      toast.error("Delete failed", e instanceof Error ? e.message : undefined);
    }
  }

  function exportCsv() {
    const header = "fullName,email,phone,notes,groups";
    const rows = patients.map(p => [
      escape(p.fullName), escape(p.email ?? ""), escape(p.phone ?? ""),
      escape(p.notes),
      escape(p.groupIds.map(id => groupNameById.get(id) ?? "").filter(Boolean).join(" | ")),
    ].join(","));
    const csv = [header, ...rows].join("\n");
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `patients-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }

  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const pageIndex = Math.floor(skip / PAGE_SIZE);

  return (
    <main className="container max-w-6xl space-y-6 py-8">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Patients</h1>
          <p className="text-sm text-muted-foreground">
            Manage your patient list and target campaigns with groups.
          </p>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={() => setImportOpen(true)}>
            <Upload className="mr-2 h-4 w-4" /> Import
          </Button>
          <Button variant="outline" size="sm" onClick={exportCsv} disabled={patients.length === 0}>
            <Download className="mr-2 h-4 w-4" /> Export CSV
          </Button>
          {tab === "patients" ? (
            <Button size="sm" onClick={() => { setEditing(null); setPatientOpen(true); }}>
              <UserPlus className="mr-2 h-4 w-4" /> Add patient
            </Button>
          ) : (
            <Button size="sm" onClick={() => { setEditingGroup(null); setGroupOpen(true); }}>
              <FolderPlus className="mr-2 h-4 w-4" /> New group
            </Button>
          )}
        </div>
      </div>

      <Tabs value={tab} onValueChange={(v) => setTab(v as "patients" | "groups")}>
        <TabsList>
          <TabsTrigger value="patients">
            <Users className="mr-2 h-4 w-4" />
            Patients ({total})
          </TabsTrigger>
          <TabsTrigger value="groups">
            <FolderPlus className="mr-2 h-4 w-4" />
            Groups ({groups.length})
          </TabsTrigger>
        </TabsList>

        <TabsContent value="patients" className="mt-4 space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <div className="relative w-full max-w-xs">
              <Search className="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
              <Input
                placeholder="Search name, email, phone…"
                value={search}
                onChange={e => { setSkip(0); setSearch(e.target.value); }}
                className="pl-8"
              />
            </div>
            <select
              value={groupFilter}
              onChange={e => { setSkip(0); setGroupFilter(e.target.value); }}
              className="h-9 rounded-md border bg-background px-2 text-sm"
            >
              <option value="">All groups</option>
              {groups.map(g => (
                <option key={g.id} value={g.id}>{g.name} ({g.patientCount})</option>
              ))}
            </select>

            {selectedIds.size > 0 && (
              <div className="ml-auto flex items-center gap-2 rounded-md border bg-muted/40 px-3 py-1 text-sm">
                <strong>{selectedIds.size}</strong> selected
                <select
                  className="h-7 rounded-md border bg-background px-2 text-xs"
                  defaultValue=""
                  onChange={e => { if (e.target.value) { void bulkAddToGroup(e.target.value); e.target.value = ""; } }}
                >
                  <option value="">Add to group…</option>
                  {groups.map(g => <option key={g.id} value={g.id}>{g.name}</option>)}
                </select>
                <Button variant="destructive" size="sm" onClick={bulkDelete}>
                  <Trash2 className="mr-1 h-3.5 w-3.5" /> Delete
                </Button>
              </div>
            )}
          </div>

          <Card>
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
                  <tr>
                    <th className="w-10 p-3">
                      <input
                        type="checkbox"
                        checked={allOnPageSelected}
                        onChange={toggleSelectAll}
                        aria-label="Select all on page"
                      />
                    </th>
                    <th className="p-3">Name</th>
                    <th className="p-3">Email</th>
                    <th className="p-3">Phone</th>
                    <th className="p-3">Groups</th>
                    <th className="w-24 p-3 text-right">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {loading ? (
                    <tr><td colSpan={6} className="p-6 text-center text-muted-foreground">Loading…</td></tr>
                  ) : patients.length === 0 ? (
                    <tr>
                      <td colSpan={6} className="p-10 text-center text-muted-foreground">
                        <div className="flex flex-col items-center gap-2">
                          <Users className="h-8 w-8 opacity-40" />
                          <p>No patients yet.</p>
                          {user && (
                            <p className="text-xs">
                              Signed in as <span className="font-mono">{user.email}</span>.
                              If you created patients on a different account, sign out and back in
                              with the correct one.
                            </p>
                          )}
                          <Button size="sm" onClick={() => { setEditing(null); setPatientOpen(true); }}>
                            <Plus className="mr-1 h-3.5 w-3.5" /> Add your first
                          </Button>
                        </div>
                      </td>
                    </tr>
                  ) : (
                    patients.map(p => (
                      <tr key={p.id} className={cn(
                        "border-t",
                        selectedIds.has(p.id) ? "bg-primary/5" : "hover:bg-muted/30",
                      )}>
                        <td className="p-3">
                          <input
                            type="checkbox"
                            checked={selectedIds.has(p.id)}
                            onChange={() => toggleSelect(p.id)}
                            aria-label={`Select ${p.fullName}`}
                          />
                        </td>
                        <td className="p-3 font-medium">{p.fullName}</td>
                        <td className="p-3 text-muted-foreground">{p.email ?? "—"}</td>
                        <td className="p-3 text-muted-foreground">{p.phone ?? "—"}</td>
                        <td className="p-3">
                          <div className="flex flex-wrap gap-1">
                            {p.groupIds.map(id => (
                              <Badge key={id} variant="secondary" className="text-xs">
                                {groupNameById.get(id) ?? id}
                              </Badge>
                            ))}
                          </div>
                        </td>
                        <td className="p-3 text-right">
                          <div className="inline-flex gap-1">
                            <Button variant="ghost" size="icon" aria-label="Edit"
                              onClick={() => { setEditing(p); setPatientOpen(true); }}>
                              <Pencil className="h-4 w-4" />
                            </Button>
                            <Button variant="ghost" size="icon" aria-label="Delete"
                              onClick={() => void deletePatient(p)}>
                              <Trash2 className="h-4 w-4 text-destructive" />
                            </Button>
                          </div>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
            <div className="flex items-center justify-between border-t p-3 text-xs text-muted-foreground">
              <span>Page {pageIndex + 1} of {pageCount} · {total} total</span>
              <div className="flex gap-2">
                <Button size="sm" variant="outline" disabled={pageIndex === 0}
                  onClick={() => setSkip(Math.max(0, skip - PAGE_SIZE))}>Previous</Button>
                <Button size="sm" variant="outline" disabled={pageIndex + 1 >= pageCount}
                  onClick={() => setSkip(skip + PAGE_SIZE)}>Next</Button>
              </div>
            </div>
          </Card>
        </TabsContent>

        <TabsContent value="groups" className="mt-4">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {groups.length === 0 && (
              <Card className="col-span-full">
                <CardContent className="py-10 text-center text-muted-foreground">
                  No groups yet. Create one to target campaigns.
                </CardContent>
              </Card>
            )}
            {groups.map(g => (
              <Card key={g.id} className="transition hover:border-primary/40">
                <CardHeader>
                  <div className="flex items-start justify-between gap-2">
                    <div>
                      <CardTitle className="text-base">{g.name}</CardTitle>
                      <CardDescription className="line-clamp-2">{g.description || "—"}</CardDescription>
                    </div>
                    <Badge variant="secondary">{g.patientCount}</Badge>
                  </div>
                </CardHeader>
                <CardContent className="flex justify-end gap-1 pt-0">
                  <Button variant="ghost" size="sm"
                    onClick={() => { setTab("patients"); setGroupFilter(g.id); }}>
                    View members
                  </Button>
                  <Button variant="ghost" size="icon" aria-label="Edit"
                    onClick={() => { setEditingGroup(g); setGroupOpen(true); }}>
                    <Pencil className="h-4 w-4" />
                  </Button>
                  <Button variant="ghost" size="icon" aria-label="Delete"
                    onClick={() => void deleteGroup(g)}>
                    <Trash2 className="h-4 w-4 text-destructive" />
                  </Button>
                </CardContent>
              </Card>
            ))}
          </div>
        </TabsContent>
      </Tabs>

      <PatientForm
        open={patientOpen}
        onOpenChange={setPatientOpen}
        patient={editing}
        groups={groups}
        onSubmit={savePatient}
      />
      <GroupForm
        open={groupOpen}
        onOpenChange={setGroupOpen}
        group={editingGroup}
        onSubmit={saveGroup}
      />
      <ImportDialog
        open={importOpen}
        onOpenChange={setImportOpen}
        onSubmit={async (rows) => {
          const r = await api.bulkImportPatients({ patients: rows });
          toast.success(`Imported ${r.imported}`, r.skipped > 0 ? `${r.skipped} skipped` : undefined);
          await Promise.all([reloadPatients(), reloadGroups()]);
          return r;
        }}
      />
    </main>
  );
}

function escape(v: string) {
  if (v.includes(",") || v.includes('"') || v.includes("\n")) {
    return `"${v.replace(/"/g, '""')}"`;
  }
  return v;
}

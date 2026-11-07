"use client";

import { useCallback, useEffect, useState } from "react";
import { Eye, Users } from "lucide-react";
import type {
  RevealedPatientContact,
  SupportGroup,
  SupportPatient,
  SupportPatientList,
} from "@acme/shared-types";
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
import {
  EmptyState,
  ErrorNote,
  Pagination,
  Panel,
  TableEmpty,
  TableLoading,
  formatDate,
} from "../AdminUi";
import { Masked } from "./SupportUi";

const TAKE = 25;

/**
 * The pharmacy's patient list.
 *
 * Contact details arrive from the API already masked — the real values are not
 * in this component's props, in its state, or in the network response behind
 * it. Revealing one is a deliberate, explained, logged act, which is what the
 * dialog below is for.
 */
export function PatientsTab({
  pharmacyId,
  pharmacyName,
}: {
  pharmacyId: string;
  pharmacyName: string;
}) {
  const [page, setPage] = useState<SupportPatientList | null>(null);
  const [groups, setGroups] = useState<SupportGroup[]>([]);
  const [search, setSearch] = useState("");
  const [groupId, setGroupId] = useState("");
  const [skip, setSkip] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [revealing, setRevealing] = useState<SupportPatient | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await api.admin.support.patients(pharmacyId, {
        search: search.trim() || undefined,
        groupId: groupId || undefined,
        skip,
        take: TAKE,
      });
      setPage(result);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load patients.");
    } finally {
      setLoading(false);
    }
  }, [pharmacyId, search, groupId, skip]);

  useEffect(() => {
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load]);

  useEffect(() => {
    api.admin.support
      .groups(pharmacyId)
      .then(setGroups)
      .catch(() => setGroups([]));
  }, [pharmacyId]);

  // Any change of filter returns to the first page. Without this, narrowing a
  // search while on page 4 shows an empty table and reads as "no results".
  useEffect(() => {
    setSkip(0);
  }, [search, groupId]);

  const rows = page?.items ?? [];

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title={loading && !page ? "Patients" : `Patients (${page?.total ?? 0})`}
        description={
          "Contact details are hidden. Revealing one records who looked, when, " +
          "and the reason given."
        }
        action={
          <div className="flex flex-wrap items-center gap-2">
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search name, email or phone"
              className="h-8 w-56 text-xs"
            />
            <select
              value={groupId}
              onChange={(e) => setGroupId(e.target.value)}
              className="h-8 rounded-md border border-input bg-background px-2 text-xs"
              aria-label="Filter by group"
            >
              <option value="">All groups</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name} ({g.patientCount})
                </option>
              ))}
            </select>
          </div>
        }
      >
        {!loading && rows.length === 0 && !search && !groupId ? (
          <EmptyState
            icon={Users}
            title="No patients yet"
            message={`${pharmacyName} has not added anyone to their patient list.`}
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-4 py-2 font-medium">Name</th>
                  <th className="px-4 py-2 font-medium">Email</th>
                  <th className="px-4 py-2 font-medium">Phone</th>
                  <th className="px-4 py-2 font-medium">Groups</th>
                  <th className="px-4 py-2 font-medium">Added</th>
                  <th className="px-4 py-2 text-right font-medium">
                    <span className="sr-only">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {loading ? (
                  <TableLoading colSpan={6} />
                ) : rows.length === 0 ? (
                  <TableEmpty
                    colSpan={6}
                    message="No patients match that search."
                  />
                ) : (
                  rows.map((p) => (
                    <tr key={p.id} className="border-b border-border/40">
                      <td className="px-4 py-2 font-medium">{p.fullName}</td>
                      <td className="px-4 py-2">
                        {p.hasEmail ? (
                          <Masked value={p.maskedEmail} />
                        ) : (
                          <span className="text-xs text-muted-foreground">
                            none on file
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-2">
                        {p.hasPhone ? (
                          <Masked value={p.maskedPhone} />
                        ) : (
                          <span className="text-xs text-muted-foreground">
                            none on file
                          </span>
                        )}
                      </td>
                      <td className="px-4 py-2 text-xs text-muted-foreground">
                        {p.groupNames.length > 0 ? p.groupNames.join(", ") : "—"}
                      </td>
                      <td className="px-4 py-2 text-xs text-muted-foreground">
                        {formatDate(p.createdAtUtc)}
                      </td>
                      <td className="px-4 py-2 text-right">
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-7 gap-1 text-xs"
                          disabled={!p.hasEmail && !p.hasPhone}
                          onClick={() => setRevealing(p)}
                        >
                          <Eye className="h-3.5 w-3.5" />
                          Reveal
                        </Button>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        )}

        {page && page.total > TAKE && (
          <Pagination
            skip={skip}
            take={TAKE}
            total={page.total}
            onChange={setSkip}
          />
        )}
      </Panel>

      <RevealDialog
        pharmacyId={pharmacyId}
        patient={revealing}
        onClose={() => setRevealing(null)}
      />
    </div>
  );
}

/**
 * Asks why, then shows the details.
 *
 * The reason box is not a formality to be clicked past. It is the thing that
 * makes the audit entry worth having: "Admin viewed patient contact" answers
 * almost nothing on its own, and "…to check the bounce on ticket 4182" answers
 * almost everything. The submit stays disabled until something has actually
 * been typed.
 */
function RevealDialog({
  pharmacyId,
  patient,
  onClose,
}: {
  pharmacyId: string;
  patient: SupportPatient | null;
  onClose: () => void;
}) {
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [revealed, setRevealed] = useState<RevealedPatientContact | null>(null);

  // A new patient must start from a blank form. Carrying the previous reason
  // over would let one typed justification cover a second lookup it was never
  // written for.
  useEffect(() => {
    setReason("");
    setError(null);
    setRevealed(null);
    setBusy(false);
  }, [patient?.id]);

  if (!patient) return null;

  async function submit() {
    if (!patient) return;
    setBusy(true);
    setError(null);
    try {
      setRevealed(
        await api.admin.support.revealPatient(pharmacyId, patient.id, reason.trim()),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not reveal details.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{patient.fullName}</DialogTitle>
          <DialogDescription>
            {revealed
              ? "This access has been recorded in the audit log."
              : "Contact details for a patient of this pharmacy."}
          </DialogDescription>
        </DialogHeader>

        {error && <ErrorNote message={error} />}

        {revealed ? (
          <dl className="space-y-3 text-sm">
            <div>
              <dt className="text-xs text-muted-foreground">Email</dt>
              <dd className="mt-0.5 font-medium">{revealed.email ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-xs text-muted-foreground">Phone</dt>
              <dd className="mt-0.5 font-medium">{revealed.phone ?? "—"}</dd>
            </div>
            <div>
              <dt className="text-xs text-muted-foreground">Logged reason</dt>
              <dd className="mt-0.5 text-xs text-muted-foreground">
                {revealed.reason}
              </dd>
            </div>
          </dl>
        ) : (
          <div className="space-y-3">
            <p className="rounded-md bg-muted px-3 py-2 text-xs text-muted-foreground">
              This is a patient of a pharmacy customer, not of ours. Viewing
              their details is logged against your name and is visible to the
              pharmacy on request.
            </p>
            <div className="space-y-1.5">
              <Label htmlFor="reveal-reason" className="text-xs">
                Why do you need these details?
              </Label>
              <Input
                id="reveal-reason"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                placeholder="e.g. checking a bounced address for ticket 4182"
                maxLength={200}
                autoFocus
              />
            </div>
          </div>
        )}

        <div className="flex justify-end gap-2 pt-2">
          <Button variant="outline" size="sm" onClick={onClose}>
            {revealed ? "Done" : "Cancel"}
          </Button>
          {!revealed && (
            <Button
              size="sm"
              disabled={busy || reason.trim().length < 5}
              onClick={submit}
            >
              {busy ? "Recording…" : "Reveal and log"}
            </Button>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}

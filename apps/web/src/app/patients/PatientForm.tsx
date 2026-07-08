"use client";

import { useEffect, useState } from "react";
import type { Patient, PatientGroup, UpsertPatientRequest } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

interface Props {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  patient?: Patient | null;
  groups: PatientGroup[];
  onSubmit: (body: UpsertPatientRequest) => Promise<void>;
}

export function PatientForm({ open, onOpenChange, patient, groups, onSubmit }: Props) {
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [phone, setPhone] = useState("");
  const [notes, setNotes] = useState("");
  const [groupIds, setGroupIds] = useState<string[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setFullName(patient?.fullName ?? "");
    setEmail(patient?.email ?? "");
    setPhone(patient?.phone ?? "");
    setNotes(patient?.notes ?? "");
    setGroupIds(patient?.groupIds ?? []);
    setError(null);
  }, [open, patient]);

  function toggleGroup(id: string) {
    setGroupIds(prev => prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]);
  }

  async function handleSave() {
    setError(null);
    if (!fullName.trim()) { setError("Full name is required."); return; }
    if (!email.trim() && !phone.trim()) {
      setError("Provide at least an email or phone number.");
      return;
    }
    setSubmitting(true);
    try {
      await onSubmit({
        fullName: fullName.trim(),
        email: email.trim() || null,
        phone: phone.trim() || null,
        notes,
        groupIds,
      });
      onOpenChange(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to save.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{patient ? "Edit patient" : "Add patient"}</DialogTitle>
          <DialogDescription>
            Patients can be sent posters by email or SMS, individually or as a group.
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-4">
          <div className="grid gap-1.5">
            <Label htmlFor="fullName">Full name *</Label>
            <Input id="fullName" value={fullName} onChange={e => setFullName(e.target.value)} />
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div className="grid gap-1.5">
              <Label htmlFor="email">Email</Label>
              <Input id="email" type="email" value={email} onChange={e => setEmail(e.target.value)} />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="phone">Phone</Label>
              <Input id="phone" value={phone} onChange={e => setPhone(e.target.value)} placeholder="+44…" />
            </div>
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="notes">Notes</Label>
            <Textarea id="notes" value={notes} onChange={e => setNotes(e.target.value)} rows={3} />
          </div>

          {groups.length > 0 && (
            <div className="grid gap-1.5">
              <Label>Groups</Label>
              <div className="flex flex-wrap gap-2">
                {groups.map(g => {
                  const checked = groupIds.includes(g.id);
                  return (
                    <button
                      type="button"
                      key={g.id}
                      onClick={() => toggleGroup(g.id)}
                      className={
                        "rounded-full border px-3 py-1 text-xs transition " +
                        (checked
                          ? "border-primary bg-primary/10 text-primary"
                          : "border-border text-muted-foreground hover:bg-accent")
                      }
                    >
                      {g.name}
                    </button>
                  );
                })}
              </div>
            </div>
          )}

          {error && <p className="text-sm text-destructive">{error}</p>}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button onClick={handleSave} disabled={submitting}>
            {submitting ? "Saving…" : patient ? "Save changes" : "Add patient"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

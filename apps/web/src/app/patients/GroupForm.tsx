"use client";

import { useEffect, useState } from "react";
import type { PatientGroup, UpsertPatientGroupRequest } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

interface Props {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  group?: PatientGroup | null;
  onSubmit: (body: UpsertPatientGroupRequest) => Promise<void>;
}

export function GroupForm({ open, onOpenChange, group, onSubmit }: Props) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setName(group?.name ?? "");
    setDescription(group?.description ?? "");
    setError(null);
  }, [open, group]);

  async function handleSave() {
    setError(null);
    if (!name.trim()) { setError("Group name is required."); return; }
    setSubmitting(true);
    try {
      await onSubmit({ name: name.trim(), description });
      onOpenChange(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to save.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>{group ? "Edit group" : "Create group"}</DialogTitle>
        </DialogHeader>

        <div className="grid gap-4">
          <div className="grid gap-1.5">
            <Label htmlFor="g-name">Name *</Label>
            <Input id="g-name" value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Flu jab list" />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="g-desc">Description</Label>
            <Textarea id="g-desc" value={description} onChange={e => setDescription(e.target.value)} rows={3} />
          </div>
          {error && <p className="text-sm text-destructive">{error}</p>}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button onClick={handleSave} disabled={submitting}>
            {submitting ? "Saving…" : group ? "Save changes" : "Create group"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

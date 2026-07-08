"use client";

import { useState } from "react";
import { UploadCloud } from "lucide-react";
import type { BulkImportResult, UpsertPatientRequest } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

interface Props {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  onSubmit: (rows: UpsertPatientRequest[]) => Promise<BulkImportResult>;
}

/** Parse a CSV with headers: fullName,email,phone,notes */
function parseCsv(text: string): UpsertPatientRequest[] {
  const lines = text.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  if (lines.length === 0) return [];
  const header = lines[0].split(",").map(h => h.trim().toLowerCase());
  const idx = (key: string) => header.indexOf(key);
  const iName = idx("fullname");
  const iEmail = idx("email");
  const iPhone = idx("phone");
  const iNotes = idx("notes");

  const rows: UpsertPatientRequest[] = [];
  for (let i = 1; i < lines.length; i++) {
    const cols = splitCsvLine(lines[i]);
    const name = iName >= 0 ? cols[iName] : cols[0];
    if (!name) continue;
    rows.push({
      fullName: name.trim(),
      email: iEmail >= 0 ? (cols[iEmail]?.trim() || null) : null,
      phone: iPhone >= 0 ? (cols[iPhone]?.trim() || null) : null,
      notes: iNotes >= 0 ? (cols[iNotes] ?? "") : "",
    });
  }
  return rows;
}

function splitCsvLine(line: string): string[] {
  const out: string[] = [];
  let cur = "";
  let inQuotes = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (c === '"') {
      if (inQuotes && line[i + 1] === '"') { cur += '"'; i++; }
      else inQuotes = !inQuotes;
    } else if (c === "," && !inQuotes) {
      out.push(cur); cur = "";
    } else cur += c;
  }
  out.push(cur);
  return out;
}

export function ImportDialog({ open, onOpenChange, onSubmit }: Props) {
  const [text, setText] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState<BulkImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function onFile(file: File) {
    const t = await file.text();
    setText(t);
  }

  async function handleImport() {
    setError(null);
    setResult(null);
    let rows: UpsertPatientRequest[];
    try {
      rows = text.trim().startsWith("[")
        ? (JSON.parse(text) as UpsertPatientRequest[])
        : parseCsv(text);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to parse data.");
      return;
    }
    if (rows.length === 0) { setError("No rows found."); return; }
    setSubmitting(true);
    try {
      const r = await onSubmit(rows);
      setResult(r);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Import failed.");
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={(v) => { onOpenChange(v); if (!v) { setText(""); setResult(null); setError(null); } }}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>Bulk import patients</DialogTitle>
          <DialogDescription>
            Paste CSV with header row{" "}
            <code className="rounded bg-muted px-1">fullName,email,phone,notes</code>,
            or a JSON array of patient objects.
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-3">
          <Label
            htmlFor="csv-file"
            className="flex cursor-pointer items-center gap-2 rounded-md border border-dashed p-3 text-sm text-muted-foreground hover:bg-accent"
          >
            <UploadCloud className="h-4 w-4" />
            Choose CSV/JSON file…
            <input
              id="csv-file"
              type="file"
              accept=".csv,.json,.txt,text/csv,application/json"
              className="sr-only"
              onChange={e => { const f = e.target.files?.[0]; if (f) void onFile(f); }}
            />
          </Label>
          <Textarea
            rows={8}
            placeholder="fullName,email,phone&#10;Jane Doe,jane@example.com,+447700900000"
            value={text}
            onChange={e => setText(e.target.value)}
            className="font-mono text-xs"
          />
          {error && <p className="text-sm text-destructive">{error}</p>}
          {result && (
            <div className="rounded-md border bg-muted/40 p-3 text-sm">
              Imported <strong>{result.imported}</strong>, skipped{" "}
              <strong>{result.skipped}</strong>.
              {result.errors.length > 0 && (
                <ul className="mt-1 list-disc pl-5 text-xs text-muted-foreground">
                  {result.errors.map((e, i) => <li key={i}>{e}</li>)}
                </ul>
              )}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Close</Button>
          <Button onClick={handleImport} disabled={submitting || !text.trim()}>
            {submitting ? "Importing…" : "Import"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

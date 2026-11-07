"use client";

import { useState } from "react";
import { AlertTriangle, CheckCircle2, Download, FileUp, Info } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import type { PatientImportReport } from "@acme/shared-types";
import { api } from "@/lib/api";

type Props = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Called after a successful import so the list behind the dialog refreshes. */
  onImported: () => void;
};

/**
 * Importing a pharmacy's existing contact list.
 *
 * The file is uploaded and parsed **on the server**, not here. It used to be
 * parsed in the browser and posted as JSON, which meant two parsers: the
 * browser's, and the one every other route uses. They disagreed in the ways
 * hand-rolled CSV always does — a byte-order mark from Excel, a quoted field
 * containing a comma — and only the server's could check a row against the
 * contacts already saved, which is what deduplication needs.
 *
 * What is shown afterwards matters as much as the import. A real list is
 * messy; "412 of 500 imported" tells somebody nothing they can act on, so
 * every row that did not become a contact is listed with its line number and
 * a reason.
 */
export function ImportDialog({ open, onOpenChange, onImported }: Props) {
  const [file, setFile] = useState<File | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [report, setReport] = useState<PatientImportReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  function reset() {
    setFile(null);
    setReport(null);
    setError(null);
  }

  async function handleImport() {
    if (!file) return;
    setError(null);
    setReport(null);
    setSubmitting(true);
    try {
      const result = await api.importPatients(file);
      setReport(result);
      if (result.created > 0) onImported();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Import failed.");
    } finally {
      setSubmitting(false);
    }
  }

  const problems = report?.rows.filter((r) => r.outcome !== "created") ?? [];

  return (
    <Dialog
      open={open}
      onOpenChange={(v) => {
        onOpenChange(v);
        if (!v) reset();
      }}
    >
      <DialogContent className="max-h-[85vh] max-w-2xl overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Import contacts</DialogTitle>
          <DialogDescription>
            Upload a CSV with a name column and an email or phone column. An optional Marketing Consent column (Yes or No) records who agreed to receive emails.
          </DialogDescription>
        </DialogHeader>

        {!report && (
          <div className="space-y-4">
            <div className="flex items-start gap-2 rounded-md border border-border/60 bg-muted/40 p-3 text-sm text-muted-foreground">
              <Info className="mt-0.5 h-4 w-4 shrink-0" />
              <p>
                These are marketing contacts. Only upload people who are happy to hear
                from you, and do not include clinical information &mdash; there is no
                field for it and none should be put in the notes.
              </p>
            </div>

            <label
              htmlFor="contacts-file"
              className="flex cursor-pointer flex-col items-center gap-2 rounded-lg border border-dashed border-border px-4 py-8 text-center transition-colors hover:bg-accent/40"
            >
              <FileUp className="h-6 w-6 text-muted-foreground" />
              <span className="text-sm font-medium">
                {file ? file.name : "Choose a CSV file"}
              </span>
              <span className="text-xs text-muted-foreground">
                {file
                  ? `${(file.size / 1024).toFixed(0)} KB`
                  : "Up to 5,000 rows"}
              </span>
              <input
                id="contacts-file"
                type="file"
                accept=".csv,text/csv"
                className="sr-only"
                onChange={(e) => {
                  setFile(e.target.files?.[0] ?? null);
                  setError(null);
                }}
              />
            </label>

            <a
              href="/api/patients/csv-template"
              className="inline-flex items-center gap-1.5 text-sm font-medium text-primary hover:underline"
            >
              <Download className="h-3.5 w-3.5" />
              Download a template
            </a>

            {error && (
              <p className="text-sm text-destructive" role="alert">
                {error}
              </p>
            )}
          </div>
        )}

        {report && (
          <div className="space-y-4">
            <div className="grid grid-cols-3 gap-3 text-center">
              <Stat label="Added" value={report.created} tone="good" />
              <Stat label="Skipped" value={report.skipped} tone="muted" />
              <Stat label="Failed" value={report.failed} tone={report.failed ? "bad" : "muted"} />
            </div>

            {problems.length === 0 ? (
              <p className="flex items-center gap-2 text-sm text-muted-foreground">
                <CheckCircle2 className="h-4 w-4 text-primary" />
                Every row in that file was added.
              </p>
            ) : (
              <div className="space-y-2">
                <p className="flex items-center gap-2 text-sm font-medium">
                  <AlertTriangle className="h-4 w-4 text-amber-500" />
                  {problems.length} row{problems.length === 1 ? "" : "s"} not added
                </p>
                <div className="max-h-64 overflow-y-auto rounded-md border border-border/60">
                  <table className="w-full text-left text-sm">
                    <thead className="sticky top-0 bg-muted/60 text-xs uppercase text-muted-foreground">
                      <tr>
                        <th className="px-3 py-2 font-medium">Line</th>
                        <th className="px-3 py-2 font-medium">Contact</th>
                        <th className="px-3 py-2 font-medium">Reason</th>
                      </tr>
                    </thead>
                    <tbody>
                      {problems.map((r) => (
                        <tr key={`${r.lineNumber}-${r.contact ?? ""}`} className="border-t border-border/60">
                          <td className="px-3 py-2 font-mono text-xs text-muted-foreground">
                            {r.lineNumber}
                          </td>
                          <td className="px-3 py-2">{r.contact ?? "—"}</td>
                          <td className="px-3 py-2 text-muted-foreground">{r.detail}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            )}
          </div>
        )}

        <DialogFooter>
          {report ? (
            <>
              <Button variant="ghost" onClick={reset}>
                Import another
              </Button>
              <Button onClick={() => onOpenChange(false)}>Done</Button>
            </>
          ) : (
            <>
              <Button variant="ghost" onClick={() => onOpenChange(false)}>
                Cancel
              </Button>
              <Button onClick={handleImport} disabled={!file || submitting}>
                {submitting ? "Importing…" : "Import"}
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function Stat({
  label,
  value,
  tone,
}: {
  label: string;
  value: number;
  tone: "good" | "bad" | "muted";
}) {
  const colour =
    tone === "good" ? "text-primary" : tone === "bad" ? "text-destructive" : "text-foreground";

  return (
    <div className="rounded-md border border-border/60 px-3 py-3">
      <p className={`text-2xl font-semibold ${colour}`}>{value}</p>
      <p className="text-xs text-muted-foreground">{label}</p>
    </div>
  );
}

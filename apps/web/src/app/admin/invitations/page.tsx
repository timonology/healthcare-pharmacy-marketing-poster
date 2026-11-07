"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import {
  AlertTriangle,
  Download,
  MailPlus,
  RefreshCw,
  ShieldAlert,
  Upload,
  UserPlus,
} from "lucide-react";
import type {
  BulkInviteResult,
  CsvPreview,
  InvitationStatus,
  InvitedPharmacist,
  InvitePharmacistRequest,
} from "@acme/shared-types";
import { canResendInvitation, invitationStatusTone } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import {
  EmptyState,
  ErrorNote,
  Panel,
  TableEmpty,
  TableLoading,
  formatDate,
} from "@/components/admin/AdminUi";
import { cn } from "@/lib/utils";

const TONE = {
  neutral: "bg-muted text-muted-foreground",
  good: "bg-primary/10 text-primary",
  warn: "bg-amber-500/15 text-amber-700 dark:text-amber-400",
} as const;

/**
 * Invite pharmacists, one at a time or from a spreadsheet.
 *
 * SuperAdmin only. The role check below hides the page; the API refuses an
 * ordinary Admin regardless, and the service checks a third time. This exists
 * so someone following a link sees an explanation rather than failed requests.
 */
export default function AdminInvitationsPage() {
  const role = useAuthStore((s) => s.user?.role);
  const isSuperAdmin = role === "SuperAdmin";

  const [items, setItems] = useState<InvitedPharmacist[] | null>(null);
  const [status, setStatus] = useState<InvitationStatus | "">("");
  const [search, setSearch] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const page = await api.admin.invitations.list({
        search: search.trim() || undefined,
        status: status || undefined,
        take: 200,
      });
      setItems(page.items);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load invitations.");
    }
  }, [search, status]);

  useEffect(() => {
    if (!isSuperAdmin) return;
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [isSuperAdmin, load]);

  async function resend(pharmacist: InvitedPharmacist) {
    setBusy(pharmacist.userId);
    setError(null);
    setNotice(null);
    try {
      const result = await api.admin.invitations.resend(pharmacist.userId);
      setNotice(
        result.emailSent
          ? `A new link is on its way to ${pharmacist.email}.`
          : `A new link was created, but the email to ${pharmacist.email} could not be sent.`,
      );
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not resend.");
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
          Inviting a pharmacist creates an account and emails a sign-in link, so
          it is limited to super admins.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}
      {notice && (
        <p className="rounded-md bg-primary/10 px-3 py-2 text-sm text-primary">{notice}</p>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <SingleInvite onDone={(m) => { setNotice(m); void load(); }} onError={setError} />
        <BulkImport onDone={(m) => { setNotice(m); void load(); }} onError={setError} />
      </div>

      <Panel
        title={items ? `Invited pharmacists (${items.length})` : "Invited pharmacists"}
        description="Everyone added by invitation. Pharmacists who signed up themselves are under Users."
        action={
          <div className="flex flex-wrap gap-2">
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search name or email"
              className="h-8 w-48 text-xs"
            />
            <select
              value={status}
              onChange={(e) => setStatus(e.target.value as InvitationStatus | "")}
              className="h-8 rounded-md border border-input bg-background px-2 text-xs"
              aria-label="Filter by status"
            >
              <option value="">Any status</option>
              <option value="Invited">Invited</option>
              <option value="Activated">Activated</option>
              <option value="Expired">Expired</option>
            </select>
          </div>
        }
      >
        {items && items.length === 0 && !search && !status ? (
          <EmptyState
            icon={MailPlus}
            title="Nobody invited yet"
            message="Add a pharmacist above, or import a list from your spreadsheet."
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-4 py-2 font-medium">Pharmacist</th>
                  <th className="px-4 py-2 font-medium">ODS</th>
                  <th className="px-4 py-2 font-medium">Status</th>
                  <th className="px-4 py-2 font-medium">Invited</th>
                  <th className="px-4 py-2 text-right font-medium">
                    <span className="sr-only">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {!items ? (
                  <TableLoading colSpan={5} />
                ) : items.length === 0 ? (
                  <TableEmpty colSpan={5} message="Nobody matches those filters." />
                ) : (
                  items.map((p) => (
                    <tr key={p.userId} className="border-b border-border/40">
                      <td className="px-4 py-2">
                        <p className="font-medium">{p.displayName}</p>
                        <p className="text-xs text-muted-foreground">{p.email}</p>
                        {p.pharmacyName && (
                          <p className="text-[11px] text-muted-foreground">
                            {p.pharmacyName}
                            {p.postCode ? ` · ${p.postCode}` : ""}
                          </p>
                        )}
                      </td>
                      <td className="px-4 py-2 font-mono text-xs text-muted-foreground">
                        {p.fCode ?? "—"}
                      </td>
                      <td className="px-4 py-2">
                        <span
                          className={cn(
                            "rounded-full px-2 py-0.5 text-[11px] font-medium",
                            TONE[invitationStatusTone(p.status)],
                          )}
                        >
                          {p.status}
                        </span>
                        {p.status === "Expired" && p.expiresAtUtc && (
                          <p className="mt-0.5 text-[11px] text-muted-foreground">
                            expired {formatDate(p.expiresAtUtc)}
                          </p>
                        )}
                      </td>
                      <td className="px-4 py-2 text-xs text-muted-foreground">
                        {formatDate(p.invitedAtUtc)}
                        {p.timesSent > 1 && (
                          <span className="block text-[11px]">sent {p.timesSent}×</span>
                        )}
                        {p.invitedByEmail && (
                          <span className="block text-[11px]">by {p.invitedByEmail}</span>
                        )}
                      </td>
                      <td className="px-4 py-2 text-right">
                        {canResendInvitation(p.status) && (
                          <Button
                            size="sm"
                            variant="outline"
                            className="h-7 gap-1 text-xs"
                            disabled={busy === p.userId}
                            onClick={() => resend(p)}
                          >
                            <RefreshCw className="h-3.5 w-3.5" />
                            {p.status === "Expired" ? "Send new link" : "Resend"}
                          </Button>
                        )}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </div>
  );
}

/* ------------------------------------------------------------------ */

function SingleInvite({
  onDone,
  onError,
}: {
  onDone: (message: string) => void;
  onError: (message: string) => void;
}) {
  const empty: InvitePharmacistRequest = { fullName: "", email: "" };
  const [form, setForm] = useState<InvitePharmacistRequest>(empty);
  const [busy, setBusy] = useState(false);

  function set<K extends keyof InvitePharmacistRequest>(
    key: K,
    value: InvitePharmacistRequest[K],
  ) {
    setForm((f) => ({ ...f, [key]: value }));
  }

  async function submit() {
    setBusy(true);
    try {
      const result = await api.admin.invitations.invite(form);
      onDone(
        result.emailSent
          ? `Invitation sent to ${result.email}.`
          : `Account created for ${result.email}, but the email could not be sent — resend it below.`,
      );
      setForm(empty);
    } catch (err) {
      onError(err instanceof Error ? err.message : "Could not send the invitation.");
    } finally {
      setBusy(false);
    }
  }

  const ready = form.fullName.trim().length > 0 && form.email.trim().includes("@");

  return (
    <Panel title="Invite one pharmacist" description="They set their own password from the email.">
      <div className="grid gap-3 p-4 sm:grid-cols-2">
        <Field label="Full name" required>
          <Input value={form.fullName} onChange={(e) => set("fullName", e.target.value)} />
        </Field>
        <Field label="Email" required>
          <Input
            type="email"
            value={form.email}
            onChange={(e) => set("email", e.target.value)}
          />
        </Field>
        <Field label="ODS / F-code">
          <Input value={form.fCode ?? ""} onChange={(e) => set("fCode", e.target.value)} />
        </Field>
        <Field label="Pharmacy name">
          <Input
            value={form.pharmacyName ?? ""}
            onChange={(e) => set("pharmacyName", e.target.value)}
          />
        </Field>
        <Field label="Address">
          <Input value={form.address ?? ""} onChange={(e) => set("address", e.target.value)} />
        </Field>
        <Field label="Postcode">
          <Input value={form.postCode ?? ""} onChange={(e) => set("postCode", e.target.value)} />
        </Field>
        <Field label="Phone">
          <Input value={form.phone ?? ""} onChange={(e) => set("phone", e.target.value)} />
        </Field>

        <div className="flex items-end sm:col-span-2">
          <Button size="sm" className="gap-1.5" disabled={!ready || busy} onClick={submit}>
            <UserPlus className="h-3.5 w-3.5" />
            {busy ? "Sending…" : "Send invitation"}
          </Button>
        </div>
      </div>
    </Panel>
  );
}

/**
 * CSV import, in two steps.
 *
 * The preview is not optional politeness. A file pointed at the wrong columns
 * would otherwise create hundreds of accounts and send hundreds of emails,
 * neither of which can be recalled — so nothing is created until someone has
 * seen what the parser made of it.
 */
function BulkImport({
  onDone,
  onError,
}: {
  onDone: (message: string) => void;
  onError: (message: string) => void;
}) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [preview, setPreview] = useState<CsvPreview | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<BulkInviteResult | null>(null);

  async function choose(file: File) {
    setBusy(true);
    setResult(null);
    try {
      setFileName(file.name);
      setPreview(await api.admin.invitations.previewCsv(file));
    } catch (err) {
      onError(err instanceof Error ? err.message : "Could not read that file.");
      setPreview(null);
    } finally {
      setBusy(false);
    }
  }

  async function importAll() {
    if (!preview || preview.valid.length === 0) return;
    setBusy(true);
    try {
      const outcome = await api.admin.invitations.inviteMany(preview.valid);
      setResult(outcome);
      setPreview(null);
      onDone(
        `${outcome.invited} invited, ${outcome.emailsSent} emails sent` +
          (outcome.failed > 0 ? `, ${outcome.failed} failed.` : "."),
      );
    } catch (err) {
      onError(err instanceof Error ? err.message : "The import failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel
      title="Import a list"
      description="CSV with a name and email column. Everything else is optional."
      action={
        <Button asChild size="sm" variant="ghost" className="h-7 gap-1 text-xs">
          <a href={api.admin.invitations.csvTemplateUrl}>
            <Download className="h-3.5 w-3.5" />
            Template
          </a>
        </Button>
      }
    >
      <div className="space-y-3 p-4">
        <input
          ref={inputRef}
          type="file"
          accept=".csv,text/csv"
          className="hidden"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) void choose(file);
            // Cleared so choosing the same file twice still fires a change.
            e.target.value = "";
          }}
        />

        <Button
          size="sm"
          variant="outline"
          className="gap-1.5"
          disabled={busy}
          onClick={() => inputRef.current?.click()}
        >
          <Upload className="h-3.5 w-3.5" />
          {fileName ?? "Choose a CSV"}
        </Button>

        {preview && (
          <div className="space-y-2 rounded-md border border-border/60 p-3">
            <p className="text-sm">
              <span className="font-medium">{preview.valid.length}</span> ready to invite
              {preview.errors.length > 0 && (
                <span className="text-destructive">
                  {" "}
                  · {preview.errors.length} need attention
                </span>
              )}
            </p>

            {preview.errors.length > 0 && (
              <ul className="max-h-32 space-y-0.5 overflow-y-auto text-[11px] text-muted-foreground">
                {preview.errors.slice(0, 20).map((e, i) => (
                  <li key={i} className="flex items-start gap-1.5">
                    <AlertTriangle className="mt-0.5 h-3 w-3 shrink-0 text-amber-600" />
                    <span>
                      Line {e.line}: {e.reason}
                      {e.email ? ` (${e.email})` : ""}
                    </span>
                  </li>
                ))}
                {preview.errors.length > 20 && (
                  <li>…and {preview.errors.length - 20} more</li>
                )}
              </ul>
            )}

            <div className="flex gap-2 pt-1">
              <Button
                size="sm"
                disabled={busy || preview.valid.length === 0}
                onClick={importAll}
              >
                {busy ? "Importing…" : `Invite ${preview.valid.length}`}
              </Button>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => {
                  setPreview(null);
                  setFileName(null);
                }}
              >
                Cancel
              </Button>
            </div>

            {preview.errors.length > 0 && preview.valid.length > 0 && (
              <p className="text-[11px] text-muted-foreground">
                The rows above are skipped. The rest are still imported — fix
                them in your file and import again.
              </p>
            )}
          </div>
        )}

        {result && (
          <div className="rounded-md bg-muted p-3 text-xs">
            <p className="font-medium">
              {result.invited} invited · {result.emailsSent} emails sent
              {result.failed > 0 && ` · ${result.failed} failed`}
            </p>
            {result.results.filter((r) => !r.success).length > 0 && (
              <ul className="mt-1.5 max-h-32 space-y-0.5 overflow-y-auto text-muted-foreground">
                {result.results
                  .filter((r) => !r.success)
                  .map((r, i) => (
                    <li key={i}>
                      {r.email}: {r.error}
                    </li>
                  ))}
              </ul>
            )}
          </div>
        )}
      </div>
    </Panel>
  );
}

function Field({
  label,
  required,
  children,
}: {
  label: string;
  required?: boolean;
  children: React.ReactNode;
}) {
  return (
    <div className="space-y-1">
      <Label className="text-xs">
        {label}
        {required && <span className="text-destructive"> *</span>}
      </Label>
      {children}
    </div>
  );
}

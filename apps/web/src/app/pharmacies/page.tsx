"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { BarChart3, Building2, Eye, Link2Off, Loader2, MailCheck, Plus } from "lucide-react";
import type {
  ChildPharmacy,
  CreateChildPharmacyRequest,
  PharmacyContext,
} from "@acme/shared-types";
import { isAwaitingActivation } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { AccessRequests } from "./AccessRequests";

/**
 * Branches — the shops a pharmacy group runs under one account.
 *
 * A child is a full account, not a sub-record: it signs in, owns its own
 * patients and posters, and works whether or not the parent ever opens this
 * page. The parent gets access in addition, never instead — which is why the
 * copy here says "manage" and not "control".
 */
export default function BranchesPage() {
  const [children, setChildren] = useState<ChildPharmacy[] | null>(null);
  const [context, setContext] = useState<PharmacyContext | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  const load = useCallback(async () => {
    try {
      const [kids, ctx] = await Promise.all([
        api.pharmacies.children(),
        api.pharmacies.context(),
      ]);
      setChildren(kids);
      setContext(ctx);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load your pharmacies.");
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function manage(child: ChildPharmacy) {
    setBusy(child.userId);
    setError(null);
    try {
      await api.pharmacies.startManageAs(child.userId);
      // Hard navigation: the delegated cookie must be in play for every
      // subsequent request, and a soft transition would leave pages rendered
      // under the previous identity still mounted.
      window.location.href = "/dashboard";
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not start managing that pharmacy.");
      setBusy(null);
    }
  }

  async function detach(child: ChildPharmacy) {
    if (
      !window.confirm(
        `Remove ${child.pharmacyName || child.displayName} from your group?\n\n` +
          "Their account, patients and posters are untouched — they keep working " +
          "independently. You simply lose access to manage them.",
      )
    )
      return;

    setBusy(child.userId);
    try {
      await api.pharmacies.detachChild(child.userId);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not remove that pharmacy.");
    } finally {
      setBusy(null);
    }
  }

  // A child cannot have children of its own — one level only. Saying so is
  // better than hiding the button and leaving someone hunting for it.
  //
  // Read the explicit flag, never the presence of a parent object. Inferring it
  // from `parent !== null` is what produced the bug where every top-level
  // pharmacy was told it was a branch: the value being tested had been
  // manufactured two layers away and was never a parent at all.
  //
  // Undefined while the request is in flight, so the notice does not flash on
  // for a pharmacy that turns out not to be a branch.
  const isChild = context?.isChildPharmacy === true;
  const parent = context?.parent ?? null;
  // Undefined while the request is in flight, so neither the badge nor the Add
  // button flashes on for a pharmacy that turns out not to be a head office.
  const isHeadOffice = context?.isHeadOffice === true;

  return (
    <main className="container max-w-5xl py-8">
      <header className="mb-6 flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="text-2xl font-semibold tracking-tight">Branches</h1>
            {/* The badge is the point of the role being visible: it says which
                kind of account you are signed in to, on the page where that
                difference decides what you can do. */}
            {isHeadOffice && (
              <Badge variant="secondary" className="gap-1">
                <Building2 className="h-3 w-3" aria-hidden />
                Head office
              </Badge>
            )}
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            Branches you manage. Each one signs in and works on its own; you can
            step in when needed.
          </p>
        </div>

        {/* Offered only to an account that may actually do it. Showing the
            button to everyone and refusing on submit teaches people that the
            app's controls cannot be trusted. */}
        {!isChild && isHeadOffice && (
          <Button className="gap-1.5" onClick={() => setAdding(true)}>
            <Plus className="h-4 w-4" />
            Add branch
          </Button>
        )}
      </header>

      {error && (
        <p className="mb-4 rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
          {error}
        </p>
      )}
      {notice && (
        <p className="mb-4 rounded-md bg-primary/10 px-3 py-2 text-sm text-primary">{notice}</p>
      )}

      {isChild && (
        <Card className="mb-6 border-primary/30 bg-primary/5">
          <CardHeader className="pb-3">
            <CardTitle className="text-base">
              {/* The parent can be missing while isChild is true: its account
                  was deleted. Name the state rather than rendering "Part of"
                  followed by nothing. */}
              {parent
                ? `Part of ${parent.pharmacyName || parent.displayName}`
                : "Part of another pharmacy group"}
            </CardTitle>
            <CardDescription>
              This pharmacy sits under another, so it cannot have branches
              of its own. Your account is otherwise completely independent.
            </CardDescription>
          </CardHeader>
        </Card>
      )}

      {children === null ? (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading…
        </p>
      ) : children.length === 0 ? (
        !isChild && (
          <Card>
            <CardContent className="flex flex-col items-center gap-2 py-12 text-center">
              <div className="rounded-full bg-muted p-3">
                <Building2 className="h-5 w-5 text-muted-foreground" aria-hidden />
              </div>
              <p className="text-sm font-medium">No branches yet</p>
              <p className="max-w-sm text-xs text-muted-foreground">
                Add a branch and we will email them an activation link. They set
                their own password and can work independently from day one.
              </p>
            </CardContent>
          </Card>
        )
      ) : (
        <ul className="grid gap-3 sm:grid-cols-2">
          {children.map((c) => {
            const pending = isAwaitingActivation(c);
            return (
              <li key={c.userId}>
                <Card className={cn(pending && "border-dashed")}>
                  <CardHeader className="pb-3">
                    <div className="flex items-start justify-between gap-2">
                      <div className="min-w-0">
                        <CardTitle className="truncate text-base">
                          {c.pharmacyName || c.displayName}
                        </CardTitle>
                        <CardDescription className="truncate">{c.email}</CardDescription>
                      </div>
                      {pending && (
                        <span className="flex shrink-0 items-center gap-1 rounded-full bg-amber-500/15 px-2 py-0.5 text-[11px] font-medium text-amber-700 dark:text-amber-400">
                          <MailCheck className="h-3 w-3" />
                          Invited
                        </span>
                      )}
                    </div>
                    <p className="mt-1 text-[11px] text-muted-foreground">
                      {c.fCode ? `${c.fCode} · ` : ""}
                      {c.postCode || "No postcode"}
                    </p>
                  </CardHeader>

                  <CardContent className="flex flex-wrap justify-end gap-1.5 pt-0">
                    {/* The way into a branch's records without becoming it. Opening
                        the detail view is audited, and so is switching — both routes
                        to a branch's data leave a trace. */}
                    <Button asChild size="sm" variant="outline" className="gap-1.5">
                      <Link href={`/pharmacies/${c.userId}`}>
                        <BarChart3 className="h-3.5 w-3.5" />
                        View
                      </Link>
                    </Button>
                    <Button
                      size="sm"
                      className="gap-1.5"
                      // A never-activated account has no owner yet; managing it
                      // would be using an account nobody has claimed.
                      disabled={pending || !c.isActive || busy === c.userId}
                      title={
                        pending
                          ? "They need to activate their account first"
                          : undefined
                      }
                      onClick={() => manage(c)}
                    >
                      <Eye className="h-3.5 w-3.5" />
                      Manage
                    </Button>
                    <Button
                      size="sm"
                      variant="ghost"
                      className="gap-1.5 text-xs text-muted-foreground"
                      disabled={busy === c.userId}
                      onClick={() => detach(c)}
                    >
                      <Link2Off className="h-3.5 w-3.5" />
                      Remove
                    </Button>
                  </CardContent>
                </Card>
              </li>
            );
          })}
        </ul>
      )}

      <AddChildDialog
        open={adding}
        onClose={() => setAdding(false)}
        onCreated={async (name) => {
          setAdding(false);
          setNotice(`${name} has been invited. They will receive an activation email.`);
          await load();
        }}
        onError={setError}
      />

      {/* Asking to work as a pharmacy you do not belong to.
          Placed here because this is already the page about which pharmacies
          you can reach — a separate screen would be one more thing to find. */}
      <section className="mt-10 border-t border-border/60 pt-8">
        <AccessRequests />
      </section>
    </main>
  );
}

function AddChildDialog({
  open,
  onClose,
  onCreated,
  onError,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (name: string) => void;
  onError: (message: string) => void;
}) {
  const empty: CreateChildPharmacyRequest = { fullName: "", email: "" };
  const [form, setForm] = useState<CreateChildPharmacyRequest>(empty);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (open) setForm(empty);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  if (!open) return null;

  function set<K extends keyof CreateChildPharmacyRequest>(
    key: K,
    value: CreateChildPharmacyRequest[K],
  ) {
    setForm((f) => ({ ...f, [key]: value }));
  }

  async function submit() {
    setBusy(true);
    try {
      const created = await api.pharmacies.createChild(form);
      onCreated(created.pharmacyName || created.displayName);
    } catch (err) {
      onError(err instanceof Error ? err.message : "Could not add that pharmacy.");
    } finally {
      setBusy(false);
    }
  }

  const ready = form.fullName.trim().length > 0 && form.email.trim().includes("@");

  return (
    <Dialog open onOpenChange={(v) => !v && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Add a branch</DialogTitle>
          <DialogDescription>
            They receive an activation email and set their own password.
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1 sm:col-span-2">
            <Label className="text-xs">Contact name *</Label>
            <Input value={form.fullName} onChange={(e) => set("fullName", e.target.value)} />
          </div>
          <div className="space-y-1 sm:col-span-2">
            <Label className="text-xs">Email *</Label>
            <Input
              type="email"
              value={form.email}
              onChange={(e) => set("email", e.target.value)}
            />
          </div>
          <div className="space-y-1 sm:col-span-2">
            <Label className="text-xs">Pharmacy name</Label>
            <Input
              value={form.pharmacyName ?? ""}
              onChange={(e) => set("pharmacyName", e.target.value)}
              placeholder="Defaults to the contact name"
            />
          </div>
          <div className="space-y-1">
            <Label className="text-xs">ODS / F-code</Label>
            <Input value={form.fCode ?? ""} onChange={(e) => set("fCode", e.target.value)} />
          </div>
          <div className="space-y-1">
            <Label className="text-xs">Postcode</Label>
            <Input
              value={form.postCode ?? ""}
              onChange={(e) => set("postCode", e.target.value)}
            />
          </div>
        </div>

        <p className="rounded-md bg-muted px-3 py-2 text-[11px] text-muted-foreground">
          You will be able to manage their patients, posters and campaigns. They
          keep their own login, and everything you do while managing them is
          recorded against your name.
        </p>

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="outline" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button size="sm" disabled={!ready || busy} onClick={submit}>
            {busy ? "Inviting…" : "Send invitation"}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}

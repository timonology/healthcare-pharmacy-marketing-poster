"use client";

import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { CheckCircle2, MailX, XCircle } from "lucide-react";
import type { UnsubscribePreview } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";

/**
 * Stop a pharmacy's campaign emails, from the link in the email (security
 * review R2-06).
 *
 * <b>A button, not an action on load.</b> Mail scanners and link previews
 * fetch every link in a message, so a page that unsubscribed when opened would
 * unsubscribe people who never clicked. Loading only describes; the press
 * unsubscribes. (Mail clients' own "Unsubscribe" buttons post straight to the
 * API and never come here.)
 *
 * Public, no sign-in: the person is a patient, not a user of this app.
 */
function Unsubscribe() {
  const token = useSearchParams().get("token") ?? "";
  const [preview, setPreview] = useState<UnsubscribePreview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!token) {
      setError("This link is incomplete. Try copying the whole link from the email.");
      return;
    }
    let cancelled = false;
    api
      .unsubscribePreview(token)
      .then((p) => !cancelled && setPreview(p))
      .catch((err) => !cancelled && setError(err instanceof Error ? err.message : "This link is not valid."));
    return () => {
      cancelled = true;
    };
  }, [token]);

  async function confirm() {
    setBusy(true);
    setError(null);
    try {
      setPreview(await api.unsubscribe(token));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not unsubscribe. Try again.");
    } finally {
      setBusy(false);
    }
  }

  if (error) {
    return (
      <div className="space-y-4 text-center">
        <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10">
          <XCircle className="h-6 w-6 text-destructive" aria-hidden />
        </div>
        <h1 className="text-xl font-semibold">We could not use this link</h1>
        <p className="text-sm text-muted-foreground">{error}</p>
        <p className="text-sm text-muted-foreground">
          You can also reply to the pharmacy and ask them to stop emailing you.
        </p>
      </div>
    );
  }

  if (!preview) {
    return <p className="text-center text-sm text-muted-foreground">Loading…</p>;
  }

  if (preview.unsubscribed) {
    return (
      <div className="space-y-4 text-center" role="status">
        <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <CheckCircle2 className="h-6 w-6 text-primary" aria-hidden />
        </div>
        <h1 className="text-xl font-semibold">You are unsubscribed</h1>
        <p className="text-sm text-muted-foreground">
          {preview.pharmacyName} will not send marketing emails to {preview.maskedEmail} again.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4 text-center">
      <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
        <MailX className="h-6 w-6 text-primary" aria-hidden />
      </div>
      <h1 className="text-xl font-semibold">Stop emails from {preview.pharmacyName}?</h1>
      <p className="text-sm text-muted-foreground">
        {preview.maskedEmail} will no longer receive their marketing emails.
      </p>
      <Button className="w-full" onClick={confirm} disabled={busy}>
        {busy ? "Unsubscribing…" : "Unsubscribe"}
      </Button>
    </div>
  );
}

export default function UnsubscribePage() {
  return (
    <main className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background via-background to-primary/[0.04] px-4">
      <div className="w-full max-w-md rounded-2xl border border-border/60 bg-card p-8 shadow-sm">
        <Suspense fallback={<p className="text-center text-sm text-muted-foreground">Loading…</p>}>
          <Unsubscribe />
        </Suspense>
      </div>
    </main>
  );
}

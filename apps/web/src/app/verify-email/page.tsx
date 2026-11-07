"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { CheckCircle2, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";

/**
 * Confirming an email address from the emailed link.
 *
 * Redeems on load rather than behind a button. The click already happened — in
 * the inbox — and asking someone to click "confirm" a second time to confirm
 * that they clicked confirm is friction with nothing on the other side of it.
 *
 * The guard against React's development double-render matters here: the token
 * is single-use, so firing twice would redeem it and then report failure on the
 * second call, showing an error for a verification that actually succeeded.
 */
function VerifyEmail() {
  const token = useSearchParams().get("token") ?? "";
  const [state, setState] = useState<"working" | "done" | "failed">("working");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!token) {
      setState("failed");
      setError("This link is incomplete. Try copying the whole link from the email.");
      return;
    }

    let cancelled = false;

    (async () => {
      try {
        await api.verifyEmail(token);
        if (!cancelled) setState("done");
      } catch (err) {
        if (cancelled) return;
        setState("failed");
        setError(err instanceof Error ? err.message : "Could not confirm the address.");
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [token]);

  if (state === "working") {
    return <p className="text-center text-sm text-muted-foreground">Confirming your address…</p>;
  }

  if (state === "done") {
    return (
      <div className="space-y-4 text-center">
        <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
          <CheckCircle2 className="h-6 w-6 text-primary" />
        </div>
        <h1 className="text-xl font-semibold">Address confirmed</h1>
        <p className="text-sm text-muted-foreground">
          Thank you — this is where campaign reports and password resets will be sent.
        </p>
        <Button asChild className="w-full">
          <Link href="/dashboard">Go to my dashboard</Link>
        </Button>
      </div>
    );
  }

  return (
    <div className="space-y-4 text-center">
      <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10">
        <XCircle className="h-6 w-6 text-destructive" />
      </div>
      <h1 className="text-xl font-semibold">We could not confirm that</h1>
      <p className="text-sm text-muted-foreground">{error}</p>
      <p className="text-sm text-muted-foreground">
        Sign in and we will send you a fresh link.
      </p>
      <Button asChild variant="outline" className="w-full">
        <Link href="/login">Go to sign in</Link>
      </Button>
    </div>
  );
}

export default function VerifyEmailPage() {
  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background via-background to-primary/[0.04] px-4">
      <div className="w-full max-w-md rounded-2xl border border-border/60 bg-card p-8 shadow-sm">
        <Suspense fallback={<p className="text-center text-sm text-muted-foreground">Loading…</p>}>
          <VerifyEmail />
        </Suspense>
      </div>
    </div>
  );
}

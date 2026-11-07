"use client";

import { Suspense, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { AlertTriangle, CheckCircle2, Loader2 } from "lucide-react";
import type { InvitationPreview } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PasswordInput } from "@/components/ui/password-input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api";
import { MIN_PASSWORD_LENGTH } from "@/lib/password-policy";
import { LogoMark } from "@/components/brand/LogoMark";

/**
 * Where an invited pharmacist sets their password.
 *
 * Public by necessity — they have no account to sign in with, so the token in
 * the URL is the only credential. That shapes the whole page: the token is
 * checked before anything is shown, every failure says the same thing (so a
 * guessed link cannot be distinguished from an expired one), and the copy
 * always points at "ask your administrator to send a new one", because that is
 * the only recovery available to someone who cannot log in.
 */
export default function ActivatePage() {
  return (
    <Suspense
      fallback={
        <main className="container flex max-w-md justify-center py-24">
          <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
        </main>
      }
    >
      <ActivateView />
    </Suspense>
  );
}

function ActivateView() {
  const router = useRouter();
  const params = useSearchParams();
  const token = params?.get("token") ?? "";

  const [invite, setInvite] = useState<InvitationPreview | null>(null);
  const [checking, setChecking] = useState(true);
  const [linkError, setLinkError] = useState<string | null>(null);

  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  useEffect(() => {
    if (!token) {
      setLinkError("This link is missing its activation code.");
      setChecking(false);
      return;
    }

    let cancelled = false;
    api
      .previewInvitation(token)
      .then((preview) => {
        if (!cancelled) setInvite(preview);
      })
      .catch((err: unknown) => {
        if (!cancelled)
          setLinkError(
            err instanceof Error ? err.message : "This activation link is not valid.",
          );
      })
      .finally(() => {
        if (!cancelled) setChecking(false);
      });

    return () => {
      cancelled = true;
    };
  }, [token]);

  // Checked in the browser as a courtesy; the API enforces the real rule.
  const tooShort = password.length > 0 && password.length < MIN_PASSWORD_LENGTH;
  const mismatch = confirm.length > 0 && password !== confirm;
  const canSubmit = password.length >= MIN_PASSWORD_LENGTH && password === confirm && !busy;

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.acceptInvitation({ token, password });
      setDone(true);
      // Straight to sign-in rather than logging them in automatically. Typing
      // the password once more is a small price for confirming they know it —
      // there is no password-reset flow to rescue them if they do not.
      setTimeout(() => router.replace("/login?activated=1"), 1800);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not activate the account.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="container max-w-md py-16">
      <div className="mb-8 flex items-center gap-2">
        <LogoMark className="h-6 w-6" />
        <span className="text-sm font-medium tracking-tight">Marketing25</span>
      </div>

      {checking ? (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          Checking your link…
        </p>
      ) : linkError ? (
        <div className="rounded-2xl border border-border/60 bg-card p-6">
          <div className="flex items-start gap-2 text-destructive">
            <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0" />
            <div>
              <h1 className="text-lg font-semibold tracking-tight">
                This link cannot be used
              </h1>
              <p className="mt-1.5 text-sm text-muted-foreground">{linkError}</p>
            </div>
          </div>
          <p className="mt-4 text-sm text-muted-foreground">
            Activation links last 72 hours. Ask whoever invited you to send a new
            one — your account is still here, it just needs a fresh link.
          </p>
          <Button asChild variant="outline" size="sm" className="mt-5">
            <Link href="/login">Back to sign in</Link>
          </Button>
        </div>
      ) : done ? (
        <div className="rounded-2xl border border-border/60 bg-card p-6">
          <div className="flex items-center gap-2 text-primary">
            <CheckCircle2 className="h-5 w-5" />
            <h1 className="text-lg font-semibold tracking-tight">Account activated</h1>
          </div>
          <p className="mt-2 text-sm text-muted-foreground">
            Taking you to sign in…
          </p>
        </div>
      ) : (
        <div className="rounded-2xl border border-border/60 bg-card p-6 shadow-xl shadow-black/[0.03]">
          <h1 className="text-xl font-semibold tracking-tight">Set your password</h1>
          <p className="mt-1.5 text-sm text-muted-foreground">
            Activating{" "}
            <span className="font-medium text-foreground">{invite?.email}</span>
            {invite?.pharmacyName ? ` for ${invite.pharmacyName}` : ""}.
          </p>

          {error && (
            <p className="mt-4 rounded-md bg-destructive/10 px-3 py-2 text-xs text-destructive">
              {error}
            </p>
          )}

          <div className="mt-6 space-y-4">
            <div className="space-y-1.5">
              <Label htmlFor="password">New password</Label>
              <PasswordInput
                id="password"
                autoComplete="new-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoFocus
              />
              <p
                className={
                  tooShort
                    ? "text-xs text-destructive"
                    : "text-xs text-muted-foreground"
                }
              >
                At least {MIN_PASSWORD_LENGTH} characters.
              </p>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="confirm">Confirm password</Label>
              <PasswordInput
                id="confirm"
                revealLabel="confirm password"
                autoComplete="new-password"
                value={confirm}
                onChange={(e) => setConfirm(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && canSubmit) void submit();
                }}
              />
              {mismatch && (
                <p className="text-xs text-destructive">These do not match.</p>
              )}
            </div>

            <Button className="w-full" disabled={!canSubmit} onClick={submit}>
              {busy ? "Activating…" : "Activate my account"}
            </Button>

            {invite && (
              <p className="text-center text-xs text-muted-foreground">
                This link expires{" "}
                {new Date(invite.expiresAtUtc).toLocaleString()}.
              </p>
            )}
          </div>
        </div>
      )}
    </main>
  );
}

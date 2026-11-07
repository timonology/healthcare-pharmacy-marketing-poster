"use client";

import Link from "next/link";
import { Suspense, useState } from "react";
import { useSearchParams } from "next/navigation";
import { ArrowLeft, ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PasswordInput } from "@/components/ui/password-input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { ThemeToggle } from "@/components/theme-toggle";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import { safeRedirect } from "@/lib/safe-redirect";
import { LogoMark } from "@/components/brand/LogoMark";

export default function LoginPage() {
  return (
    <div className="relative min-h-screen overflow-hidden bg-gradient-to-br from-background via-background to-primary/[0.04]">
      <BackgroundDecor />

      <div className="absolute left-4 top-4 z-20 flex items-center gap-2 md:left-6 md:top-6">
        <Button asChild variant="ghost" size="sm">
          <Link href="/">
            <ArrowLeft className="mr-1.5 h-4 w-4" />
            Back to home
          </Link>
        </Button>
      </div>

      <div className="absolute right-4 top-4 z-20 md:right-6 md:top-6">
        <ThemeToggle />
      </div>

      <main className="container relative flex min-h-screen items-center justify-center px-4 py-16">
        <div className="w-full max-w-md">
          <div className="flex flex-col items-center text-center">
            <LogoMark className="h-12 w-12" title="Marketing25" />
            <h1 className="mt-5 text-2xl font-bold tracking-tight">
              Welcome back
            </h1>
            <p className="mt-1.5 text-sm text-muted-foreground">
              Sign in to continue designing.
            </p>
          </div>

          <div className="mt-8 rounded-2xl border border-border/60 bg-card p-6 shadow-xl shadow-black/[0.03] sm:p-8">
            <Suspense fallback={<LoginSkeleton />}>
              <LoginForm />
            </Suspense>

            <p className="mt-6 text-center text-sm text-muted-foreground">
              Don&apos;t have an account?{" "}
              <Link
                href="/register"
                className="font-medium text-primary hover:underline"
              >
                Create one for free
              </Link>
            </p>
          </div>

          <p className="mt-6 flex items-center justify-center gap-1.5 text-xs text-muted-foreground">
            <ShieldCheck className="h-3.5 w-3.5" />
            Bank-grade encryption · GDPR safe
          </p>
        </div>
      </main>
    </div>
  );
}

function LoginForm() {
  const params = useSearchParams();
  const setUser = useAuthStore((s) => s.setUser);

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setPending(true);
    setError(null);
    try {
      const { user } = await api.login({ email, password });
      setUser(user);
      const fallback = user.onboardingCompleted ? "/dashboard" : "/onboarding";
      const target = safeRedirect(params.get("redirect"), window.location.origin) ?? fallback;
      window.location.assign(target);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login failed");
      setPending(false);
    }
  }

  // Set by expireSession() when a session ends mid-use. Without this the user
  // is silently dumped on the login page with no idea why, which reads like
  // the app logged them out at random.
  const reason = params.get("reason");
  const expiryNotice =
    reason === "inactive"
      ? "You were signed out after a period of inactivity. This protects patient information on shared devices."
      : reason === "expired"
        ? "Your session timed out. Sign in again to pick up where you left off."
        : reason === "revoked"
          ? "You've been signed out. If this keeps happening, contact your administrator."
          : null;

  // Confirmations from the flows that end here. A password reset signs the
  // account out everywhere, so it lands on this page — without a word of
  // explanation that looks like the reset failed and dumped them at login.
  const doneNotice =
    params.get("reset") === "1"
      ? "Your password has been changed. Sign in with your new password."
      : params.get("activated") === "1"
        ? "Your account is ready. Sign in to get started."
        : null;

  return (
    <form onSubmit={onSubmit} className="space-y-4">
      {doneNotice && (
        <p
          role="status"
          className="rounded-md border border-primary/30 bg-primary/5 p-3 text-sm text-foreground"
        >
          {doneNotice}
        </p>
      )}

      {expiryNotice && (
        <p
          role="status"
          className="rounded-md border border-border/60 bg-muted/50 p-3 text-sm text-muted-foreground"
        >
          {expiryNotice}
        </p>
      )}

      <Button
        type="button"
        variant="outline"
        className="w-full"
        disabled
        title="Google sign-in coming soon"
      >
        <GoogleMark />
        Sign in with Google
      </Button>

      <div className="relative my-2">
        <Separator />
        <span className="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 bg-card px-2 text-xs uppercase text-muted-foreground">
          or with email
        </span>
      </div>

      <div className="space-y-2">
        <Label htmlFor="email">Email</Label>
        <Input
          id="email"
          type="email"
          autoComplete="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          placeholder="you@pharmacy.com"
          required
        />
      </div>

      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <Label htmlFor="password">Password</Label>
          <Link
            href="/forgot-password"
            className="text-xs font-medium text-primary hover:underline"
          >
            Forgot password?
          </Link>
        </div>
        <PasswordInput
          id="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
      </div>

      <label className="flex items-center gap-2 text-sm text-muted-foreground">
        <input
          type="checkbox"
          checked={remember}
          onChange={(e) => setRemember(e.target.checked)}
          className="h-4 w-4 rounded border-border accent-primary"
        />
        Remember me on this device
      </label>

      {error && (
        <p className="text-sm text-destructive" role="alert">
          {error}
        </p>
      )}

      <Button
        type="submit"
        className="w-full bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground hover:opacity-90"
        disabled={pending}
      >
        {pending ? "Signing in…" : "Sign in"}
      </Button>
    </form>
  );
}

function LoginSkeleton() {
  return (
    <div className="space-y-4">
      <div className="h-10 w-full animate-pulse rounded bg-muted" />
      <div className="h-px bg-border" />
      <div className="h-10 w-full animate-pulse rounded bg-muted" />
      <div className="h-10 w-full animate-pulse rounded bg-muted" />
      <div className="h-10 w-full animate-pulse rounded bg-muted" />
    </div>
  );
}

function GoogleMark() {
  return (
    <svg
      aria-hidden
      viewBox="0 0 18 18"
      className="mr-2 h-4 w-4"
      xmlns="http://www.w3.org/2000/svg"
    >
      <path
        fill="#4285F4"
        d="M17.64 9.2c0-.64-.06-1.25-.16-1.84H9v3.48h4.84a4.14 4.14 0 0 1-1.79 2.71v2.26h2.9c1.7-1.57 2.69-3.88 2.69-6.61z"
      />
      <path
        fill="#34A853"
        d="M9 18c2.43 0 4.46-.81 5.95-2.19l-2.9-2.26c-.8.54-1.83.86-3.05.86-2.34 0-4.32-1.58-5.03-3.71H.97v2.33A8.99 8.99 0 0 0 9 18z"
      />
      <path
        fill="#FBBC05"
        d="M3.97 10.7A5.4 5.4 0 0 1 3.68 9c0-.59.1-1.16.29-1.7V4.97H.97A8.99 8.99 0 0 0 0 9c0 1.45.35 2.82.97 4.03l3-2.33z"
      />
      <path
        fill="#EA4335"
        d="M9 3.58c1.32 0 2.5.46 3.44 1.35l2.58-2.58A8.96 8.96 0 0 0 9 0 8.99 8.99 0 0 0 .97 4.97l3 2.33C4.68 5.16 6.66 3.58 9 3.58z"
      />
    </svg>
  );
}

function BackgroundDecor() {
  return (
    <>
      <div
        aria-hidden
        className="pointer-events-none absolute -top-40 left-1/4 -z-10 h-[420px] w-[420px] rounded-full bg-primary/10 blur-[120px] dark:bg-primary/5"
      />
      <div
        aria-hidden
        className="pointer-events-none absolute -bottom-40 right-1/4 -z-10 h-[420px] w-[420px] rounded-full bg-emerald-400/10 blur-[120px] dark:bg-emerald-400/5"
      />
    </>
  );
}


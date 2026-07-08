"use client";

import Link from "next/link";
import { useState } from "react";
import { ArrowLeft, Pill, ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ThemeToggle } from "@/components/theme-toggle";
import { api } from "@/lib/api";
import { setPendingSignup } from "@/lib/pending-signup";
import { useAuthStore } from "@/store/auth-store";

const SUGGESTED_BRAND_COLORS = [
  "#0e7490",
  "#10b981",
  "#6366f1",
  "#f59e0b",
  "#ef4444",
];

export default function RegisterPage() {
  const setUser = useAuthStore((s) => s.setUser);

  const [pharmacyName, setPharmacyName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [email, setEmail] = useState("");
  const [phone, setPhone] = useState("");
  const [postCode, setPostCode] = useState("");
  const [sonarFCode, setSonarFCode] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [brandColor, setBrandColor] = useState(SUGGESTED_BRAND_COLORS[0]);
  const [acceptTerms, setAcceptTerms] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);

    if (password !== confirmPassword) {
      setError("Passwords don't match.");
      return;
    }
    if (!sonarFCode.trim()) {
      setError("ODS / F code is required.");
      return;
    }
    if (!postCode.trim()) {
      setError("Postcode is required.");
      return;
    }
    if (!acceptTerms) {
      setError("Please accept the terms to continue.");
      return;
    }

    setPending(true);
    try {
      const fullName =
        displayName.trim() || pharmacyName.trim() || email.split("@")[0];
      const { user } = await api.register({
        email,
        password,
        displayName: fullName,
      });
      setUser(user);
      // The register API only takes email/password/name. Stash the pharmacy
      // info collected here so /onboarding and /brand-kit can pre-fill from it.
      setPendingSignup({
        pharmacyName: pharmacyName.trim(),
        contactName: fullName,
        phone: phone.trim(),
        postCode: postCode.trim().toUpperCase(),
        sonarFCode: sonarFCode.trim().toUpperCase(),
        brandColor,
      });
      window.location.assign("/onboarding");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Registration failed");
      setPending(false);
    }
  }

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
        <div className="w-full max-w-lg">
          <div className="flex flex-col items-center text-center">
            <span className="flex h-12 w-12 items-center justify-center rounded-2xl bg-gradient-to-br from-primary to-emerald-500 text-primary-foreground shadow-lg">
              <Pill className="h-6 w-6" />
            </span>
            <h1 className="mt-5 text-2xl font-bold tracking-tight">
              Create your free account
            </h1>
            <p className="mt-1.5 text-sm text-muted-foreground">
              No credit card. Cancel anytime.
            </p>
          </div>

          <form
            onSubmit={onSubmit}
            className="mt-8 space-y-4 rounded-2xl border border-border/60 bg-card p-6 shadow-xl shadow-black/[0.03] sm:p-8"
          >
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <div className="space-y-2 sm:col-span-2">
                <Label htmlFor="pharmacyName">Pharmacy name</Label>
                <Input
                  id="pharmacyName"
                  value={pharmacyName}
                  onChange={(e) => setPharmacyName(e.target.value)}
                  placeholder="Acme Community Pharmacy"
                  required
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor="sonarFCode">ODS / F code</Label>
                <Input
                  id="sonarFCode"
                  value={sonarFCode}
                  onChange={(e) => setSonarFCode(e.target.value.toUpperCase())}
                  placeholder="FA123"
                  autoCapitalize="characters"
                  required
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor="postCode">Postcode</Label>
                <Input
                  id="postCode"
                  value={postCode}
                  onChange={(e) => setPostCode(e.target.value.toUpperCase())}
                  placeholder="SW1A 1AA"
                  autoCapitalize="characters"
                  required
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor="fullName">Your full name</Label>
                <Input
                  id="fullName"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  placeholder="Jane Patel"
                  required
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor="phone">
                  Phone <span className="text-muted-foreground">(optional)</span>
                </Label>
                <Input
                  id="phone"
                  type="tel"
                  value={phone}
                  onChange={(e) => setPhone(e.target.value)}
                  placeholder="020 7946 0000"
                />
              </div>

              <div className="space-y-2 sm:col-span-2">
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
                <Label htmlFor="password">Password</Label>
                <Input
                  id="password"
                  type="password"
                  autoComplete="new-password"
                  minLength={8}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                />
              </div>

              <div className="space-y-2">
                <Label htmlFor="confirmPassword">Confirm password</Label>
                <Input
                  id="confirmPassword"
                  type="password"
                  autoComplete="new-password"
                  minLength={8}
                  value={confirmPassword}
                  onChange={(e) => setConfirmPassword(e.target.value)}
                  required
                />
              </div>
            </div>

            <div className="space-y-2">
              <Label>Pick a brand color</Label>
              <div className="flex items-center gap-2">
                {SUGGESTED_BRAND_COLORS.map((c) => (
                  <button
                    key={c}
                    type="button"
                    aria-label={`Use brand color ${c}`}
                    onClick={() => setBrandColor(c)}
                    className={`h-8 w-8 rounded-full ring-2 ring-offset-2 ring-offset-card transition-all ${
                      brandColor === c
                        ? "ring-foreground"
                        : "ring-transparent hover:ring-border"
                    }`}
                    style={{ background: c }}
                  />
                ))}
                <Input
                  type="color"
                  value={brandColor}
                  onChange={(e) => setBrandColor(e.target.value)}
                  className="h-8 w-12 cursor-pointer p-1"
                  aria-label="Custom brand color"
                />
              </div>
              <p className="text-xs text-muted-foreground">
                You can fine-tune this later in your brand kit.
              </p>
            </div>

            <label className="flex items-start gap-2 pt-2 text-sm text-muted-foreground">
              <input
                type="checkbox"
                checked={acceptTerms}
                onChange={(e) => setAcceptTerms(e.target.checked)}
                className="mt-0.5 h-4 w-4 rounded border-border accent-primary"
                required
              />
              <span>
                I agree to the{" "}
                <Link href="#terms" className="font-medium text-primary hover:underline">
                  terms of service
                </Link>{" "}
                and{" "}
                <Link href="#privacy" className="font-medium text-primary hover:underline">
                  privacy policy
                </Link>
                .
              </span>
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
              {pending ? "Creating your account…" : "Create free account"}
            </Button>

            <p className="text-center text-sm text-muted-foreground">
              Already have an account?{" "}
              <Link
                href="/login"
                className="font-medium text-primary hover:underline"
              >
                Sign in
              </Link>
            </p>
          </form>

          <p className="mt-6 flex items-center justify-center gap-1.5 text-xs text-muted-foreground">
            <ShieldCheck className="h-3.5 w-3.5" />
            GDPR safe · Bank-grade encryption
          </p>
        </div>
      </main>
    </div>
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

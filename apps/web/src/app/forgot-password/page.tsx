"use client";

import Link from "next/link";
import { useState } from "react";
import { ArrowLeft, MailCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api";

/**
 * "I've forgotten my password."
 *
 * The screen always ends in the same place. Whether or not the address has an
 * account, submitting shows the sent confirmation — because telling the
 * visitor "no account with that address" would turn this page into a way to
 * find out which of a list of addresses are customers, which is the first step
 * of a credential-stuffing run.
 *
 * That is also why there is no error state for a valid-looking address: the
 * request is fire-and-forget by design, and the API answers 202 regardless.
 *
 * The one exception is "too many requests". That limit is per connection, not
 * per account, so it gives nothing away — and showing "check your inbox" while
 * nothing was sent made people retry, which kept them refused.
 */
export default function ForgotPasswordPage() {
  const [email, setEmail] = useState("");
  const [sent, setSent] = useState(false);
  const [pending, setPending] = useState(false);
  const [refused, setRefused] = useState<string | null>(null);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setPending(true);
    setRefused(null);
    let limited: string | null = null;
    try {
      limited = (await api.forgotPassword(email)).refused;
    } catch {
      // A network failure must not be distinguishable from success; the
      // person can retry.
    } finally {
      setPending(false);
      if (limited) setRefused(limited);
      else setSent(true);
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-background via-background to-primary/[0.04] px-4">
      <div className="w-full max-w-md">
        <Button asChild variant="ghost" size="sm" className="mb-6">
          <Link href="/login">
            <ArrowLeft className="mr-1.5 h-4 w-4" />
            Back to sign in
          </Link>
        </Button>

        <div className="rounded-2xl border border-border/60 bg-card p-8 shadow-sm">
          {sent ? (
            <div className="space-y-4 text-center">
              <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
                <MailCheck className="h-6 w-6 text-primary" />
              </div>
              <h1 className="text-xl font-semibold">Check your inbox</h1>
              <p className="text-sm text-muted-foreground">
                If <span className="font-medium text-foreground">{email}</span> has an
                account, we have sent a link to reset the password. It works once and
                expires in an hour.
              </p>
              <p className="text-sm text-muted-foreground">
                Were you invited and never set a password? You&apos;ll get your
                activation link instead.
              </p>
              <p className="text-sm text-muted-foreground">
                Nothing arrived? Check the spam folder, then{" "}
                <button
                  type="button"
                  onClick={() => setSent(false)}
                  className="font-medium text-primary hover:underline"
                >
                  try again
                </button>
                .
              </p>
            </div>
          ) : (
            <form onSubmit={onSubmit} className="space-y-5">
              <div className="space-y-1.5">
                <h1 className="text-xl font-semibold">Reset your password</h1>
                <p className="text-sm text-muted-foreground">
                  Enter the email address for your account and we will send you a link.
                </p>
              </div>

              <div className="space-y-2">
                <Label htmlFor="email">Email</Label>
                <Input
                  id="email"
                  type="email"
                  autoComplete="email"
                  required
                  autoFocus
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="you@pharmacy.com"
                />
              </div>

              {refused && (
                <p role="alert" className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
                  {refused}
                </p>
              )}

              <Button type="submit" className="w-full" disabled={pending}>
                {pending ? "Sending…" : "Send reset link"}
              </Button>
            </form>
          )}
        </div>
      </div>
    </div>
  );
}

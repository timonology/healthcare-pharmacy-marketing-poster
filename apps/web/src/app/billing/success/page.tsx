"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { CheckCircle2, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import { useSubscriptionStore } from "@/store/subscription-store";

/**
 * Where Stripe returns the customer after a successful checkout.
 *
 * Deliberately does NOT grant anything. Reaching this URL only means the
 * browser followed a redirect — anyone can type it, and with SCA a payment
 * can still fail after the redirect fires. Entitlement is granted solely by
 * the webhook. All this page does is poll until that webhook lands, so the
 * user sees their new plan instead of a stale one.
 */
export default function BillingSuccessPage() {
  const [state, setState] = useState<"waiting" | "confirmed" | "slow">("waiting");
  const setUser = useAuthStore((s) => s.setUser);
  const setSubscription = useSubscriptionStore((s) => s.setCurrent);

  useEffect(() => {
    let cancelled = false;
    let attempts = 0;

    // Webhook delivery is usually sub-second but isn't guaranteed to beat the
    // redirect. Poll for ~20s, then fall back to a reassuring message rather
    // than spinning forever.
    const poll = async () => {
      attempts += 1;
      try {
        const [me, sub] = await Promise.all([
          api.me(),
          api.getCurrentSubscription().catch(() => null),
        ]);
        if (cancelled) return;

        setUser(me);
        if (sub) setSubscription(sub);

        // Any paid tier, or a credit balance, means the webhook has landed.
        const billing = await api.getBillingStatus().catch(() => null);
        const settled =
          me.tier !== "Free" || (billing?.videoCredits ?? 0) > 0;

        if (settled) {
          setState("confirmed");
          return;
        }
      } catch {
        /* keep polling */
      }

      if (cancelled) return;
      if (attempts >= 10) {
        setState("slow");
        return;
      }
      setTimeout(poll, 2000);
    };

    poll();
    return () => {
      cancelled = true;
    };
  }, [setUser, setSubscription]);

  return (
    <main className="container flex max-w-lg flex-col items-center py-24 text-center">
      {state === "waiting" ? (
        <>
          <Loader2 className="h-10 w-10 animate-spin text-muted-foreground" />
          <h1 className="mt-6 text-2xl font-semibold tracking-tight">
            Confirming your payment
          </h1>
          <p className="mt-2 text-sm text-muted-foreground">
            This usually takes a couple of seconds.
          </p>
        </>
      ) : state === "confirmed" ? (
        <>
          <CheckCircle2 className="h-12 w-12 text-primary" />
          <h1 className="mt-6 text-2xl font-semibold tracking-tight">
            You&apos;re all set
          </h1>
          <p className="mt-2 text-sm text-muted-foreground">
            Your plan is active and your receipt is on its way by email.
          </p>
          <div className="mt-8 flex gap-3">
            <Button asChild>
              <Link href="/dashboard">Go to dashboard</Link>
            </Button>
            <Button asChild variant="outline">
              <Link href="/videos">Make a video</Link>
            </Button>
          </div>
        </>
      ) : (
        <>
          <CheckCircle2 className="h-12 w-12 text-primary" />
          <h1 className="mt-6 text-2xl font-semibold tracking-tight">
            Payment received
          </h1>
          <p className="mt-2 text-sm text-muted-foreground">
            Your account is still updating. It should reflect within a minute —
            refresh the dashboard if you don&apos;t see it. If it hasn&apos;t
            updated in a few minutes, contact support with your receipt and
            we&apos;ll sort it immediately.
          </p>
          <div className="mt-8">
            <Button asChild>
              <Link href="/dashboard">Go to dashboard</Link>
            </Button>
          </div>
        </>
      )}
    </main>
  );
}

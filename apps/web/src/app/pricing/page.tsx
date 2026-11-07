"use client";

import { useEffect, useState } from "react";
import { Check, Lock, Sparkles } from "lucide-react";
import type {
  BillingInterval,
  BillingStatusResponse,
  CurrentSubscription,
  Plan,
} from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { useSubscriptionStore } from "@/store/subscription-store";

export default function PricingPage() {
  const [plans, setPlans] = useState<Plan[]>([]);
  const [billing, setBilling] = useState<BillingStatusResponse | null>(null);
  const [interval, setInterval] = useState<BillingInterval>("Monthly");
  const [loadingPlans, setLoadingPlans] = useState(true);
  const [pendingTier, setPendingTier] = useState<string | null>(null);
  const [pendingPack, setPendingPack] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const current = useSubscriptionStore((s) => s.current);
  const setCurrent = useSubscriptionStore((s) => s.setCurrent);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        // Billing status is allowed to fail — anonymous visitors browsing
        // /pricing aren't authenticated, and the page must still render.
        const [p, c, b] = await Promise.all([
          api.listPlans(),
          api.getCurrentSubscription().catch(() => null),
          api.getBillingStatus().catch(() => null),
        ]);
        if (cancelled) return;
        setPlans(p);
        if (c) setCurrent(c);
        setBilling(b);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load.");
      } finally {
        if (!cancelled) setLoadingPlans(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [setCurrent]);

  async function selectPlan(plan: Plan) {
    setPendingTier(plan.tier);
    setError(null);
    try {
      if (billing?.stripeEnabled) {
        // Real billing: hand off to Stripe Checkout. Must be a top-level
        // navigation — Stripe refuses to render inside an iframe.
        const session = await api.startCheckout(plan.tier, interval);
        window.location.assign(session.url);
        return; // keep the button disabled through the redirect
      }

      // Stripe not configured (local dev) — fall back to the direct tier
      // switch, which the API only permits while billing is disabled.
      const next = await api.upgradeSubscription(plan.tier);
      setCurrent(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not start checkout.");
    } finally {
      setPendingTier(null);
    }
  }

  async function buyCredits(packKey: string) {
    setPendingPack(packKey);
    setError(null);
    try {
      const session = await api.buyCredits(packKey);
      window.location.assign(session.url);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not start checkout.");
      setPendingPack(null);
    }
  }

  async function manageBilling() {
    setError(null);
    try {
      const session = await api.openBillingPortal();
      window.location.assign(session.url);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not open billing portal.");
    }
  }

  const yearlyDiscountPct = 17; // 10 months charged for 12

  // Working as another pharmacy: the plan is shown, but only that pharmacy's
  // own account holder may change it (R3-06). `=== false` so an API that
  // predates the flag keeps today's behaviour rather than locking everyone out.
  const readOnly = billing?.canManageBilling === false;

  return (
    <main className="container max-w-6xl py-12">
      <header className="mx-auto max-w-2xl text-center">
        <h1 className="text-4xl font-bold tracking-tight">Pricing</h1>
        <p className="mt-3 text-muted-foreground">
          Start free. Upgrade when you outgrow it.
        </p>
      </header>

      {/* Interval toggle. Hidden when Stripe is off, since the fallback
          upgrade path has no concept of a billing interval. */}
      {billing?.stripeEnabled && (
        <div className="mt-8 flex items-center justify-center">
          <div
            role="radiogroup"
            aria-label="Billing interval"
            className="inline-flex rounded-lg border border-border/60 bg-muted/40 p-1"
          >
            {(["Monthly", "Yearly"] as const).map((value) => (
              <button
                key={value}
                type="button"
                role="radio"
                aria-checked={interval === value}
                onClick={() => setInterval(value)}
                className={cn(
                  "rounded-md px-4 py-1.5 text-sm font-medium transition-colors",
                  interval === value
                    ? "bg-background text-foreground shadow-sm"
                    : "text-muted-foreground hover:text-foreground",
                )}
              >
                {value}
                {value === "Yearly" && (
                  <span className="ml-1.5 text-xs text-primary">
                    −{yearlyDiscountPct}%
                  </span>
                )}
              </button>
            ))}
          </div>
        </div>
      )}

      {readOnly && (
        <div
          role="note"
          data-testid="billing-read-only"
          className="mx-auto mt-6 flex max-w-2xl items-start gap-3 rounded-lg border border-amber-500/40 bg-amber-50 p-4 text-sm text-amber-950 dark:bg-amber-950 dark:text-amber-100"
        >
          <Lock className="mt-0.5 h-4 w-4 shrink-0" aria-hidden />
          <p>
            You can see this pharmacy&apos;s plan, but only its own account holder can
            change the plan, buy video top-ups or manage billing and invoices. To
            manage your own billing, go back to your account.
          </p>
        </div>
      )}

      {error && (
        <p className="mx-auto mt-6 max-w-md rounded-md border border-destructive/30 bg-destructive/10 p-3 text-center text-sm text-destructive" role="alert">
          {error}
        </p>
      )}

      <section className="mt-10 grid grid-cols-1 gap-6 md:grid-cols-3">
        {loadingPlans
          ? Array.from({ length: 3 }).map((_, i) => <PlanSkeleton key={i} />)
          : plans.map((plan) => (
            <PlanCard
              key={plan.tier}
              plan={plan}
              current={current}
              interval={interval}
              pending={pendingTier === plan.tier}
              readOnly={readOnly}
              onSelect={() => selectPlan(plan)}
            />
          ))}
      </section>

      {/* Credit packs — only meaningful once Stripe is live and the API has
          packs configured. */}
      {billing?.stripeEnabled && !readOnly && billing.creditPacks.length > 0 && (
        <section className="mt-16">
          <div className="mx-auto max-w-2xl text-center">
            <h2 className="text-2xl font-semibold tracking-tight">
              Need more videos?
            </h2>
            <p className="mt-2 text-sm text-muted-foreground">
              Top-up packs never expire and are used only after your monthly
              allowance runs out.
              {billing.videoCredits > 0 && (
                <>
                  {" "}You have{" "}
                  <span className="font-medium text-foreground">
                    {billing.videoCredits} credit{billing.videoCredits === 1 ? "" : "s"}
                  </span>{" "}
                  remaining.
                </>
              )}
            </p>
          </div>

          <div className="mt-8 grid grid-cols-1 gap-4 sm:grid-cols-3">
            {billing.creditPacks.map((pack) => (
              <Card key={pack.key} className="flex flex-col">
                <CardHeader className="pb-3">
                  <CardTitle className="text-lg">{pack.displayName}</CardTitle>
                  <div className="flex items-baseline gap-1">
                    <span className="text-2xl font-bold tracking-tight">
                      £{pack.priceGbp}
                    </span>
                    <span className="text-xs text-muted-foreground">
                      one-time
                    </span>
                  </div>
                </CardHeader>
                <CardFooter className="mt-auto">
                  <Button
                    variant="outline"
                    className="w-full"
                    disabled={pendingPack === pack.key}
                    onClick={() => buyCredits(pack.key)}
                  >
                    {pendingPack === pack.key ? "Redirecting…" : "Buy pack"}
                  </Button>
                </CardFooter>
              </Card>
            ))}
          </div>
        </section>
      )}

      {billing?.hasPaymentMethod && !readOnly && (
        <div className="mt-12 text-center">
          <Button variant="ghost" size="sm" onClick={manageBilling}>
            Manage billing, invoices, and cancellation →
          </Button>
        </div>
      )}
    </main>
  );
}

function PlanCard({
  plan,
  current,
  interval,
  pending,
  readOnly,
  onSelect,
}: {
  plan: Plan;
  current: CurrentSubscription | null;
  interval: BillingInterval;
  pending: boolean;
  /** Working as another pharmacy: the plan is shown, not offered. */
  readOnly: boolean;
  onSelect: () => void;
}) {
  const isCurrent = current?.plan.tier === plan.tier;
  const isPro = plan.tier === "Pro";
  const isFree = plan.tier === "Free";

  // Yearly is billed at 10x the monthly rate — two months free. This mirrors
  // the amounts in scripts/stripe/create_stripe_prices.sh; if you change the
  // multiplier there, change it here too. Stripe remains the source of truth
  // for what is actually charged — this is display only.
  const yearlyPrice = plan.monthlyPriceGbp * 10;
  const showYearly = interval === "Yearly" && !isFree;

  return (
    <Card
      className={cn(
        "flex flex-col",
        isPro && "border-primary shadow-md ring-1 ring-primary/20",
      )}
    >
      <CardHeader className="space-y-2">
        <div className="flex items-center justify-between">
          <CardTitle className="flex items-center gap-2">
            {plan.displayName}
            {isPro && <Sparkles className="h-4 w-4 text-primary" />}
          </CardTitle>
          {isCurrent && <Badge variant="secondary">Current</Badge>}
        </div>
        <div className="flex items-baseline gap-1">
          <span className="text-4xl font-bold tracking-tight">
            £{showYearly ? yearlyPrice : plan.monthlyPriceGbp}
          </span>
          <span className="text-sm text-muted-foreground">
            {isFree ? "" : showYearly ? "/year" : "/month"}
          </span>
        </div>
        {showYearly && (
          <p className="text-xs text-muted-foreground">
            £{(yearlyPrice / 12).toFixed(2)} per month, billed annually
          </p>
        )}
        <CardDescription>{descriptionFor(plan.tier)}</CardDescription>
      </CardHeader>

      <Separator />

      <CardContent className="flex-1 pt-6">
        <ul className="space-y-2 text-sm">
          {plan.highlights.map((h) => (
            <li key={h} className="flex items-start gap-2">
              <Check className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
              <span>{h}</span>
            </li>
          ))}
        </ul>
      </CardContent>

      <CardFooter>
        {readOnly ? (
          // Not a disabled "Choose" button: that reads as broken, or as
          // something to retry. Say what the plan is to this pharmacy instead.
          <p className="w-full text-center text-sm text-muted-foreground">
            {isCurrent ? "This pharmacy's current plan" : "Changed by the pharmacy's own account holder"}
          </p>
        ) : (
          <Button
            className="w-full"
            variant={isPro ? "default" : "outline"}
            disabled={isCurrent || pending}
            onClick={onSelect}
          >
            {isCurrent ? "Your current plan" : pending ? "Updating…" : `Choose ${plan.displayName}`}
          </Button>
        )}
      </CardFooter>
    </Card>
  );
}

function PlanSkeleton() {
  return (
    <Card>
      <CardHeader className="space-y-3">
        <div className="h-6 w-24 animate-pulse rounded bg-muted" />
        <div className="h-9 w-32 animate-pulse rounded bg-muted" />
      </CardHeader>
      <Separator />
      <CardContent className="space-y-2 pt-6">
        {Array.from({ length: 4 }).map((_, i) => (
          <div key={i} className="h-4 w-3/4 animate-pulse rounded bg-muted" />
        ))}
      </CardContent>
      <CardFooter>
        <div className="h-10 w-full animate-pulse rounded bg-muted" />
      </CardFooter>
    </Card>
  );
}

function descriptionFor(tier: string): string {
  switch (tier) {
    case "Free": return "Try the app, no credit card.";
    case "Starter": return "For small pharmacies and individuals.";
    case "Pro": return "For pharmacies running campaigns, videos and screens.";
    default: return "";
  }
}

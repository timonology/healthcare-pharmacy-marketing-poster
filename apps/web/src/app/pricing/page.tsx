"use client";

import { useEffect, useState } from "react";
import { Check, Sparkles } from "lucide-react";
import type { CurrentSubscription, Plan } from "@acme/shared-types";
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
  const [loadingPlans, setLoadingPlans] = useState(true);
  const [pendingTier, setPendingTier] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const current = useSubscriptionStore((s) => s.current);
  const setCurrent = useSubscriptionStore((s) => s.setCurrent);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [p, c] = await Promise.all([
          api.listPlans(),
          api.getCurrentSubscription().catch(() => null),
        ]);
        if (cancelled) return;
        setPlans(p);
        if (c) setCurrent(c);
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

  async function upgrade(plan: Plan) {
    setPendingTier(plan.tier);
    setError(null);
    try {
      const next = await api.upgradeSubscription(plan.tier);
      setCurrent(next);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Upgrade failed.");
    } finally {
      setPendingTier(null);
    }
  }

  return (
    <main className="container max-w-6xl py-12">
      <header className="mx-auto max-w-2xl text-center">
        <h1 className="text-4xl font-bold tracking-tight">Pricing</h1>
        <p className="mt-3 text-muted-foreground">
          Start free. Upgrade when you outgrow it.
        </p>
      </header>

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
              pending={pendingTier === plan.tier}
              onSelect={() => upgrade(plan)}
            />
          ))}
      </section>
    </main>
  );
}

function PlanCard({
  plan,
  current,
  pending,
  onSelect,
}: {
  plan: Plan;
  current: CurrentSubscription | null;
  pending: boolean;
  onSelect: () => void;
}) {
  const isCurrent = current?.plan.tier === plan.tier;
  const isPro = plan.tier === "Pro";

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
            £{plan.monthlyPriceGbp}
          </span>
          <span className="text-sm text-muted-foreground">/month</span>
        </div>
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
        <Button
          className="w-full"
          variant={isPro ? "default" : "outline"}
          disabled={isCurrent || pending}
          onClick={onSelect}
        >
          {isCurrent ? "Your current plan" : pending ? "Updating…" : `Choose ${plan.displayName}`}
        </Button>
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
    case "Pro": return "For teams that need it all.";
    default: return "";
  }
}

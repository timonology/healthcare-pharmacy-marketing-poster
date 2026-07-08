import Link from "next/link";
import { Check, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

const PLANS = [
  {
    tier: "Free",
    price: "£0",
    description: "Try the app, no credit card.",
    features: [
      "Up to 5 saved posters",
      "3 AI generations / month",
      "Basic templates",
      "Watermark on exports",
    ],
    cta: "Start free",
    href: "/register",
    featured: false,
  },
  {
    tier: "Starter",
    price: "£7",
    description: "For small pharmacies and individuals.",
    features: [
      "Up to 30 saved posters",
      "50 AI generations / month",
      "All templates + custom uploads",
      "No watermark",
      "Email export",
    ],
    cta: "Choose Starter",
    href: "/register?plan=starter",
    featured: true,
  },
  {
    tier: "Pro",
    price: "£25",
    description: "For teams that need it all.",
    features: [
      "Unlimited posters",
      "Unlimited AI generations",
      "Priority AI responses",
      "Team collaboration",
      "Custom domain branding",
    ],
    cta: "Choose Pro",
    href: "/register?plan=pro",
    featured: false,
  },
];

export function PricingTeaser() {
  return (
    <section
      id="pricing"
      className="border-y border-border/60 bg-muted/20 py-20 md:py-28"
    >
      <div className="container mx-auto px-4 md:px-6">
        <div className="mx-auto max-w-2xl text-center">
          <span className="text-xs font-semibold uppercase tracking-widest text-primary">
            Pricing
          </span>
          <h2 className="mt-3 text-3xl font-bold tracking-tight md:text-4xl">
            Start free. Upgrade when you outgrow it.
          </h2>
          <p className="mt-3 text-muted-foreground">
            All plans include the editor, brand kit, and unlimited template
            previews.
          </p>
        </div>

        <div className="mx-auto mt-14 grid max-w-5xl grid-cols-1 gap-5 md:grid-cols-3">
          {PLANS.map((plan) => (
            <div
              key={plan.tier}
              className={cn(
                "relative flex flex-col rounded-2xl border bg-card p-6 transition-all",
                plan.featured
                  ? "border-primary/40 shadow-xl ring-1 ring-primary/15"
                  : "border-border/60 hover:-translate-y-0.5 hover:shadow-md",
              )}
            >
              {plan.featured && (
                <span className="absolute -top-3 left-1/2 inline-flex -translate-x-1/2 items-center gap-1 rounded-full bg-gradient-to-r from-primary to-emerald-500 px-3 py-1 text-[11px] font-semibold uppercase tracking-wider text-primary-foreground shadow-md">
                  <Sparkles className="h-3 w-3" />
                  Most popular
                </span>
              )}

              <h3 className="text-lg font-semibold tracking-tight">
                {plan.tier}
              </h3>
              <p className="mt-1 text-sm text-muted-foreground">
                {plan.description}
              </p>

              <div className="mt-5 flex items-baseline gap-1">
                <span className="text-4xl font-bold tracking-tight">
                  {plan.price}
                </span>
                <span className="text-sm text-muted-foreground">/month</span>
              </div>

              <ul className="mt-6 flex-1 space-y-2 text-sm">
                {plan.features.map((f) => (
                  <li key={f} className="flex items-start gap-2">
                    <Check className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                    <span>{f}</span>
                  </li>
                ))}
              </ul>

              <Button
                asChild
                className={cn(
                  "mt-6 w-full",
                  plan.featured &&
                    "bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground hover:opacity-90",
                )}
                variant={plan.featured ? "default" : "outline"}
              >
                <Link href={plan.href}>{plan.cta}</Link>
              </Button>
            </div>
          ))}
        </div>

        <p className="mt-10 text-center text-xs text-muted-foreground">
          Need a custom plan for a multi-branch group?{" "}
          <Link href="/pricing" className="font-medium text-primary hover:underline">
            See full pricing details →
          </Link>
        </p>
      </div>
    </section>
  );
}

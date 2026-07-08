"use client";

import { useEffect, useState } from "react";
import { ArrowLeft, ArrowRight, Check, Pill, Sparkles } from "lucide-react";
import type { SubscriptionTier } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { ThemeToggle } from "@/components/theme-toggle";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { readPendingSignup } from "@/lib/pending-signup";
import { useAuthStore } from "@/store/auth-store";
import { useSubscriptionStore } from "@/store/subscription-store";

interface FormState {
  pharmacyName: string;
  address: string;
  postCode: string;
  description: string;
  contactName: string;
  contactPhone: string;
  sonarFCode: string;
  tier: SubscriptionTier;
}

const STEPS = ["Pharmacy", "Contact", "Plan"] as const;

const PLAN_CHOICES: { tier: SubscriptionTier; name: string; price: string; highlights: string[]; featured?: boolean }[] = [
  {
    tier: "Free",
    name: "Free",
    price: "£0",
    highlights: ["5 posters", "Basic templates", "Watermark on exports"],
  },
  {
    tier: "Starter",
    name: "Starter",
    price: "£7",
    featured: true,
    highlights: ["30 posters", "All templates", "No watermark", "500 patients/month"],
  },
  {
    tier: "Pro",
    name: "Pro",
    price: "£25",
    highlights: ["Unlimited posters", "Priority AI", "5,000 patients/month", "Team collaboration"],
  },
];

export default function OnboardingPage() {
  const user = useAuthStore((s) => s.user);
  const setUser = useAuthStore((s) => s.setUser);
  const setSubscription = useSubscriptionStore((s) => s.setCurrent);

  const [step, setStep] = useState(0);
  const [form, setForm] = useState<FormState>({
    pharmacyName: "",
    address: "",
    postCode: "",
    description: "",
    contactName: user?.displayName ?? "",
    contactPhone: "",
    sonarFCode: "",
    tier: "Free",
  });
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const me = await api.getMe();
        if (cancelled) return;
        // Strict check: only stay on onboarding when the API explicitly returns false.
        if (me.profile.onboardingCompleted !== false) {
          window.location.assign("/dashboard");
          return;
        }
        // Seed from anything the user already entered on /register.
        const pending = readPendingSignup();
        setForm((f) => ({
          ...f,
          pharmacyName: me.profile.pharmacyName || pending?.pharmacyName || f.pharmacyName,
          address: me.profile.address || f.address,
          postCode: me.profile.postCode || pending?.postCode || f.postCode,
          description: me.profile.description || f.description,
          contactName:
            me.profile.contactName
            || pending?.contactName
            || me.displayName
            || f.contactName,
          contactPhone: me.profile.contactPhone || pending?.phone || f.contactPhone,
          sonarFCode: me.profile.sonarFCode || pending?.sonarFCode || f.sonarFCode,
          tier: me.tier,
        }));
      } catch {
        // Not authenticated — middleware will bounce them to /login
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  function next() {
    setError(null);
    if (step === 0) {
      if (!form.pharmacyName.trim()) return setError("Pharmacy name is required.");
      if (!form.address.trim()) return setError("Postal address is required.");
      if (!form.postCode.trim()) return setError("Postcode is required.");
      if (!form.sonarFCode.trim()) return setError("ODS / F code is required.");
    }
    if (step === 1) {
      if (!form.contactName.trim()) return setError("Contact name is required.");
      if (!form.contactPhone.trim()) return setError("Contact phone is required.");
    }
    setStep((s) => Math.min(STEPS.length - 1, s + 1));
  }

  function back() {
    setError(null);
    setStep((s) => Math.max(0, s - 1));
  }

  async function submit() {
    setPending(true);
    setError(null);
    try {
      const me = await api.onboard({
        pharmacyName: form.pharmacyName.trim(),
        address: form.address.trim(),
        postCode: form.postCode.trim().toUpperCase(),
        description: form.description.trim(),
        contactName: form.contactName.trim(),
        contactPhone: form.contactPhone.trim(),
        sonarFCode: form.sonarFCode.trim().toUpperCase() || null,
        tier: form.tier,
      });
      setUser({
        id: me.id,
        email: me.email,
        displayName: me.displayName,
        tier: me.tier,
        onboardingCompleted: true,
        createdAtUtc: me.createdAtUtc,
      });
      const sub = await api.getCurrentSubscription();
      setSubscription(sub);
      // Hard navigation so the new onboarded cookie + RSC cache are fresh.
      window.location.assign("/dashboard");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Onboarding failed");
      setPending(false);
    }
  }

  return (
    <div className="relative min-h-screen bg-gradient-to-br from-background via-background to-primary/[0.04]">
      <BackgroundGlow />

      <div className="absolute right-4 top-4 md:right-6 md:top-6">
        <ThemeToggle />
      </div>

      <main className="container relative mx-auto flex min-h-screen max-w-3xl flex-col px-4 py-12">
        <div className="flex flex-col items-center text-center">
          <span className="flex h-12 w-12 items-center justify-center rounded-2xl bg-gradient-to-br from-primary to-emerald-500 text-primary-foreground shadow-lg">
            <Pill className="h-6 w-6" />
          </span>
          <h1 className="mt-4 text-2xl font-bold tracking-tight">Set up your pharmacy</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Three short steps so we can personalize posters and campaigns for you.
          </p>
        </div>

        <Stepper current={step} />

        <div className="mt-8 rounded-2xl border border-border/60 bg-card p-6 shadow-sm sm:p-8">
          {step === 0 && <PharmacyStep form={form} setForm={setForm} />}
          {step === 1 && <ContactStep form={form} setForm={setForm} />}
          {step === 2 && <PlanStep form={form} setForm={setForm} />}

          {error && (
            <p className="mt-4 text-sm text-destructive" role="alert">
              {error}
            </p>
          )}

          <div className="mt-8 flex items-center justify-between">
            {step > 0 ? (
              <Button variant="ghost" onClick={back} disabled={pending}>
                <ArrowLeft className="mr-1.5 h-4 w-4" />
                Back
              </Button>
            ) : (
              <span />
            )}

            {step < STEPS.length - 1 ? (
              <Button onClick={next} className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground">
                Next
                <ArrowRight className="ml-1.5 h-4 w-4" />
              </Button>
            ) : (
              <Button
                onClick={submit}
                disabled={pending}
                className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground"
              >
                {pending ? "Finishing…" : "Finish setup"}
                <Check className="ml-1.5 h-4 w-4" />
              </Button>
            )}
          </div>
        </div>
      </main>
    </div>
  );
}

function Stepper({ current }: { current: number }) {
  return (
    <ol className="mx-auto mt-8 flex w-full max-w-md items-center justify-between gap-3">
      {STEPS.map((label, idx) => {
        const done = idx < current;
        const active = idx === current;
        return (
          <li key={label} className="flex flex-1 items-center gap-2">
            <span
              className={cn(
                "flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-semibold transition-colors",
                done
                  ? "bg-emerald-500 text-white"
                  : active
                  ? "bg-primary text-primary-foreground"
                  : "bg-muted text-muted-foreground",
              )}
            >
              {done ? <Check className="h-3.5 w-3.5" /> : idx + 1}
            </span>
            <span
              className={cn(
                "text-xs font-medium",
                active ? "text-foreground" : "text-muted-foreground",
              )}
            >
              {label}
            </span>
            {idx < STEPS.length - 1 && (
              <span className="h-px flex-1 bg-border" />
            )}
          </li>
        );
      })}
    </ol>
  );
}

function PharmacyStep({
  form,
  setForm,
}: {
  form: FormState;
  setForm: React.Dispatch<React.SetStateAction<FormState>>;
}) {
  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold">Tell us about your pharmacy</h2>
        <p className="text-sm text-muted-foreground">
          This goes onto every poster you create.
        </p>
      </div>

      <div className="space-y-2">
        <Label htmlFor="pharmacyName">Pharmacy name</Label>
        <Input
          id="pharmacyName"
          value={form.pharmacyName}
          onChange={(e) => setForm({ ...form, pharmacyName: e.target.value })}
          placeholder="Acme Community Pharmacy"
          required
        />
      </div>

      <div className="space-y-2">
        <Label htmlFor="address">Postal address</Label>
        <Textarea
          id="address"
          value={form.address}
          onChange={(e) => setForm({ ...form, address: e.target.value })}
          placeholder="123 Main Street, Anytown"
          rows={3}
          required
        />
      </div>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div className="space-y-2">
          <Label htmlFor="postCode">Postcode</Label>
          <Input
            id="postCode"
            value={form.postCode}
            onChange={(e) => setForm({ ...form, postCode: e.target.value.toUpperCase() })}
            placeholder="SW1A 1AA"
            autoCapitalize="characters"
            required
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="sonarFCode">ODS / F code</Label>
          <Input
            id="sonarFCode"
            value={form.sonarFCode}
            onChange={(e) => setForm({ ...form, sonarFCode: e.target.value.toUpperCase() })}
            placeholder="FA123"
            autoCapitalize="characters"
            required
          />
        </div>
      </div>

      <div className="space-y-2">
        <Label htmlFor="description">
          Description <span className="text-muted-foreground">(optional)</span>
        </Label>
        <Textarea
          id="description"
          value={form.description}
          onChange={(e) => setForm({ ...form, description: e.target.value })}
          placeholder="Independent pharmacy serving the local community since 1987."
          rows={3}
        />
      </div>
    </div>
  );
}

function ContactStep({
  form,
  setForm,
}: {
  form: FormState;
  setForm: React.Dispatch<React.SetStateAction<FormState>>;
}) {
  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold">Who's the contact person?</h2>
        <p className="text-sm text-muted-foreground">
          We'll use this for support and as the default contact on shared posters.
        </p>
      </div>

      <div className="space-y-2">
        <Label htmlFor="contactName">Contact name</Label>
        <Input
          id="contactName"
          value={form.contactName}
          onChange={(e) => setForm({ ...form, contactName: e.target.value })}
          placeholder="Jane Patel"
          required
        />
      </div>

      <div className="space-y-2">
        <Label htmlFor="contactPhone">Mobile number</Label>
        <Input
          id="contactPhone"
          type="tel"
          value={form.contactPhone}
          onChange={(e) => setForm({ ...form, contactPhone: e.target.value })}
          placeholder="07700 900000"
          required
        />
      </div>
    </div>
  );
}

function PlanStep({
  form,
  setForm,
}: {
  form: FormState;
  setForm: React.Dispatch<React.SetStateAction<FormState>>;
}) {
  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-lg font-semibold">Pick a plan</h2>
        <p className="text-sm text-muted-foreground">
          Stay free or pick a paid plan now. You can change it any time.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        {PLAN_CHOICES.map((plan) => {
          const selected = form.tier === plan.tier;
          return (
            <button
              key={plan.tier}
              type="button"
              onClick={() => setForm({ ...form, tier: plan.tier })}
              className={cn(
                "relative rounded-xl border bg-card p-4 text-left transition-all",
                selected
                  ? "border-primary ring-1 ring-primary/40"
                  : "border-border/60 hover:border-border",
              )}
            >
              {plan.featured && (
                <span className="absolute -top-2 right-3 inline-flex items-center gap-1 rounded-full bg-gradient-to-r from-primary to-emerald-500 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wider text-primary-foreground">
                  <Sparkles className="h-2.5 w-2.5" />
                  Popular
                </span>
              )}
              <p className="text-sm font-semibold">{plan.name}</p>
              <p className="mt-1 text-2xl font-bold tracking-tight">{plan.price}</p>
              <p className="text-xs text-muted-foreground">/ month</p>
              <ul className="mt-3 space-y-1.5">
                {plan.highlights.map((h) => (
                  <li key={h} className="flex items-start gap-1.5 text-xs text-muted-foreground">
                    <Check className="mt-0.5 h-3 w-3 shrink-0 text-primary" />
                    {h}
                  </li>
                ))}
              </ul>
            </button>
          );
        })}
      </div>
    </div>
  );
}

function BackgroundGlow() {
  return (
    <>
      <div className="pointer-events-none absolute -top-40 left-1/4 -z-10 h-[420px] w-[420px] rounded-full bg-primary/10 blur-[120px] dark:bg-primary/5" />
      <div className="pointer-events-none absolute -bottom-40 right-1/4 -z-10 h-[420px] w-[420px] rounded-full bg-emerald-400/10 blur-[120px] dark:bg-emerald-400/5" />
    </>
  );
}

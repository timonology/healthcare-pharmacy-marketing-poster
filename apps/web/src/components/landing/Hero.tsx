import Link from "next/link";
import { ArrowRight, Clapperboard, FileImage, Mail, MonitorPlay, Share2, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { HousePosterPreview } from "./HousePosterPreview";

/** The five things a pharmacy makes here — each one a section of the app. */
const MAKES = [
  { icon: FileImage, label: "Posters" },
  { icon: Clapperboard, label: "Videos" },
  { icon: Mail, label: "Email campaigns" },
  { icon: Share2, label: "Social posts" },
  { icon: MonitorPlay, label: "In-store screens" },
];

export function Hero() {
  return (
    <section className="relative overflow-hidden">
      <BackgroundGlow />

      <div className="container relative mx-auto px-4 py-16 md:px-6 md:py-24 lg:py-28">
        <div className="grid items-center gap-12 lg:grid-cols-2 lg:gap-16">
          <div>
            <span className="inline-flex items-center gap-1.5 rounded-full border border-primary/20 bg-primary/5 px-3 py-1 text-xs font-medium text-primary">
              <Sparkles className="h-3 w-3" />
              Made for UK community pharmacies
            </span>
            <h1 className="mt-5 text-4xl font-bold leading-[1.05] tracking-tight md:text-5xl lg:text-6xl">
              Professional pharmacy{" "}
              <span className="bg-gradient-to-r from-primary via-cyan-500 to-teal-500 bg-clip-text text-transparent">
                marketing
              </span>
            </h1>
            <p className="mt-5 max-w-xl text-lg text-muted-foreground">
              Posters, short videos, patient email campaigns, social posts and
              in-pharmacy screens, all from one brand kit. Start from a
              print-ready template or let AI design it, with your logo, contact
              details and booking QR code added for you. No design skills needed.
            </p>

            <ul className="mt-6 flex flex-wrap gap-2" aria-label="What you can make">
              {MAKES.map(({ icon: Icon, label }) => (
                <li
                  key={label}
                  className="inline-flex items-center gap-1.5 rounded-full border border-border/60 bg-background px-3 py-1 text-xs font-medium text-muted-foreground"
                >
                  <Icon className="h-3.5 w-3.5 text-primary" aria-hidden />
                  {label}
                </li>
              ))}
            </ul>

            <div className="mt-8 flex flex-wrap gap-3">
              <Button
                asChild
                size="lg"
                className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground shadow-md hover:opacity-90"
              >
                <Link href="/register">
                  Start free
                  <ArrowRight className="ml-1.5 h-4 w-4" />
                </Link>
              </Button>
              <Button asChild size="lg" variant="outline">
                <Link href="/templates">Browse templates</Link>
              </Button>
            </div>

            <div className="mt-8 flex items-center gap-5 text-xs text-muted-foreground">
              <div className="flex items-center gap-1.5">
                <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
                Free plan
              </div>
              <div className="flex items-center gap-1.5">
                <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
                No credit card
              </div>
              <div className="flex items-center gap-1.5">
                <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
                Cancel anytime
              </div>
            </div>
          </div>

          <div className="lg:pl-6">
            <HousePosterPreview />
          </div>
        </div>
      </div>
    </section>
  );
}

function BackgroundGlow() {
  return (
    <>
      <div
        aria-hidden
        className="pointer-events-none absolute inset-x-0 top-0 -z-10 h-[600px] bg-gradient-to-b from-primary/[0.04] via-cyan-500/[0.03] to-transparent"
      />
      <div
        aria-hidden
        className="pointer-events-none absolute -top-32 right-1/4 -z-10 h-[420px] w-[420px] rounded-full bg-cyan-400/10 blur-[120px] dark:bg-cyan-400/5"
      />
      <div
        aria-hidden
        className="pointer-events-none absolute -top-32 left-1/4 -z-10 h-[420px] w-[420px] rounded-full bg-primary/10 blur-[120px] dark:bg-primary/5"
      />
    </>
  );
}

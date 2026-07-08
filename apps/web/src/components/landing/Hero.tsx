import Link from "next/link";
import { ArrowRight, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { PosterMockup } from "./PosterMockup";

export function Hero() {
  return (
    <section className="relative overflow-hidden">
      <BackgroundGlow />

      <div className="container relative mx-auto px-4 py-16 md:px-6 md:py-24 lg:py-28">
        <div className="grid items-center gap-12 lg:grid-cols-2 lg:gap-16">
          <div>
            <span className="inline-flex items-center gap-1.5 rounded-full border border-primary/20 bg-primary/5 px-3 py-1 text-xs font-medium text-primary">
              <Sparkles className="h-3 w-3" />
              AI-powered for pharmacies
            </span>
            <h1 className="mt-5 text-4xl font-bold leading-[1.05] tracking-tight md:text-5xl lg:text-6xl">
              Create professional pharmacy{" "}
              <span className="bg-gradient-to-r from-primary via-cyan-500 to-emerald-500 bg-clip-text text-transparent">
                marketing posters
              </span>{" "}
              in minutes
            </h1>
            <p className="mt-5 max-w-xl text-lg text-muted-foreground">
              AI-powered drag &amp; drop designer made for pharmacies. Print-ready
              templates, your brand kit auto-applied. No design skills needed.
            </p>

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
                Free forever plan
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
            <PosterMockup />
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

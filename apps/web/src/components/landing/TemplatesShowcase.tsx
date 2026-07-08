import Link from "next/link";
import { ArrowRight } from "lucide-react";
import { Button } from "@/components/ui/button";

const TEMPLATES = [
  {
    title: "Flu Vaccination",
    category: "Vaccination",
    bg: "from-cyan-50 to-cyan-100",
    accent: "bg-cyan-700",
    accentText: "text-cyan-900",
    sub: "text-cyan-800/70",
    headline: "Flu shots\navailable now",
  },
  {
    title: "Vitamin D Awareness",
    category: "Awareness",
    bg: "from-amber-50 to-amber-100",
    accent: "bg-amber-600",
    accentText: "text-amber-900",
    sub: "text-amber-800/70",
    headline: "Are you Vitamin D deficient?",
  },
  {
    title: "Loyalty Rewards",
    category: "Promotion",
    bg: "from-indigo-950 to-indigo-900",
    accent: "bg-indigo-500",
    accentText: "text-white",
    sub: "text-indigo-200",
    headline: "Earn points.\nSave more.",
    dark: true,
  },
  {
    title: "Hand Hygiene",
    category: "Safety",
    bg: "from-violet-50 to-violet-100",
    accent: "bg-violet-700",
    accentText: "text-violet-900",
    sub: "text-violet-800/70",
    headline: "Wash your hands",
  },
  {
    title: "Allergy Season",
    category: "Seasonal",
    bg: "from-rose-50 to-pink-100",
    accent: "bg-rose-700",
    accentText: "text-rose-900",
    sub: "text-rose-800/70",
    headline: "Spring allergy survival kit",
  },
  {
    title: "Welcome Patients",
    category: "General",
    bg: "from-slate-50 to-slate-100",
    accent: "bg-slate-900",
    accentText: "text-slate-900",
    sub: "text-slate-700",
    headline: "We're glad you're here",
  },
];

export function TemplatesShowcase() {
  return (
    <section id="templates" className="py-20 md:py-28">
      <div className="container mx-auto px-4 md:px-6">
        <div className="flex flex-col items-start justify-between gap-4 md:flex-row md:items-end">
          <div className="max-w-2xl">
            <span className="text-xs font-semibold uppercase tracking-widest text-primary">
              Templates
            </span>
            <h2 className="mt-3 text-3xl font-bold tracking-tight md:text-4xl">
              Pharmacy-specific designs, ready to brand
            </h2>
            <p className="mt-3 text-muted-foreground">
              A growing library of vaccination, awareness, promotion, and seasonal
              posters. All editable, all print-ready.
            </p>
          </div>
          <Button asChild variant="ghost" size="sm" className="text-primary">
            <Link href="/templates">
              Browse all templates
              <ArrowRight className="ml-1 h-4 w-4" />
            </Link>
          </Button>
        </div>

        <div className="mt-12 grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-6">
          {TEMPLATES.map((t) => (
            <Link
              key={t.title}
              href="/templates"
              className="group overflow-hidden rounded-xl border border-border/60 bg-card transition-all hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-lg focus:outline-none focus-visible:ring-2 focus-visible:ring-primary"
            >
              <div
                className={`aspect-[3/4] bg-gradient-to-br ${t.bg} p-3`}
              >
                <div className={`h-1 w-full ${t.accent}`} />
                <div className="mt-2 space-y-1.5">
                  <p className={`text-[7px] font-semibold uppercase tracking-widest ${t.sub}`}>
                    {t.category}
                  </p>
                  <h3
                    className={`whitespace-pre-line text-[12px] font-bold leading-tight ${t.accentText}`}
                  >
                    {t.headline}
                  </h3>
                </div>
              </div>
              <div className="p-3">
                <p className="truncate text-xs font-medium">{t.title}</p>
                <p className="mt-0.5 text-[10px] text-muted-foreground">{t.category}</p>
              </div>
            </Link>
          ))}
        </div>
      </div>
    </section>
  );
}

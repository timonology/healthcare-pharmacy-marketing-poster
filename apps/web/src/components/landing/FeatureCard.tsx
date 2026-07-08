import type { LucideIcon } from "lucide-react";
import { cn } from "@/lib/utils";

interface FeatureCardProps {
  icon: LucideIcon;
  title: string;
  description: string;
  accent: "blue" | "cyan" | "emerald" | "violet" | "amber" | "rose";
}

const ACCENTS: Record<FeatureCardProps["accent"], string> = {
  blue: "from-primary/15 to-primary/5 text-primary",
  cyan: "from-cyan-500/15 to-emerald-500/5 text-cyan-600 dark:text-cyan-400",
  emerald: "from-emerald-500/15 to-emerald-500/5 text-emerald-600 dark:text-emerald-400",
  violet: "from-violet-500/15 to-violet-500/5 text-violet-600 dark:text-violet-400",
  amber: "from-amber-500/15 to-amber-500/5 text-amber-600 dark:text-amber-400",
  rose: "from-rose-500/15 to-rose-500/5 text-rose-600 dark:text-rose-400",
};

export function FeatureCard({ icon: Icon, title, description, accent }: FeatureCardProps) {
  return (
    <div className="group relative overflow-hidden rounded-2xl border border-border/60 bg-card p-6 transition-all hover:-translate-y-0.5 hover:shadow-lg">
      <div
        className={cn(
          "flex h-11 w-11 items-center justify-center rounded-xl bg-gradient-to-br ring-1 ring-inset ring-border/40",
          ACCENTS[accent],
        )}
      >
        <Icon className="h-5 w-5" />
      </div>
      <h3 className="mt-5 text-base font-semibold tracking-tight">{title}</h3>
      <p className="mt-2 text-sm text-muted-foreground">{description}</p>
    </div>
  );
}

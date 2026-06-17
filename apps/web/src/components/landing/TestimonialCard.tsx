import { Star } from "lucide-react";
import { cn } from "@/lib/utils";

interface TestimonialCardProps {
  quote: string;
  name: string;
  role: string;
  initials: string;
  accent?: "blue" | "cyan" | "emerald";
}

const ACCENT_BG: Record<NonNullable<TestimonialCardProps["accent"]>, string> = {
  blue: "from-primary to-blue-600 text-primary-foreground",
  cyan: "from-cyan-500 to-cyan-700 text-white",
  emerald: "from-emerald-500 to-teal-600 text-white",
};

export function TestimonialCard({
  quote,
  name,
  role,
  initials,
  accent = "blue",
}: TestimonialCardProps) {
  return (
    <figure className="flex h-full flex-col rounded-2xl border border-border/60 bg-card p-6 shadow-sm">
      <div className="flex gap-0.5 text-amber-400">
        {Array.from({ length: 5 }).map((_, i) => (
          <Star key={i} className="h-3.5 w-3.5 fill-current" />
        ))}
      </div>
      <blockquote className="mt-4 flex-1 text-sm leading-relaxed text-foreground">
        &ldquo;{quote}&rdquo;
      </blockquote>
      <figcaption className="mt-6 flex items-center gap-3">
        <span
          className={cn(
            "flex h-10 w-10 items-center justify-center rounded-full bg-gradient-to-br text-sm font-semibold",
            ACCENT_BG[accent],
          )}
        >
          {initials}
        </span>
        <div>
          <p className="text-sm font-medium">{name}</p>
          <p className="text-xs text-muted-foreground">{role}</p>
        </div>
      </figcaption>
    </figure>
  );
}

import {
  MousePointerClick,
  Sparkles,
  Palette,
  Printer,
  Mail,
  Users,
} from "lucide-react";
import { FeatureCard } from "./FeatureCard";

const FEATURES = [
  {
    icon: MousePointerClick,
    title: "Drag & drop designer",
    description:
      "A canvas built for non-designers. Click, drag, resize, rotate. Snap to grid, undo as far back as you need.",
    accent: "blue" as const,
  },
  {
    icon: Sparkles,
    title: "AI content generator",
    description:
      "Describe the campaign in a sentence; get headline, sub-copy, and call-to-action that follows pharmacy ad guidelines.",
    accent: "violet" as const,
  },
  {
    icon: Palette,
    title: "Brand kit, auto-applied",
    description:
      "Logo, colors, license number, regulatory footer. Stamped on every poster automatically.",
    accent: "cyan" as const,
  },
  {
    icon: Printer,
    title: "Print-ready export",
    description:
      "300 DPI PDF with crop marks and bleed. Send straight to your printer or in-store kiosk.",
    accent: "emerald" as const,
  },
  {
    icon: Mail,
    title: "Email marketing",
    description:
      "One-click resize for newsletter banners, social posts, and your patient mailing list.",
    accent: "amber" as const,
  },
  {
    icon: Users,
    title: "Team collaboration",
    description:
      "Share posters with your team, leave comments, and manage roles. Pro plan includes branch-level permissions.",
    accent: "rose" as const,
  },
];

export function Features() {
  return (
    <section id="features" className="relative py-20 md:py-28">
      <div className="container mx-auto px-4 md:px-6">
        <div className="mx-auto max-w-2xl text-center">
          <span className="text-xs font-semibold uppercase tracking-widest text-primary">
            Features
          </span>
          <h2 className="mt-3 text-3xl font-bold tracking-tight md:text-4xl">
            Everything your pharmacy needs to look its best
          </h2>
          <p className="mt-4 text-muted-foreground">
            Six pieces that turn a blank canvas into a poster your patients trust.
          </p>
        </div>

        <div className="mt-14 grid grid-cols-1 gap-5 md:grid-cols-2 lg:grid-cols-3">
          {FEATURES.map((feature) => (
            <FeatureCard key={feature.title} {...feature} />
          ))}
        </div>
      </div>
    </section>
  );
}

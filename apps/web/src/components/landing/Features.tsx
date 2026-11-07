import {
  Clapperboard,
  LayoutTemplate,
  Mail,
  MonitorPlay,
  Palette,
  Printer,
  Share2,
  Sparkles,
  Users,
} from "lucide-react";
import { FeatureCard } from "./FeatureCard";

/**
 * What the product does today — nothing more. Each card is a section of the
 * app a pharmacist can open after signing up; claims about print resolutions,
 * compliance or collaboration features we do not have were removed rather than
 * softened. Where a feature is not on every plan, the card says which.
 */
const FEATURES = [
  {
    icon: LayoutTemplate,
    title: "Pharmacy poster templates",
    description:
      "Vaccinations, Pharmacy First, health checks, seasonal advice and more, in one consistent clinical style. Pick one and make it yours.",
    accent: "blue" as const,
  },
  {
    icon: Sparkles,
    title: "Design with AI",
    description:
      "Describe what you need — \"walk-in flu clinic\" — and get an editable A4 poster in the same style, with a stock photo, your own photo or none.",
    accent: "violet" as const,
  },
  {
    icon: Clapperboard,
    title: "Marketing videos",
    description:
      "Turn a topic into a 15-second vertical video for Reels, TikTok or YouTube Shorts, from an AI script, your direction or your own words. Starter and Pro.",
    accent: "rose" as const,
  },
  {
    icon: Mail,
    title: "Patient email campaigns",
    description:
      "Send a poster, with videos if you like, to patients who have agreed to hear from you. Every email carries a one-click unsubscribe. Starter and Pro.",
    accent: "amber" as const,
  },
  {
    icon: Share2,
    title: "Social posts",
    description:
      "Schedule posters and videos to Facebook, Instagram, LinkedIn and X, and videos to YouTube and TikTok — once, or on a repeating schedule.",
    accent: "cyan" as const,
  },
  {
    icon: MonitorPlay,
    title: "In-pharmacy screens",
    description:
      "Play a loop of your published posters and videos on a TV or tablet in the pharmacy, and change it from anywhere. Starter and Pro.",
    accent: "emerald" as const,
  },
  {
    icon: Palette,
    title: "Your brand kit, applied",
    description:
      "Add your logo, pharmacy details and booking link once. They appear on every poster, with a QR code that opens your booking page.",
    accent: "cyan" as const,
  },
  {
    icon: Printer,
    title: "Print-ready",
    description:
      "Download an A4 PDF or PNG for the shop window, the counter or the noticeboard. Edit text, photos and colours in the drag-and-drop editor first.",
    accent: "blue" as const,
  },
  {
    icon: Users,
    title: "Patient contacts, with consent",
    description:
      "Import your patient list, group it, and keep consent and unsubscribes in order, so every campaign goes only to people who said yes.",
    accent: "violet" as const,
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
            Everything your pharmacy needs to market itself
          </h2>
          <p className="mt-4 text-muted-foreground">
            Posters, videos, email, social and screens, from one brand kit and without a designer.
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

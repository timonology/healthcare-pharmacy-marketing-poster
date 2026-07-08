import type { Metadata } from "next";
import { Features } from "@/components/landing/Features";
import { FinalCta } from "@/components/landing/FinalCta";
import { Hero } from "@/components/landing/Hero";
import { HowItWorks } from "@/components/landing/HowItWorks";
import { LandingNav } from "@/components/landing/LandingNav";
import { PricingTeaser } from "@/components/landing/PricingTeaser";
import { SiteFooter } from "@/components/landing/SiteFooter";
import { TemplatesShowcase } from "@/components/landing/TemplatesShowcase";
// Testimonials hidden for now — re-enable in app/page.tsx when copy is finalised.
// import { Testimonials } from "@/components/landing/Testimonials";
import { TrustBar } from "@/components/landing/TrustBar";

export const metadata: Metadata = {
  title: "Sonar Marketing25 — Create marketing posters in minutes",
  description:
    "AI-powered drag & drop poster designer made for pharmacies. Print-ready templates, your brand kit auto-applied. Free forever plan.",
  openGraph: {
    title: "Sonar Marketing25 — Create marketing posters in minutes",
    description:
      "AI-powered drag & drop poster designer made for pharmacies. Print-ready templates, your brand kit auto-applied. Free forever plan.",
    type: "website",
  },
  twitter: {
    card: "summary_large_image",
    title: "Sonar Marketing25",
    description:
      "AI-powered drag & drop poster designer made for pharmacies.",
  },
};

export default function HomePage() {
  return (
    <>
      <LandingNav />
      <main>
        <Hero />
        <TrustBar />
        <Features />
        <HowItWorks />
        <TemplatesShowcase />
        <PricingTeaser />
        <FinalCta />
      </main>
      <SiteFooter />
    </>
  );
}

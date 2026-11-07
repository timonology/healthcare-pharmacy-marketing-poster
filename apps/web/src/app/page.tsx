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
import { HomeStructuredData } from "@/components/landing/HomeStructuredData";
import { SITE_DESCRIPTION, SITE_NAME, SITE_TAGLINE, pageMetadata } from "@/lib/site";

export const metadata: Metadata = {
  // Absolute: the brand comes first here, not after the page name.
  title: { absolute: `${SITE_NAME} — ${SITE_TAGLINE} for UK community pharmacies` },
  ...pageMetadata({ title: `${SITE_NAME} — ${SITE_TAGLINE}`, description: SITE_DESCRIPTION, path: "/" }),
};

export default function HomePage() {
  return (
    <>
      <HomeStructuredData />
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

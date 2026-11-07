import type { Metadata } from "next";
import { PLAN_PRICES_GBP } from "@acme/shared-types";
import { pageMetadata } from "@/lib/site";

export const metadata: Metadata = {
  title: "Pricing",
  ...pageMetadata({
    title: "Pricing",
    description:
      `Free, Starter (£${PLAN_PRICES_GBP.Starter}/month) and Pro (£${PLAN_PRICES_GBP.Pro}/month) plans for pharmacy marketing: posters, AI poster designs, marketing videos, email campaigns and in-pharmacy screens.`,
    path: "/pricing",
  }),
};

export default function PricingLayout({ children }: { children: React.ReactNode }) {
  return children;
}

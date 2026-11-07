import type { Metadata } from "next";
import { pageMetadata } from "@/lib/site";

export const metadata: Metadata = {
  title: "Pharmacy poster templates",
  ...pageMetadata({
    title: "Pharmacy poster templates",
    description:
      "Print-ready A4 poster templates for UK community pharmacies: vaccinations, Pharmacy First, health checks, seasonal advice and more. Your logo, contact details and booking QR code added for you.",
    path: "/templates",
  }),
};

export default function TemplatesLayout({ children }: { children: React.ReactNode }) {
  return children;
}

import type { Metadata } from "next";
import { SiteHeader } from "@/components/site/SiteHeader";
import "./globals.css";

export const metadata: Metadata = {
  title: "Pharmacy Healthcare Poster",
  description: "Design and share pharmacy marketing posters.",
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en">
      <body className="min-h-screen bg-background antialiased">
        <SiteHeader />
        {children}
      </body>
    </html>
  );
}

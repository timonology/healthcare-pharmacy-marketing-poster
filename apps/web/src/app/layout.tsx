import type { Metadata } from "next";
import { cookies } from "next/headers";
import { SiteHeader } from "@/components/site/SiteHeader";
import { ThemeProvider } from "@/components/theme-provider";
import { ToastProvider } from "@/components/ui/toast";
import "./globals.css";

export const metadata: Metadata = {
  title: "Sonar Marketing25",
  description: "Design and share pharmacy marketing posters.",
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  // Read the auth cookie server-side so the initial SSR HTML already renders
  // the correct header variant (avoids the "marketing header flash" on refresh).
  // acme_access is httpOnly but present alongside acme_onboarded (public), so
  // either presence is a strong hint the user is signed in.
  const cookieStore = cookies();
  const initialSignedIn =
    cookieStore.get("acme_access") !== undefined
    || cookieStore.get("acme_onboarded") !== undefined;

  return (
    <html lang="en" suppressHydrationWarning>
      <body className="min-h-screen bg-background antialiased">
        <ThemeProvider
          attribute="class"
          defaultTheme="system"
          enableSystem
          disableTransitionOnChange
        >
          <ToastProvider>
            <SiteHeader initialSignedIn={initialSignedIn} />
            {children}
          </ToastProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}

import { ManageAsBanner } from "@/components/site/ManageAsBanner";
import type { Metadata, Viewport } from "next";
import { cookies, headers } from "next/headers";
import { AppShell } from "@/components/site/AppShell";
import { ThemeProvider } from "@/components/theme-provider";
import { ToastProvider } from "@/components/ui/toast";
import { MANAGE_AS_COOKIE, MANAGE_AS_LABEL_COOKIE } from "@/lib/server/cookies";
import {
  SITE_DESCRIPTION,
  SITE_KEYWORDS,
  SITE_NAME,
  SITE_TAGLINE,
  pageMetadata,
  siteUrl,
} from "@/lib/site";
import { NONCE_HEADER } from "@/lib/csp";
import "./globals.css";

/**
 * Defaults for every page. Pages set their own title and description; the
 * template puts the brand after it ("Pricing · Marketing25").
 *
 * A function rather than a constant so `metadataBase` comes from the running
 * container's SITE_URL (see lib/site.ts): canonical links and social preview
 * images must be absolute, and must name the real host.
 */
export function generateMetadata(): Metadata {
  return {
    metadataBase: new URL(siteUrl()),
    title: {
      default: `${SITE_NAME} — ${SITE_TAGLINE}`,
      template: `%s · ${SITE_NAME}`,
    },
    applicationName: SITE_NAME,
    keywords: SITE_KEYWORDS,
    // Public pages are indexable; robots.txt keeps crawlers out of the
    // signed-in app, and its pages carry no links a crawler could follow in.
    robots: { index: true, follow: true },
    // Defaults for pages that set none (privacy, terms, the app). No path:
    // each public page states its own canonical address; none is inherited.
    ...pageMetadata({ title: `${SITE_NAME} — ${SITE_TAGLINE}`, description: SITE_DESCRIPTION }),
    // A phone number in poster copy is not a link to dial on iOS Safari.
    formatDetection: { telephone: false, address: false, email: false },
  };
}

export const viewport: Viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#ffffff" },
    { media: "(prefers-color-scheme: dark)", color: "#0b1220" },
  ],
};

export default async function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  // Read the auth cookie server-side so the initial SSR HTML already renders
  // the correct header variant (avoids the "marketing header flash" on refresh).
  // acme_access is httpOnly but present alongside acme_onboarded (public), so
  // either presence is a strong hint the user is signed in.
  const cookieStore = await cookies();
  // This request's CSP nonce (middleware). next-themes injects an inline
  // script to set the theme before paint; it carries the nonce so the CSP
  // allows it (security review SEC-09).
  const nonce = (await headers()).get(NONCE_HEADER) ?? undefined;
  const initialSignedIn =
    cookieStore.get("acme_access") !== undefined
    || cookieStore.get("acme_onboarded") !== undefined;

  // The manage-as banner is decided here rather than in the browser, because
  // only the server can see the httpOnly token that proves the delegated
  // session exists. The readable name cookie is a label; on its own it proved
  // nothing, and the banner was showing to signed-out people on the strength
  // of it. Both must be present: the token for "there is a session", the name
  // for "and this is whose".
  const managingAs =
    cookieStore.get(MANAGE_AS_COOKIE) !== undefined
      ? (cookieStore.get(MANAGE_AS_LABEL_COOKIE)?.value ?? null)
      : null;

  return (
    <html lang="en" suppressHydrationWarning>
      <body className="min-h-screen bg-background antialiased">
        <ManageAsBanner pharmacyName={managingAs} />
        <ThemeProvider
          attribute="class"
          defaultTheme="system"
          enableSystem
          disableTransitionOnChange
          nonce={nonce}
        >
          <ToastProvider>
            <AppShell initialSignedIn={initialSignedIn}>{children}</AppShell>
          </ToastProvider>
        </ThemeProvider>
      </body>
    </html>
  );
}

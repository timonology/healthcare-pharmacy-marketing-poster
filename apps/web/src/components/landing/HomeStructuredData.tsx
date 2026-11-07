import { PLAN_HIGHLIGHTS, PLAN_PRICES_GBP, TIERS } from "@acme/shared-types";
import { headers } from "next/headers";
import { NONCE_HEADER } from "@/lib/csp";
import { SITE_DESCRIPTION, SITE_NAME, siteUrl } from "@/lib/site";

/**
 * schema.org data for the home page: who we are and what the product costs.
 *
 * Search engines use it for the knowledge panel and can show the price range
 * in results, so it is built from the same plan prices and lines as the
 * pricing cards (PLAN_PRICES_GBP / PLAN_HIGHLIGHTS, checked against the API
 * by PlanCopyTests) — never typed out separately, where it would drift.
 * No ratings or review counts: we have none to publish.
 */
export async function HomeStructuredData() {
  const url = siteUrl();
  // Inline, so it carries this request's CSP nonce like every other script.
  const nonce = (await headers()).get(NONCE_HEADER) ?? undefined;
  const data = {
    "@context": "https://schema.org",
    "@graph": [
      {
        "@type": "Organization",
        "@id": `${url}/#organization`,
        name: SITE_NAME,
        url,
        email: "info@sonarinformatics.com",
      },
      {
        "@type": "WebSite",
        "@id": `${url}/#website`,
        name: SITE_NAME,
        url,
        inLanguage: "en-GB",
        publisher: { "@id": `${url}/#organization` },
      },
      {
        "@type": "SoftwareApplication",
        name: SITE_NAME,
        url,
        description: SITE_DESCRIPTION,
        applicationCategory: "BusinessApplication",
        applicationSubCategory: "Pharmacy marketing",
        operatingSystem: "Web",
        audience: { "@type": "BusinessAudience", name: "UK community pharmacies" },
        publisher: { "@id": `${url}/#organization` },
        offers: TIERS.map((tier) => ({
          "@type": "Offer",
          name: tier,
          price: PLAN_PRICES_GBP[tier].toFixed(2),
          priceCurrency: "GBP",
          description: PLAN_HIGHLIGHTS[tier].join("; "),
          url: `${url}/pricing`,
        })),
      },
    ],
  };

  return (
    <script
      type="application/ld+json"
      nonce={nonce}
      // Browsers hide a nonce from the DOM once the page loads (it reads back
      // as ""), so React sees the server's value and the DOM's disagree and
      // reports a hydration mismatch. The difference is the security feature
      // working; the value only matters to the browser at load.
      suppressHydrationWarning
      // JSON in a script element: escape "<" so no string can close the tag.
      dangerouslySetInnerHTML={{ __html: JSON.stringify(data).replace(/</g, "\\u003c") }}
    />
  );
}

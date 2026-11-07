import type { Metadata } from "next";
import { LegalPage, Section } from "../../legal/LegalPage";

export const metadata: Metadata = {
  title: "Data deletion",
  alternates: { canonical: "/privacy/data-deletion" },
  description: "How to have Marketing25 delete the Facebook and Instagram data it holds for you.",
};

/**
 * The status page Meta links to after a data-deletion request, and the
 * instructions page for anyone who wants one.
 *
 * Static on purpose. The API carries out a deletion request in full before it
 * answers Meta with a confirmation code, so any code that reaches this page
 * refers to a request that has already been completed — there is no pending
 * state to look up. A made-up code shows the same text, which reveals nothing.
 */
export default async function DataDeletionPage({
  searchParams,
}: {
  // A Promise from Next 15; awaiting a plain object is harmless on 14.
  searchParams?: Promise<{ code?: string | string[] }>;
}) {
  const query = await searchParams;
  // Shown back only if it looks like one of ours: 16 hex characters.
  const code =
    typeof query?.code === "string" && /^[0-9a-f]{16}$/.test(query.code)
      ? query.code
      : null;

  return (
    <LegalPage title="Data deletion" updated="3 November 2026">
      {code && (
        <div className="rounded-md border border-primary/30 bg-primary/5 p-4">
          <p className="font-medium">Your request has been completed.</p>
          <p className="mt-1 text-muted-foreground">
            Reference <span className="font-mono">{code}</span>. We have deleted the
            Facebook Pages and Instagram accounts you connected, and their access tokens.
          </p>
        </div>
      )}

      <Section heading="Facebook and Instagram">
        <p>
          You can remove Marketing25 at any time in Facebook under{" "}
          <em>Settings &amp; privacy → Settings → Business integrations</em>. When you do,
          Facebook tells us and we immediately delete the access tokens for every Page
          and Instagram account you connected.
        </p>
        <p>
          From the same place you can ask for your data to be deleted. We then delete
          those connected accounts outright — names, pictures, ids and tokens — and
          Facebook shows you a reference you can check here.
        </p>
        <p>
          Posts already published stay on your Page; delete them in Facebook or
          Instagram if you want them gone. Your pharmacy&rsquo;s own history in
          Marketing25 keeps a record of what was sent, without the account details.
        </p>
      </Section>

      <Section heading="YouTube, LinkedIn, TikTok and X">
        <p>
          Disconnect the account in Social &amp; Ads and we delete its access tokens
          immediately. To withdraw Marketing25&rsquo;s access at the platform too,
          remove it in Google at <em>myaccount.google.com/permissions</em>, in
          LinkedIn under <em>Settings → Data privacy → Permitted services</em>, or in
          TikTok under <em>Settings and privacy → Security → Manage app permissions</em>,
          or in X under <em>Settings and privacy → Security and account access → Apps and
          sessions → Connected apps</em>. Disconnecting a TikTok or X account also
          withdraws Marketing25&rsquo;s access there. To
          have the account records deleted entirely, email us as below.
        </p>
      </Section>

      <Section heading="Everything else">
        <p>
          To delete your Marketing25 account and everything in it, email{" "}
          <a href="mailto:info@sonarinformatics.com" className="text-primary hover:underline">
            info@sonarinformatics.com
          </a>{" "}
          from the address you signed up with.
        </p>
      </Section>
    </LegalPage>
  );
}

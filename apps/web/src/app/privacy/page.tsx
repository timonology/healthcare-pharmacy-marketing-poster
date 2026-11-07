import Link from "next/link";
import type { Metadata } from "next";
import { LegalPage, Section } from "../legal/LegalPage";

export const metadata: Metadata = {
  title: "Privacy policy",
  alternates: { canonical: "/privacy" },
  description: "What Marketing25 collects, why, and what you can ask us to do with it.",
};

export default function PrivacyPage() {
  return (
    <LegalPage title="Privacy policy" updated="3 November 2026">
      <p>
        Marketing25 is operated by Sonar Informatics. This page explains what we hold,
        why we hold it, and what you can ask us to do about it.
      </p>

      <Section heading="What we hold about you">
        <p>When you use Marketing25 as a pharmacy, we hold:</p>
        <ul className="ml-5 list-disc space-y-1">
          <li>your name, email address and password (stored only as a hash)</li>
          <li>your pharmacy&rsquo;s name, address, postcode, ODS/F code and phone number</li>
          <li>the posters, campaigns and videos you create</li>
          <li>billing details, held by our payment provider rather than by us</li>
          <li>sign-in times, so we can show you recent activity and investigate problems</li>
        </ul>
      </Section>

      <Section heading="The contacts you upload">
        <p>
          Marketing contacts you add or import — a name, and an email address or phone
          number — are held so that campaigns can be sent to them.
        </p>
        <p>
          <strong>These are marketing contacts, not clinical records.</strong>{" "}
          Marketing25 has no field for a diagnosis, a prescription or any other health
          information, and you must not put such information into a free-text field.
        </p>
        <p>
          For these contacts, your pharmacy is the data controller and Sonar
          Informatics is the processor: we act on your instructions, and you are
          responsible for having a lawful basis to contact them.
        </p>
      </Section>

      <Section heading="Social and advertising accounts you connect">
        <p>
          If you connect a Facebook Page, an Instagram business account, a YouTube
          channel, a LinkedIn profile or Page, a TikTok or X account, or a Google Ads
          account so Marketing25 can post or advertise for you, we hold:
        </p>
        <ul className="ml-5 list-disc space-y-1">
          <li>
            the access tokens those services issue, encrypted (AES-256-GCM) and used
            only to publish what you schedule or manage the campaigns you create
          </li>
          <li>
            each account&rsquo;s name, picture and id, so you can see which account a
            post went to
          </li>
          <li>
            the id of the Facebook, LinkedIn, TikTok or X user who granted access, so a
            withdrawal reaches us
          </li>
          <li>when a sign-in expires (LinkedIn&rsquo;s last 60 days), so we can remind you to renew it</li>
          <li>the ids and links of the posts we published for you</li>
        </ul>
        <p>
          We do not read your followers, messages or insights, and we use nothing from
          these accounts for advertising of our own.
        </p>
        <p>
          Disconnecting an account in Social &amp; Ads deletes its tokens immediately.
          You can also remove Marketing25 in Google (myaccount.google.com/permissions)
          or LinkedIn (Settings → Data privacy → Permitted services), or in TikTok
          (Settings and privacy → Security → Manage app permissions), or in X
          (Settings and privacy → Security and account access → Apps and sessions). Removing it in
          your Facebook settings deletes its tokens too, and a deletion
          request made through Facebook removes the account records entirely —{" "}
          <Link href="/privacy/data-deletion" className="text-primary hover:underline">
            how data deletion works
          </Link>
          .
        </p>
      </Section>

      <Section heading="Who else processes it">
        <p>We use a small number of providers to run the service:</p>
        <ul className="ml-5 list-disc space-y-1">
          <li>Microsoft Azure — hosting, database and file storage (UK/EU regions)</li>
          <li>Mailjet — sending campaign and account emails</li>
          <li>Stripe — subscription billing</li>
          <li>
            Meta, Google (including YouTube), LinkedIn, TikTok and X — only when you
            connect an account, to publish and advertise on your behalf
          </li>
        </ul>
        <p>
          We do not sell your data, and we do not share it for anyone else&rsquo;s
          advertising.
        </p>
      </Section>

      <Section heading="How long we keep it">
        <p>
          Account and content data is kept while your account is open. When an account
          is closed we delete it, and backups age out within 30 days.
        </p>
        <p>
          Contacts you delete are removed immediately. Records we must keep for
          accounting purposes are retained for as long as the law requires.
        </p>
      </Section>

      <Section heading="Your rights">
        <p>
          Under UK GDPR you can ask for a copy of your data, ask us to correct it, ask
          us to delete it, or object to how we use it. Email{" "}
          <a
            href="mailto:info@sonarinformatics.com"
            className="font-medium text-primary hover:underline"
          >
            info@sonarinformatics.com
          </a>{" "}
          and we will respond within one month.
        </p>
        <p>
          If a contact of yours contacts us directly, we will point them to your
          pharmacy, since you are the controller for their details.
        </p>
        <p>
          You can also complain to the Information Commissioner&rsquo;s Office at
          ico.org.uk.
        </p>
      </Section>

      <Section heading="Security">
        <p>
          Traffic is encrypted in transit. Passwords are hashed, never stored in a
          readable form. Access to production data is limited to staff who need it.
        </p>
        <p>
          If a breach affects your data we will tell you and the ICO within the time
          the law requires.
        </p>
      </Section>

      <Section heading="Cookies">
        <p>
          We set cookies to keep you signed in and to remember which pharmacy you are
          working as. We do not use advertising cookies.
        </p>
      </Section>
    </LegalPage>
  );
}

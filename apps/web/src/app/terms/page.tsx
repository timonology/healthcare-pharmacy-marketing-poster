import type { Metadata } from "next";
import { LegalPage, Section } from "../legal/LegalPage";

export const metadata: Metadata = {
  title: "Terms of service",
  alternates: { canonical: "/terms" },
  description: "The terms under which Marketing25 is provided.",
};

export default function TermsPage() {
  return (
    <LegalPage title="Terms of service" updated="27 August 2026">
      <p>
        Marketing25 is a marketing tool for pharmacies, operated by Sonar Informatics.
        These terms apply to everyone who uses it. By creating an account you agree
        to them.
      </p>

      <Section heading="What the service is">
        <p>
          Marketing25 lets a pharmacy design posters, send email campaigns to its own
          contacts, generate short videos, and publish to its own social accounts.
        </p>
        <p>
          It is a marketing product. It is <strong>not</strong> a clinical system, not a
          patient record, and must not be used to store diagnoses, prescriptions,
          treatment notes or any other clinical information.
        </p>
      </Section>

      <Section heading="Your account">
        <p>
          You are responsible for what happens under your account, including keeping
          your password to yourself. Tell us promptly if you think someone else has
          access to it.
        </p>
        <p>
          Accounts are for the pharmacy or organisation they were created for. If you
          leave that organisation, your access may be removed by its administrator.
        </p>
      </Section>

      <Section heading="The contacts you upload">
        <p>
          When you add or import contacts, you are telling us that you have a lawful
          basis to hold their details and to send them the marketing you intend to
          send. That responsibility stays with you — we process those contacts on
          your instructions.
        </p>
        <p>
          You must honour unsubscribe requests, and you must not upload lists you
          bought or scraped.
        </p>
      </Section>

      <Section heading="Content you create">
        <p>
          Posters, campaigns and videos you make remain yours. You give us permission
          to store and process them so we can provide the service.
        </p>
        <p>
          Templates and stock imagery supplied by Marketing25 are licensed for use
          within the product. You may not resell them as standalone assets.
        </p>
      </Section>

      <Section heading="Plans and payment">
        <p>
          Paid plans are billed in advance for the period shown at checkout. Limits —
          campaign recipients, video credits — apply per plan and are shown in the
          app. Changing plan takes effect immediately; a downgrade applies at the end
          of the current period.
        </p>
      </Section>

      <Section heading="Acceptable use">
        <p>
          Do not use Marketing25 to send anything unlawful, misleading or harassing,
          to impersonate another pharmacy, or to make claims about medicines that you
          could not defend to a regulator.
        </p>
        <p>
          We may suspend an account that is being used this way, and will tell you why.
        </p>
      </Section>

      <Section heading="Availability">
        <p>
          We work to keep the service running but do not guarantee uninterrupted
          availability. Planned maintenance is announced in advance where we can.
        </p>
      </Section>

      <Section heading="Ending your account">
        <p>
          You can stop using Marketing25 at any time. Ask us to delete your account and
          we will remove your data as described in the{" "}
          <a href="/privacy" className="font-medium text-primary hover:underline">
            privacy policy
          </a>
          .
        </p>
      </Section>

      <Section heading="Changes to these terms">
        <p>
          If we change these terms materially we will tell you by email before the
          change takes effect.
        </p>
      </Section>
    </LegalPage>
  );
}

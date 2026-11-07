import Link from "next/link";
import { ArrowLeft } from "lucide-react";
import { Button } from "@/components/ui/button";

/**
 * Shared shell for the legal pages.
 *
 * NOTE FOR THE TEAM: the wording in /terms and /privacy describes what the
 * system actually does — what is collected, where it is stored, who processes
 * it — and is accurate as of writing. It has **not** been reviewed by a
 * solicitor. Before launch it needs to be, particularly the UK GDPR lawful
 * basis and the data-processor list, because Marketing25 handles contact
 * details belonging to pharmacy patients.
 */
export function LegalPage({
  title,
  updated,
  children,
}: {
  title: string;
  updated: string;
  children: React.ReactNode;
}) {
  return (
    <div className="min-h-screen bg-background">
      <div className="mx-auto max-w-3xl px-4 py-12 md:py-16">
        <Button asChild variant="ghost" size="sm" className="mb-8">
          <Link href="/">
            <ArrowLeft className="mr-1.5 h-4 w-4" />
            Back to home
          </Link>
        </Button>

        <h1 className="text-3xl font-semibold tracking-tight">{title}</h1>
        <p className="mt-2 text-sm text-muted-foreground">Last updated {updated}</p>

        <div className="mt-10 space-y-8 text-sm leading-relaxed text-foreground/90">
          {children}
        </div>

        <div className="mt-14 border-t border-border/60 pt-6 text-sm text-muted-foreground">
          <p>
            Questions about this page? Email{" "}
            <a
              href="mailto:info@sonarinformatics.com"
              className="font-medium text-primary hover:underline"
            >
              info@sonarinformatics.com
            </a>
            .
          </p>
          <p className="mt-3 flex gap-4">
            <Link href="/terms" className="hover:underline">
              Terms of service
            </Link>
            <Link href="/privacy" className="hover:underline">
              Privacy policy
            </Link>
          </p>
        </div>
      </div>
    </div>
  );
}

export function Section({ heading, children }: { heading: string; children: React.ReactNode }) {
  return (
    <section className="space-y-3">
      <h2 className="text-lg font-medium text-foreground">{heading}</h2>
      {children}
    </section>
  );
}

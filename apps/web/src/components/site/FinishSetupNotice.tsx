"use client";

import Link from "next/link";
import { ArrowRight, Settings2 } from "lucide-react";
import { useAuthStore } from "@/store/auth-store";

/**
 * A standing invitation to finish setup, for anyone who chose to skip it.
 *
 * Skipping is allowed, and the app works — but "works" is doing some quiet
 * work in that sentence. Onboarding is where the pharmacy name, address and
 * contact details come from, and those are what appear on posters and in the
 * From line of every campaign email. Without them a campaign goes out from a
 * pharmacy with no name. Someone who skipped deserves to know that plainly
 * rather than discovering it from a patient.
 *
 * So this is a prompt rather than a lock: it is present on every page, says
 * what is actually missing, and is one click from fixing it. The other half of
 * the arrangement lives in the sign-out path — the skip is a session cookie, so
 * the next sign-in returns to onboarding and asks properly again.
 *
 * It is driven by the server's `onboardingCompleted`, never by the skip cookie.
 * The cookie records a choice; only the API knows whether the work is done, and
 * conflating the two is how a half-finished account starts looking finished.
 */
export function FinishSetupNotice() {
  const user = useAuthStore((s) => s.user);

  if (!user || user.onboardingCompleted) return null;

  return (
    <div className="border-b border-primary/25 bg-primary/[0.07] px-4 py-2.5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="flex items-center gap-2 text-sm text-foreground">
          <Settings2 className="h-4 w-4 shrink-0 text-primary" aria-hidden />
          <span>
            Your pharmacy details aren&apos;t set up yet, so posters and
            campaign emails will go out unbranded.
          </span>
        </p>

        {/* Not prefetched. Where /onboarding goes depends on cookies that
            are re-synced as the app loads; a prefetch made a moment earlier
            can cache an old redirect and send the click somewhere else. */}
        <Link
          href="/onboarding"
          prefetch={false}
          className="inline-flex items-center gap-1.5 rounded-md border border-primary/40 bg-background px-2.5 py-1 text-xs font-medium text-primary transition-colors hover:bg-primary/10"
        >
          Finish setup
          <ArrowRight className="h-3.5 w-3.5" aria-hidden />
        </Link>
      </div>
    </div>
  );
}

"use client";

import { useEffect, useState } from "react";
import { Clock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { expireSession, markActivity, onIdleWarning } from "@/lib/session";

/**
 * Countdown shown shortly before an idle session is ended.
 *
 * The point is not the countdown — it's that nobody loses half-finished
 * poster work to a timeout they never saw coming. Someone reading a long
 * patient list, or thinking about copy, is idle by any technical measure and
 * would otherwise be signed out mid-thought with no warning.
 *
 * Rendered inline rather than with `position: fixed` so it participates in
 * normal layout; it sits at the top of the app shell where it can't be missed.
 */
export function SessionTimeoutDialog() {
  const [secondsLeft, setSecondsLeft] = useState<number | null>(null);

  useEffect(() => onIdleWarning(setSecondsLeft), []);

  if (secondsLeft === null) return null;

  const minutes = Math.floor(secondsLeft / 60);
  const seconds = secondsLeft % 60;
  const display =
    minutes > 0 ? `${minutes}:${String(seconds).padStart(2, "0")}` : `${seconds}s`;

  return (
    <div
      role="alertdialog"
      aria-live="assertive"
      aria-label="Session about to expire"
      className="sticky top-0 z-50 border-b border-amber-500/30 bg-amber-500/10 backdrop-blur"
    >
      <div className="mx-auto flex max-w-5xl flex-wrap items-center gap-3 px-4 py-3">
        <Clock className="h-4 w-4 shrink-0 text-amber-600 dark:text-amber-500" />

        <p className="min-w-0 flex-1 text-sm">
          <span className="font-medium">
            You&apos;ll be signed out in {display}
          </span>
          <span className="ml-1.5 text-muted-foreground">
            for security, because there&apos;s been no activity. Save anything
            you&apos;re working on.
          </span>
        </p>

        <div className="flex gap-2">
          <Button
            size="sm"
            onClick={() => {
              // Resets the idle clock. The monitor's next tick sees a fresh
              // timestamp, clears the warning, and renews the token.
              markActivity();
              setSecondsLeft(null);
            }}
          >
            Stay signed in
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={() => expireSession("inactive")}
          >
            Sign out now
          </Button>
        </div>
      </div>
    </div>
  );
}

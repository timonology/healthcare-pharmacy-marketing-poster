"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { AlertTriangle, CheckCircle2, Loader2 } from "lucide-react";
import type { DiscoveredAccount, SocialOAuthProviderKind } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { PlatformIcon } from "@/components/social/PlatformIcon";

/**
 * Where the platform sends the pharmacy back after consenting.
 *
 * Two things happen here and they are deliberately separate: the code is
 * exchanged for credentials (server-side), and then the pharmacy chooses which
 * of the discovered accounts to actually connect. A pharmacy group may
 * administer five Pages and want one — connecting all of them because they
 * appeared in a response is not consent.
 */
export default function OAuthCallbackPage() {
  // useParams rather than the `params` prop: the prop is a Promise from
  // Next 15, and the hook reads the same value in both versions.
  const { provider } = useParams<{ provider: string }>();
  return (
    <Suspense
      fallback={
        <main className="container flex max-w-md flex-col items-center py-24">
          <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
        </main>
      }
    >
      <CallbackView provider={provider} />
    </Suspense>
  );
}

function CallbackView({ provider }: { provider: string }) {
  const router = useRouter();
  const search = useSearchParams();

  const [accounts, setAccounts] = useState<DiscoveredAccount[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [connected, setConnected] = useState(false);
  // The exchange is single use — the provider's code and our state are both
  // consumed by the first call. React runs effects twice in development, and a
  // second call would replace the account list with "already used".
  const exchanged = useRef(false);

  useEffect(() => {
    if (exchanged.current) return;
    exchanged.current = true;

    const code = search?.get("code");
    const state = search?.get("state");
    const denied = search?.get("error") ?? search?.get("error_reason");

    if (denied) {
      // The pharmacy pressed cancel, or the platform refused. Not an error
      // worth alarming language over.
      // LinkedIn says user_cancelled_login / user_cancelled_authorize.
      const cancelled = denied === "access_denied" || denied.startsWith("user_cancelled");
      const detail = search?.get("error_description");
      setError(
        cancelled
          ? "You cancelled the connection. Nothing was changed."
          : `The platform returned: ${detail ? `${denied} (${detail})` : denied}`,
      );
      return;
    }

    if (!code || !state) {
      setError("That callback was missing its code. Please start again.");
      return;
    }

    const expected = sessionStorage.getItem("social-oauth-state");
    const kind = sessionStorage.getItem("social-oauth-kind") as SocialOAuthProviderKind | null;

    // The state we stored when starting must match what came back. The server
    // binds it to this account and provider and accepts it once; this is the
    // second lock, and it catches a mismatched flow before a round trip.
    if (!expected || expected !== state) {
      setError(
        "This connection attempt did not match the one started in this browser. Please start again.",
      );
      return;
    }

    if (!kind) {
      setError("That connection attempt has expired. Please start again.");
      return;
    }

    setSessionId(state);

    api.social
      .completeConnect(kind, code, state)
      .then((found) => {
        setAccounts(found);
        // Pre-tick everything. New accounts: the common case is a single
        // Page, and a two-click flow there is friction without a purpose.
        // Already-connected ones: this is usually a Reconnect, and ticking
        // one renews its sign-in in place. Leaving them unticked made
        // Reconnect a no-op ("Connect 0"), and for LinkedIn — where a profile
        // and its Pages share one 60-day sign-in — left the Pages expiring
        // after the profile was renewed.
        setSelected(new Set(found.map((a) => a.handle)));
      })
      .catch((err: unknown) =>
        // The platform's own message, verbatim. A redirect-URI mismatch says so
        // precisely, and replacing it with "connection failed" turns a
        // five-minute fix into an afternoon.
        setError(err instanceof Error ? err.message : "Could not complete the connection."),
      );
  }, [search, provider]);

  async function connect() {
    if (!sessionId) return;
    setBusy(true);
    setError(null);
    try {
      await api.social.connect(sessionId, [...selected]);
      sessionStorage.removeItem("social-oauth-state");
      sessionStorage.removeItem("social-oauth-kind");
      setConnected(true);
      setTimeout(() => router.replace("/social?tab=accounts"), 1200);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save the connection.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="container max-w-md py-16">
      <h1 className="text-xl font-semibold tracking-tight">Connect accounts</h1>

      {error && (
        <div className="mt-4 flex items-start gap-2 rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {connected && (
        <div className="mt-4 flex items-center gap-2 rounded-md bg-primary/10 p-3 text-sm text-primary">
          <CheckCircle2 className="h-4 w-4" />
          Connected. Taking you back…
        </div>
      )}

      {!error && !connected && accounts === null && (
        <p className="mt-6 flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          Talking to the platform…
        </p>
      )}

      {accounts && !connected && (
        <>
          <p className="mt-2 text-sm text-muted-foreground">
            Choose which to connect. You can add or remove these at any time.
          </p>

          <ul className="mt-4 space-y-1.5">
            {accounts.map((a) => (
              <li key={a.handle}>
                <button
                  type="button"
                  onClick={() =>
                    setSelected((prev) => {
                      const next = new Set(prev);
                      if (next.has(a.handle)) next.delete(a.handle);
                      else next.add(a.handle);
                      return next;
                    })
                  }
                  className={cn(
                    "flex w-full items-center gap-3 rounded-md border p-3 text-left transition-colors",
                    selected.has(a.handle)
                      ? "border-primary bg-primary/5"
                      : "border-border/60 hover:bg-accent/50",
                  )}
                >
                  <input
                    type="checkbox"
                    readOnly
                    checked={selected.has(a.handle)}
                    className="h-3.5 w-3.5 accent-primary"
                    tabIndex={-1}
                  />
                  <PlatformIcon platform={a.platform} className="h-4 w-4" />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-sm font-medium">
                      {a.displayName}
                    </span>
                    <span className="block text-[11px] text-muted-foreground">
                      {a.platformName}
                      {a.alreadyConnected && " · already connected — sign-in will be renewed"}
                    </span>
                  </span>
                </button>
              </li>
            ))}
          </ul>

          <div className="mt-5 flex justify-end gap-2">
            <Button asChild variant="outline" size="sm">
              <Link href="/social">Cancel</Link>
            </Button>
            <Button size="sm" disabled={busy || selected.size === 0} onClick={connect}>
              {busy ? "Connecting…" : `Connect ${selected.size}`}
            </Button>
          </div>
        </>
      )}

      {error && (
        <Button asChild variant="outline" size="sm" className="mt-5">
          <Link href="/social">Back to Social &amp; Ads</Link>
        </Button>
      )}
    </main>
  );
}

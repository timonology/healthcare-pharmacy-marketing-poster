"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, Link2, Loader2, Plug, Trash2 } from "lucide-react";
import Link from "next/link";
import type {
  SocialAvailability,
  SocialConnection,
  SocialOAuthProviderKind,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { expiryState, reconnectKind } from "@/lib/social-connections";
import { cn } from "@/lib/utils";
import { PlatformIcon } from "./PlatformIcon";

/**
 * Connected accounts, and the buttons that add more.
 *
 * The OAuth redirect URI is decided by the server, from its configured public
 * web address (`Public__WebBaseUrl`). This page sends the one it expects so a
 * mismatch — the app opened on a different host — is reported plainly before
 * the pharmacy is sent to Meta, Google or LinkedIn. It must also match what
 * is registered in each platform's app settings exactly; the callback page
 * shows the platform's own message when it does not.
 */
/**
 * Starts the OAuth round trip and leaves the page for the provider.
 *
 * Shared by this tab and the Recurring tab's empty state, so there is one
 * Connect flow — the same state handling, the same callback page.
 */
export async function beginConnect(kind: SocialOAuthProviderKind): Promise<void> {
  const redirectUri = `${window.location.origin}/social/callback/${kind.toLowerCase()}`;
  const start = await api.social.startConnect(kind, redirectUri);

  // Held so the callback page can prove the response belongs to the flow
  // this tab began. The server binds it too — this is the second lock.
  sessionStorage.setItem("social-oauth-state", start.state);
  sessionStorage.setItem("social-oauth-kind", kind);

  window.location.href = start.authorizationUrl;
}

/** The Facebook & Instagram Connect button, for places other than this tab. */
export function MetaConnectButton({ className }: { className?: string }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <div className={className}>
      <Button
        size="sm"
        className="gap-1.5"
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError(null);
          try {
            await beginConnect("Meta");
          } catch (err) {
            setError(err instanceof Error ? err.message : "Could not start the connection.");
            setBusy(false);
          }
        }}
      >
        {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plug className="h-3.5 w-3.5" />}
        {busy ? "Opening…" : "Connect Facebook & Instagram"}
      </Button>
      {error && <p className="mt-2 text-xs text-destructive">{error}</p>}
    </div>
  );
}

export function ConnectionsTab() {
  const [connections, setConnections] = useState<SocialConnection[] | null>(null);
  const [availability, setAvailability] = useState<SocialAvailability | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [list, avail] = await Promise.all([
        api.social.connections(),
        api.social.availability(),
      ]);
      setConnections(list);
      setAvailability(avail);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load your accounts.");
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function connect(kind: SocialOAuthProviderKind) {
    setBusy(kind);
    setError(null);
    try {
      await beginConnect(kind);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not start the connection.");
      setBusy(null);
    }
  }

  async function disconnect(connection: SocialConnection) {
    if (
      !window.confirm(
        `Disconnect ${connection.displayName}? Scheduled posts to this account will fail, and recurring posts will stop going to it.`,
      )
    )
      return;

    setBusy(connection.id);
    try {
      await api.social.disconnect(connection.id);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not disconnect.");
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="space-y-4">
      {error && (
        <p className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
          {error}
        </p>
      )}

      {availability && !availability.planAllows && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-primary/30 bg-primary/5 p-4">
          <div>
            <p className="text-sm font-medium">Publish to Facebook, Instagram, YouTube, LinkedIn, TikTok and X from here</p>
            <p className="mt-0.5 text-xs text-muted-foreground">
              {availability.planReason ?? "Social publishing is not included in your plan."}
            </p>
          </div>
          <Button asChild size="sm">
            <Link href="/pricing">See plans</Link>
          </Button>
        </div>
      )}

      <section className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <ConnectCard
          title="Facebook & Instagram"
          body="One connection covers your Facebook Page and any Instagram Business account linked to it."
          note="Instagram must be a Business or Creator account linked to a Page — a personal account will not appear."
          busy={busy === "Meta"}
          unavailable={unavailableReason(availability, "Meta")}
          onConnect={() => connect("Meta")}
        />
        <ConnectCard
          title="Google Ads"
          body="Run local Google Ads campaigns from your posters and videos."
          note="Google Business Profile posting is not available yet."
          busy={busy === "Google"}
          unavailable={unavailableReason(availability, "Google")}
          onConnect={() => connect("Google")}
        />
        <ConnectCard
          title="YouTube"
          body="Post your videos to your YouTube channel as Shorts."
          note="Sign in with the Google account that owns the channel. Videos only — posters are not offered for YouTube."
          busy={busy === "YouTube"}
          unavailable={unavailableReason(availability, "YouTube")}
          onConnect={() => connect("YouTube")}
        />
        <ConnectCard
          title="LinkedIn"
          body="Post posters and videos to your LinkedIn profile, and to company Pages you manage where available."
          note="LinkedIn asks you to sign in again every 60 days. We show a reminder here two weeks before."
          busy={busy === "LinkedIn"}
          unavailable={unavailableReason(availability, "LinkedIn")}
          onConnect={() => connect("LinkedIn")}
        />
        <ConnectCard
          title="TikTok"
          body="Post your videos to your TikTok account. You choose who can see each post."
          note="Videos only. Until TikTok approves Marketing25, posts are private and the TikTok account itself must be set to private."
          busy={busy === "TikTok"}
          unavailable={unavailableReason(availability, "TikTok")}
          onConnect={() => connect("TikTok")}
        />
        <ConnectCard
          title="X"
          body="Post your posters and videos to your X (formerly Twitter) account."
          note="X allows 280 characters per post, with each link counting as 23 and each emoji as 2."
          busy={busy === "X"}
          unavailable={unavailableReason(availability, "X")}
          onConnect={() => connect("X")}
        />
      </section>

      <section className="rounded-lg border border-border/60 bg-card">
        <header className="border-b border-border/60 px-4 py-3">
          <h2 className="text-sm font-semibold tracking-tight">
            {connections ? `Connected accounts (${connections.length})` : "Connected accounts"}
          </h2>
        </header>

        {connections === null ? (
          <div className="flex items-center gap-2 p-6 text-sm text-muted-foreground">
            <Loader2 className="h-4 w-4 animate-spin" />
            Loading…
          </div>
        ) : connections.length === 0 ? (
          <div className="flex flex-col items-center gap-2 px-6 py-12 text-center">
            <div className="rounded-full bg-muted p-3">
              <Plug className="h-5 w-5 text-muted-foreground" aria-hidden />
            </div>
            <p className="text-sm font-medium">Nothing connected yet</p>
            <p className="max-w-sm text-xs text-muted-foreground">
              Connect an account above and you can publish posters and videos
              straight from your library.
            </p>
          </div>
        ) : (
          <ul className="divide-y divide-border/40">
            {connections.map((c) => {
              const expiry = expiryState(c);
              const needsReconnect = c.status === "NeedsReauth" || expiry.kind === "expired";
              return (
              <li key={c.id} className="flex items-center gap-3 px-4 py-3">
                <PlatformIcon platform={c.platform} className="h-5 w-5 shrink-0" />

                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium">{c.displayName}</p>
                  <p className="text-xs text-muted-foreground">{c.platformName}</p>

                  {c.status === "NeedsReauth" && (
                    <p className="mt-1 flex items-start gap-1.5 text-[11px] text-amber-700 dark:text-amber-400">
                      <AlertTriangle className="mt-0.5 h-3 w-3 shrink-0" />
                      {/* The platform's own words. Replacing them with
                          "authentication failed" throws away the one detail
                          that says what to do. */}
                      <span>{c.lastError ?? "Needs reconnecting."}</span>
                    </p>
                  )}
                  {expiry.kind !== "none" && (
                    <p className="mt-1 flex items-start gap-1.5 text-[11px] text-amber-700 dark:text-amber-400">
                      <AlertTriangle className="mt-0.5 h-3 w-3 shrink-0" />
                      <span>
                        {expiry.kind === "expired"
                          ? "The sign-in has expired. Reconnect to keep posting."
                          : `The sign-in expires on ${expiry.at.toLocaleDateString(undefined, { day: "numeric", month: "short" })}. Reconnect before then to keep posting.`}
                      </span>
                    </p>
                  )}
                </div>

                <span
                  className={cn(
                    "rounded-full px-2 py-0.5 text-[11px] font-medium",
                    !needsReconnect
                      ? "bg-primary/10 text-primary"
                      : "bg-amber-500/15 text-amber-700 dark:text-amber-400",
                  )}
                >
                  {!needsReconnect ? "Connected" : "Reconnect"}
                </span>

                {(needsReconnect || expiry.kind === "soon") && (
                  <Button
                    size="sm"
                    variant="outline"
                    className="h-7 gap-1 text-xs"
                    disabled={busy !== null}
                    onClick={() => connect(reconnectKind(c.platform))}
                  >
                    <Link2 className="h-3.5 w-3.5" />
                    Reconnect
                  </Button>
                )}

                <Button
                  size="sm"
                  variant="ghost"
                  className="h-7 w-7 p-0 text-muted-foreground hover:text-destructive"
                  disabled={busy === c.id}
                  onClick={() => disconnect(c)}
                  aria-label={`Disconnect ${c.displayName}`}
                >
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              </li>
              );
            })}
          </ul>
        )}
      </section>
    </div>
  );
}

/**
 * Why a provider cannot be connected, or null when it can. Unknown (still
 * loading) counts as available: the server refuses anything that is not, so a
 * brief optimistic button costs nothing.
 */
function unavailableReason(
  availability: SocialAvailability | null,
  kind: SocialOAuthProviderKind,
): string | null {
  const entry = availability?.providers.find((p) => p.kind === kind);
  return entry && !entry.available ? (entry.reason ?? "Not available.") : null;
}

function ConnectCard({
  title,
  body,
  note,
  busy,
  unavailable,
  onConnect,
}: {
  title: string;
  body: string;
  note: string;
  busy: boolean;
  /** Why this cannot be connected right now. Disables the button and says so. */
  unavailable: string | null;
  onConnect: () => void;
}) {
  return (
    <div className="flex flex-col rounded-lg border border-border/60 bg-card p-4">
      <h3 className="text-sm font-semibold">{title}</h3>
      <p className="mt-1 text-xs text-muted-foreground">{body}</p>
      <p className="mt-2 text-[11px] text-muted-foreground">{note}</p>
      {unavailable && (
        <p className="mt-2 text-[11px] text-amber-700 dark:text-amber-400">{unavailable}</p>
      )}
      <Button
        size="sm"
        className="mt-3 gap-1.5"
        disabled={busy || unavailable !== null}
        onClick={onConnect}
      >
        {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plug className="h-3.5 w-3.5" />}
        {busy ? "Opening…" : "Connect"}
      </Button>
    </div>
  );
}

"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import {
  Copy,
  Film,
  Link2,
  ListVideo,
  Loader2,
  Monitor,
  Plus,
  RefreshCw,
  Trash2,
  TriangleAlert,
} from "lucide-react";
import type { DisplayScreen, Playlist } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api";
import { videoCount } from "@/lib/playlist-items";

/**
 * The screens a pharmacy runs, and how a device is trusted to show one.
 *
 * Two ways in, deliberately. A pairing code is the normal path: nothing secret
 * is written down, the code dies in four hours, and it works once. A direct
 * link exists for hardware that cannot take a code — a browser with no usable
 * keyboard, or a screen somebody has to set up remotely — and is a worse
 * credential by nature, so it is created only when asked for, shown once, and
 * revocable on its own.
 */
export default function DisplaysPage() {
  const [screens, setScreens] = useState<DisplayScreen[]>([]);
  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [form, setForm] = useState({ name: "", location: "", addressLine: "" });

  /** The one-time token, held only until the page is left. */
  const [issued, setIssued] = useState<{ screenId: string; url: string } | null>(null);

  const load = useCallback(async () => {
    try {
      const [s, p] = await Promise.all([
        api.displays.listScreens(),
        api.displays.listPlaylists(),
      ]);
      setScreens(s);
      setPlaylists(p);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load screens.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function run(key: string, fn: () => Promise<unknown>) {
    setBusy(key);
    setError(null);
    try {
      await fn();
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "That did not work.");
    } finally {
      setBusy(null);
    }
  }

  async function add(e: React.FormEvent) {
    e.preventDefault();
    await run("add", async () => {
      await api.displays.registerScreen({
        name: form.name,
        location: form.location || undefined,
        addressLine: form.addressLine || undefined,
      });
      setForm({ name: "", location: "", addressLine: "" });
      setAdding(false);
    });
  }

  return (
    <main className="container max-w-4xl py-8">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Screens</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Displays in your pharmacy, and the playlist each one loops.
          </p>
        </div>
        <div className="flex gap-2">
          <Button asChild variant="outline" size="sm" className="gap-1.5">
            <Link href="/playlists">
              <ListVideo className="h-4 w-4" />
              Playlists
            </Link>
          </Button>
          <Button size="sm" className="gap-1.5" onClick={() => setAdding((v) => !v)}>
            <Plus className="h-4 w-4" />
            Add screen
          </Button>
        </div>
      </header>

      {error && (
        <p
          role="alert"
          className="mt-4 rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {error}
        </p>
      )}

      {adding && (
        <form
          onSubmit={add}
          className="mt-5 space-y-3 rounded-lg border border-border/60 p-4"
        >
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="screen-name">Name</Label>
              <Input
                id="screen-name"
                required
                value={form.name}
                onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
                placeholder="Waiting room"
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="screen-location">Where it hangs</Label>
              <Input
                id="screen-location"
                value={form.location}
                onChange={(e) => setForm((f) => ({ ...f, location: e.target.value }))}
                placeholder="Behind the counter"
              />
            </div>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="screen-address">Address shown on the screen</Label>
            <Input
              id="screen-address"
              value={form.addressLine}
              onChange={(e) => setForm((f) => ({ ...f, addressLine: e.target.value }))}
              placeholder="12 Riverside Walk, Manchester M3 4AB"
            />
            <p className="text-[11px] text-muted-foreground">
              Optional, and drawn along the bottom of the display. Leave it empty
              for a screen inside the shop, where people already know where they are.
            </p>
          </div>

          <div className="flex gap-2">
            <Button type="submit" size="sm" disabled={busy === "add" || !form.name.trim()}>
              {busy === "add" ? "Adding…" : "Add screen"}
            </Button>
            <Button
              type="button"
              size="sm"
              variant="ghost"
              onClick={() => setAdding(false)}
            >
              Cancel
            </Button>
          </div>
        </form>
      )}

      {loading ? (
        <p className="mt-8 flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading screens…
        </p>
      ) : screens.length === 0 ? (
        <div className="mt-8 rounded-lg border border-dashed border-border/60 p-10 text-center">
          <Monitor className="mx-auto h-8 w-8 text-muted-foreground/50" aria-hidden />
          <p className="mt-3 font-medium">No screens yet</p>
          <p className="mx-auto mt-1 max-w-md text-sm text-muted-foreground">
            Add a screen here, then open{" "}
            <span className="font-mono text-foreground">/display</span> on the
            television or tablet and type the pairing code.
          </p>
        </div>
      ) : (
        <ul className="mt-6 space-y-4">
          {screens.map((screen) => (
            <ScreenCard
              key={screen.id}
              screen={screen}
              playlists={playlists}
              busy={busy}
              issuedUrl={issued?.screenId === screen.id ? issued.url : null}
              onDismissIssued={() => setIssued(null)}
              onRun={run}
              onIssued={(url) => setIssued({ screenId: screen.id, url })}
            />
          ))}
        </ul>
      )}
    </main>
  );
}

function ScreenCard({
  screen,
  playlists,
  busy,
  issuedUrl,
  onRun,
  onIssued,
  onDismissIssued,
}: {
  screen: DisplayScreen;
  playlists: Playlist[];
  busy: string | null;
  issuedUrl: string | null;
  onRun: (key: string, fn: () => Promise<unknown>) => Promise<void>;
  onIssued: (url: string) => void;
  onDismissIssued: () => void;
}) {
  const anyBusy = busy !== null;

  return (
    <li className="rounded-lg border border-border/60 p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-medium">{screen.name}</h2>
            {screen.isPaired ? (
              <Badge variant="secondary">Paired</Badge>
            ) : (
              <Badge variant="outline">Not paired</Badge>
            )}
            <LastSeen at={screen.lastSeenAtUtc} paired={screen.isPaired} />
          </div>
          {screen.location && (
            <p className="mt-0.5 text-sm text-muted-foreground">{screen.location}</p>
          )}
        </div>

        <Button
          size="sm"
          variant="ghost"
          className="gap-1.5 text-destructive hover:text-destructive"
          disabled={anyBusy}
          onClick={() => {
            if (!confirm(`Remove "${screen.name}"? The screen will stop showing anything.`)) return;
            void onRun(`del-${screen.id}`, () => api.displays.deleteScreen(screen.id));
          }}
        >
          <Trash2 className="h-3.5 w-3.5" />
          Remove
        </Button>
      </div>

      {/* ---- playlist ---- */}
      <div className="mt-4 flex flex-wrap items-center gap-2">
        <Label htmlFor={`playlist-${screen.id}`} className="text-xs">
          Showing
        </Label>
        <select
          id={`playlist-${screen.id}`}
          value={screen.playlistId ?? ""}
          disabled={anyBusy}
          className="h-9 rounded-md border border-border/60 bg-background px-2 text-sm"
          onChange={(e) =>
            void onRun(`pl-${screen.id}`, () =>
              api.displays.assignPlaylist(screen.id, e.target.value || null),
            )
          }
        >
          <option value="">Nothing assigned</option>
          {playlists.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name} ({p.items.length})
            </option>
          ))}
        </select>

        {(() => {
          // Which videos this screen is set to play. A badge only — the
          // screen itself decides, each poll, which of them are still Ready.
          const assigned = playlists.find((p) => p.id === screen.playlistId);
          const count = assigned ? videoCount(assigned.items) : 0;
          return count > 0 ? (
            <Badge variant="secondary" className="gap-1 text-[10px]">
              <Film className="h-3 w-3" />
              {count} video{count === 1 ? "" : "s"}
            </Badge>
          ) : null;
        })()}

        {screen.playlistId && screen.itemCount === 0 && (
          <span className="flex items-center gap-1 text-xs text-amber-600 dark:text-amber-500">
            <TriangleAlert className="h-3.5 w-3.5" />
            That playlist is empty, so this screen shows nothing
          </span>
        )}
      </div>

      {/*
        ---- pairing ----

        One row of controls in every state, with only the message above them
        changing.

        This used to be an if/else on whether a code was outstanding, and
        <b>Direct link only existed in the else</b>. Registering a screen issues
        a code straight away, so for the first four hours of a screen's life —
        which is to say while somebody is standing in front of it installing it
        — the direct link was not on the page at all. It reappeared once the
        code expired.

        That hid it exactly from the person it is for. A direct link is the
        escape hatch for hardware that cannot type a code: a screen with no
        usable keyboard, or one being set up remotely. Waiting four hours for
        the button to appear is not a workaround anybody would guess at.

        The two credentials are independent — issuing a link does not disturb a
        paired device and does not consume the code — so there was never a
        reason for one to hide the other.
      */}
      <div className="mt-4 rounded-md bg-muted/50 p-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          {screen.pairingCode ? (
            <div>
              <p className="text-xs text-muted-foreground">
                On the screen, open{" "}
                <span className="font-mono text-foreground">/display</span> and type
              </p>
              <p className="mt-1 font-mono text-3xl font-semibold tracking-[0.25em]">
                {screen.pairingCode}
              </p>
              <p className="mt-1 text-[11px] text-muted-foreground">
                Works once. Expires{" "}
                {screen.pairingCodeExpiresAtUtc
                  ? new Date(screen.pairingCodeExpiresAtUtc).toLocaleTimeString()
                  : "shortly"}
                .
              </p>
            </div>
          ) : (
            <p className="text-xs text-muted-foreground">
              {screen.isPaired
                ? "A device is paired to this screen. Pairing a new one replaces it."
                : "No pairing code outstanding."}
            </p>
          )}

          <div className="flex gap-2">
            <Button
              size="sm"
              variant="outline"
              className="gap-1.5"
              disabled={anyBusy}
              onClick={() =>
                void onRun(`code-${screen.id}`, () =>
                  api.displays.refreshPairingCode(screen.id),
                )
              }
            >
              <RefreshCw className="h-3.5 w-3.5" />
              {/*
                "Replace", not "another". A screen is one physical panel, and
                the domain retires the previous paired credential when a new
                device claims a code — so a label promising an additional
                device would be describing something that does not happen,
                and would leave somebody wondering why the first screen went
                dark.
              */}
              {screen.pairingCode
                ? "New code"
                : screen.isPaired
                  ? "Replace paired device"
                  : "Get a pairing code"}
            </Button>
            <Button
              size="sm"
              variant="ghost"
              className="gap-1.5"
              disabled={anyBusy}
              onClick={() =>
                void onRun(`link-${screen.id}`, async () => {
                  const issued = await api.displays.issueDirectLink(screen.id);
                  onIssued(
                    `${window.location.origin}/display#token=${issued.token}`,
                  );
                })
              }
            >
              <Link2 className="h-3.5 w-3.5" />
              Direct link
            </Button>
          </div>
        </div>
      </div>

      {issuedUrl && <IssuedLink url={issuedUrl} onDismiss={onDismissIssued} />}

      {/* ---- what can reach this screen ---- */}
      {screen.credentials.length > 0 && (
        <ul className="mt-3 space-y-1.5">
          {screen.credentials.map((c) => (
            <li
              key={c.id}
              className="flex flex-wrap items-center justify-between gap-2 text-xs"
            >
              <span className="text-muted-foreground">
                {c.kind === "PairedDevice" ? "Paired device" : "Direct link"} · added{" "}
                {new Date(c.createdAtUtc).toLocaleDateString()}
                {c.lastSeenAtUtc
                  ? ` · last seen ${new Date(c.lastSeenAtUtc).toLocaleString()}`
                  : " · never connected"}
              </span>
              <Button
                size="sm"
                variant="ghost"
                className="h-7 text-xs text-destructive hover:text-destructive"
                disabled={anyBusy}
                onClick={() => {
                  if (!confirm("Revoke this access? That device will stop showing content.")) return;
                  void onRun(`rev-${c.id}`, () =>
                    api.displays.revokeCredential(screen.id, c.id),
                  );
                }}
              >
                Revoke
              </Button>
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}

/**
 * The direct link, shown once.
 *
 * Said plainly, because the alternative is somebody closing the page and
 * generating another one — and each new link silently retires the last, which
 * would leave a screen dark for reasons nobody could reconstruct.
 */
function IssuedLink({ url, onDismiss }: { url: string; onDismiss: () => void }) {
  const [copied, setCopied] = useState(false);

  return (
    <div className="mt-3 rounded-md border border-amber-500/40 bg-amber-50 p-3 dark:bg-amber-950/40">
      <p className="text-xs font-medium text-amber-900 dark:text-amber-200">
        Copy this now — it is not shown again.
      </p>
      <div className="mt-2 flex gap-2">
        <Input readOnly value={url} className="h-8 font-mono text-xs" />
        <Button
          size="sm"
          variant="outline"
          className="h-8 shrink-0 gap-1.5"
          onClick={async () => {
            try {
              await navigator.clipboard.writeText(url);
              setCopied(true);
            } catch {
              // Clipboard is refused on an insecure origin and in some locked
              // down browsers. The field is selectable, so this is a downgrade
              // rather than a dead end.
              setCopied(false);
            }
          }}
        >
          <Copy className="h-3.5 w-3.5" />
          {copied ? "Copied" : "Copy"}
        </Button>
      </div>
      <p className="mt-2 text-[11px] text-amber-800 dark:text-amber-300/80">
        Anyone with this link can see what the screen shows. Revoke it below if it
        goes astray.
      </p>
      <Button size="sm" variant="ghost" className="mt-1 h-7 text-xs" onClick={onDismiss}>
        Done
      </Button>
    </div>
  );
}

/**
 * How long since the screen last called home.
 *
 * The one field somebody looks at when a display in the corner has gone dark,
 * so it is phrased as an answer to that question rather than as a timestamp.
 */
function LastSeen({ at, paired }: { at: string | null; paired: boolean }) {
  if (!paired) return null;

  if (!at) {
    return (
      <span className="text-xs text-muted-foreground">paired, not yet connected</span>
    );
  }

  const minutes = Math.round((Date.now() - new Date(at).getTime()) / 60000);

  // A screen polls every thirty seconds, so anything past a couple of minutes
  // means it is switched off, asleep, or off the network.
  if (minutes <= 2) {
    return (
      <span className="flex items-center gap-1.5 text-xs text-emerald-600 dark:text-emerald-500">
        <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
        Live
      </span>
    );
  }

  const ago =
    minutes < 60
      ? `${minutes} min ago`
      : minutes < 60 * 24
        ? `${Math.round(minutes / 60)} h ago`
        : `${Math.round(minutes / 1440)} d ago`;

  return (
    <span className="flex items-center gap-1.5 text-xs text-amber-600 dark:text-amber-500">
      <span className="h-1.5 w-1.5 rounded-full bg-amber-500" />
      Last seen {ago}
    </span>
  );
}

"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { AlertTriangle, CalendarClock, Send, Sparkles } from "lucide-react";
import type { PublishSourceKind, SocialConnection, TikTokCreatorInfo } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { api } from "@/lib/api";
import { emptyTikTokForm, tikTokProblem, toTikTokOptions, type TikTokForm } from "@/lib/tiktok-post";
import { cn } from "@/lib/utils";
import { X_MAX_WEIGHTED_LENGTH, xWeightedLength } from "@/lib/x-text";
import { PlatformIcon } from "./PlatformIcon";
import { TikTokPostSettings } from "./TikTokPostSettings";

/** Instagram's limit, and the lowest of the platforms offered. */
const CAPTION_LIMIT = 2200;

/**
 * A Date as a datetime-local value, in the browser's own zone.
 *
 * Not `toISOString().slice(0, 16)`: that is UTC, so the earliest time the
 * picker allowed was an hour out in UK summer, and several hours out — in the
 * other direction — anywhere west of Greenwich.
 */
function localDateTimeValue(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` +
    `T${pad(d.getHours())}:${pad(d.getMinutes())}`
  );
}

/**
 * Publish a poster or video to connected accounts, now or later.
 *
 * Shared by the poster and video pages rather than duplicated, because the
 * rules about what can go where — Google Business Profile takes no video,
 * a connection needing re-auth cannot be a target — should exist once.
 *
 * The datetime input collects the pharmacy's *local* time and this converts to
 * UTC before sending. Sending wall-clock time would make the server guess a
 * timezone, and it would guess wrong twice a year.
 */
export function PublishDialog({
  open,
  onClose,
  sourceKind,
  sourceId,
  sourceName,
  isVideo,
  onPublished,
}: {
  open: boolean;
  onClose: () => void;
  sourceKind: PublishSourceKind;
  sourceId: string;
  sourceName: string;
  /** Drives which platforms are offered. Videos exclude Google Business Profile. */
  isVideo: boolean;
  onPublished?: () => void;
}) {
  const [connections, setConnections] = useState<SocialConnection[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [caption, setCaption] = useState("");
  const [mode, setMode] = useState<"now" | "later">("now");
  const [localWhen, setLocalWhen] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [tikTokForm, setTikTokForm] = useState<TikTokForm>(emptyTikTokForm());
  const [tikTokInfo, setTikTokInfo] = useState<TikTokCreatorInfo | null>(null);
  const [tikTokLoading, setTikTokLoading] = useState(false);
  const [tikTokError, setTikTokError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    // A fresh form each time. Carrying the last post's accounts and caption
    // into the next one is how a poster goes somewhere it was not meant to.
    setError(null);
    setDone(false);
    setSelected(new Set());
    setCaption("");
    setMode("now");
    setLocalWhen("");
    setTikTokForm(emptyTikTokForm());
    setTikTokInfo(null);
    setTikTokError(null);
    api.social
      .connections()
      .then(setConnections)
      .catch((err: unknown) =>
        setError(err instanceof Error ? err.message : "Could not load your accounts."),
      );
  }, [open]);

  // Google Ads is a connection but never a publish target, and a video cannot
  // go to Google Business Profile. Filtering here rather than disabling on
  // click means the pharmacy is never invited to make a choice we will refuse.
  const publishable = useMemo(
    () =>
      (connections ?? []).filter(
        // And YouTube takes videos only, so it is not offered for a poster.
        (c) => c.canPublish && (isVideo ? c.supportsVideo : c.supportsImages !== false),
      ),
    [connections, isVideo],
  );

  const excludedForVideo = useMemo(
    () => (connections ?? []).filter((c) => c.canPublish && isVideo && !c.supportsVideo),
    [connections, isVideo],
  );

  const needsReauth = (connections ?? []).filter((c) => c.status === "NeedsReauth");
  const gbpSelected = publishable.some(
    (c) => selected.has(c.id) && c.platform === "GoogleBusinessProfile",
  );
  const youtubeSelected = publishable.some(
    (c) => selected.has(c.id) && c.platform === "YouTubeChannel",
  );
  const tikTokSelected = publishable.filter(
    (c) => selected.has(c.id) && c.platform === "TikTokAccount",
  );
  const tikTokId = tikTokSelected.length === 1 ? tikTokSelected[0].id : null;
  const xSelected = publishable.some((c) => selected.has(c.id) && c.platform === "XAccount");
  const xLength = xWeightedLength(caption.trim());
  const xTooLong = xSelected && xLength > X_MAX_WEIGHTED_LENGTH;

  // TikTok's guidelines: read the account's settings live each time it is
  // chosen, and start the choices afresh — never a remembered audience.
  useEffect(() => {
    if (!tikTokId) {
      setTikTokInfo(null);
      setTikTokError(null);
      return;
    }
    let cancelled = false;
    setTikTokLoading(true);
    setTikTokError(null);
    setTikTokForm(emptyTikTokForm());
    api.social
      .tikTokCreator(tikTokId)
      .then((info) => !cancelled && setTikTokInfo(info))
      .catch((err: unknown) => {
        if (cancelled) return;
        setTikTokInfo(null);
        setTikTokError(err instanceof Error ? err.message : "TikTok did not answer.");
      })
      .finally(() => !cancelled && setTikTokLoading(false));
    return () => {
      cancelled = true;
    };
  }, [tikTokId]);

  const tikTokBlocker =
    tikTokSelected.length > 1
      ? "Choose one TikTok account per post: each needs its own settings."
      : tikTokId
        ? tikTokError
          ? "TikTok's settings for this account could not be read. Untick TikTok, or try again later."
          : tikTokProblem(tikTokForm, tikTokInfo)
        : null;

  function toggle(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function submit() {
    if (mode === "later") {
      const when = new Date(localWhen);
      if (Number.isNaN(when.getTime()) || when.getTime() <= Date.now()) {
        setError("Choose a time in the future, or publish now.");
        return;
      }
    }

    setBusy(true);
    setError(null);
    try {
      await api.social.createPost({
        sourceKind,
        sourceId,
        caption,
        connectionIds: [...selected],
        // datetime-local gives a value with no zone. new Date() reads it as
        // local time, and toISOString converts to UTC — which is exactly the
        // conversion we want, done by the browser that knows the zone.
        scheduledForUtc:
          mode === "later" && localWhen ? new Date(localWhen).toISOString() : null,
        // Updates only. Offers and events need fields this dialog does not
        // collect, and the server refuses them without.
        gbpOptions: gbpSelected ? { postType: "Update" } : null,
        tikTokOptions: tikTokId && tikTokInfo ? toTikTokOptions(tikTokForm, tikTokInfo) : null,
      });

      setDone(true);
      onPublished?.();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not queue this post.");
    } finally {
      setBusy(false);
    }
  }

  if (!open) return null;

  const canSubmit =
    selected.size > 0 &&
    !busy &&
    caption.length <= CAPTION_LIMIT &&
    tikTokBlocker === null &&
    !xTooLong &&
    (mode === "now" || localWhen.length > 0);

  return (
    <Dialog open onOpenChange={(v) => !v && onClose()}>
      <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Publish {sourceName}</DialogTitle>
          <DialogDescription>
            Choose where this goes and when.
          </DialogDescription>
        </DialogHeader>

        {done ? (
          <div className="space-y-4 py-2">
            <div className="flex items-start gap-3 rounded-md bg-primary/10 p-3">
              <Sparkles className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
              <div className="text-sm">
                <p className="font-medium">Queued</p>
                <p className="mt-0.5 text-xs text-muted-foreground">
                  {mode === "now"
                    ? "It will go out within the next minute or so. Track it under Social & Ads."
                    : "It will go out at the time you chose."}
                </p>
              </div>
            </div>
            <div className="flex justify-end gap-2">
              <Button variant="outline" size="sm" onClick={onClose}>
                Close
              </Button>
              <Button asChild size="sm">
                <Link href="/social?tab=scheduled">View scheduled posts</Link>
              </Button>
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            {/* ---- accounts ---- */}
            <section className="space-y-2">
              <Label className="text-xs">Publish to</Label>

              {connections === null ? (
                <p className="text-xs text-muted-foreground">Loading accounts…</p>
              ) : publishable.length === 0 ? (
                <div className="rounded-md border border-border/60 p-4 text-center">
                  <p className="text-sm font-medium">No accounts available</p>
                  <p className="mt-1 text-xs text-muted-foreground">
                    {isVideo
                      ? "Connect Facebook, Instagram, YouTube, LinkedIn, TikTok or X to publish video."
                      : "Connect Facebook, Instagram, LinkedIn or X first."}
                  </p>
                  <Button asChild size="sm" variant="outline" className="mt-3">
                    <Link href="/social">Connect an account</Link>
                  </Button>
                </div>
              ) : (
                <ul className="space-y-1.5">
                  {publishable.map((c) => (
                    <li key={c.id}>
                      <button
                        type="button"
                        onClick={() => toggle(c.id)}
                        className={cn(
                          "flex w-full items-center gap-3 rounded-md border p-2.5 text-left transition-colors",
                          selected.has(c.id)
                            ? "border-primary bg-primary/5"
                            : "border-border/60 hover:bg-accent/50",
                        )}
                      >
                        <input
                          type="checkbox"
                          readOnly
                          checked={selected.has(c.id)}
                          className="h-3.5 w-3.5 accent-primary"
                          tabIndex={-1}
                        />
                        <PlatformIcon platform={c.platform} className="h-4 w-4" />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-sm font-medium">
                            {c.displayName}
                          </span>
                          <span className="block text-[11px] text-muted-foreground">
                            {c.platformName}
                          </span>
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}

              {/* Stated rather than silently omitted — a missing account reads
                  as a bug, an explained one reads as a rule. */}
              {excludedForVideo.length > 0 && (
                <p className="text-[11px] text-muted-foreground">
                  {excludedForVideo.map((c) => c.platformName).join(", ")} cannot
                  publish video, so {excludedForVideo.length === 1 ? "it is" : "they are"} not
                  listed.
                </p>
              )}

              {needsReauth.length > 0 && (
                <p className="flex items-start gap-1.5 text-[11px] text-amber-700 dark:text-amber-400">
                  <AlertTriangle className="mt-0.5 h-3 w-3 shrink-0" />
                  <span>
                    {needsReauth.map((c) => c.displayName).join(", ")} needs
                    reconnecting.{" "}
                    <Link href="/social" className="underline">
                      Fix it
                    </Link>
                  </span>
                </p>
              )}
            </section>

            {/* ---- TikTok ---- */}
            {tikTokId && (
              <TikTokPostSettings
                info={tikTokInfo}
                loading={tikTokLoading}
                error={tikTokError}
                form={tikTokForm}
                onChange={setTikTokForm}
              />
            )}

            {/* ---- caption ---- */}
            <section className="space-y-1.5">
              <div className="flex items-baseline justify-between">
                <Label htmlFor="publish-caption" className="text-xs">
                  Caption
                </Label>
                <span
                  className={cn(
                    "text-[11px]",
                    caption.length > CAPTION_LIMIT
                      ? "text-destructive"
                      : "text-muted-foreground",
                  )}
                >
                  {caption.length} / {CAPTION_LIMIT}
                </span>
              </div>
              <Textarea
                id="publish-caption"
                value={caption}
                onChange={(e) => setCaption(e.target.value)}
                rows={4}
                placeholder="What would you like to say about this?"
              />
              {xSelected && (
                <p
                  className={cn(
                    "text-[11px]",
                    xTooLong ? "text-destructive" : "text-muted-foreground",
                  )}
                >
                  X: {xLength} / {X_MAX_WEIGHTED_LENGTH}
                  {xTooLong
                    ? " — too long for X. Shorten it, or untick X and post there separately."
                    : " (X counts each link as 23 and each emoji as 2)."}
                </p>
              )}
              {youtubeSelected && (
                <p className="text-[11px] text-muted-foreground">
                  On YouTube the first line becomes the video title (up to 100 characters) and the
                  whole caption its description.
                </p>
              )}
              <p className="text-[11px] text-muted-foreground">
                Avoid naming prescription-only medicines — advertising them to
                the public is not permitted, and platforms reject the post.
              </p>
            </section>

            {/* ---- when ---- */}
            <section className="space-y-2">
              <Label className="text-xs">When</Label>
              <div className="flex gap-1.5">
                <Button
                  type="button"
                  size="sm"
                  variant={mode === "now" ? "default" : "outline"}
                  className="flex-1 gap-1.5 text-xs"
                  onClick={() => setMode("now")}
                >
                  <Send className="h-3.5 w-3.5" />
                  Publish now
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant={mode === "later" ? "default" : "outline"}
                  className="flex-1 gap-1.5 text-xs"
                  onClick={() => setMode("later")}
                >
                  <CalendarClock className="h-3.5 w-3.5" />
                  Schedule
                </Button>
              </div>

              {mode === "later" && (
                <div className="space-y-1">
                  <Input
                    type="datetime-local"
                    value={localWhen}
                    min={localDateTimeValue(new Date(Date.now() + 60_000))}
                    onChange={(e) => setLocalWhen(e.target.value)}
                    className="h-9"
                  />
                  <p className="text-[11px] text-muted-foreground">
                    Your local time. Stored as UTC, so it stays correct when the
                    clocks change.
                  </p>
                </div>
              )}
            </section>

            {/* Beside the button that was refused, not at the top of a
                scrolling dialog where it would be out of sight. */}
            {error && (
              <p role="alert" className="rounded-md bg-destructive/10 px-3 py-2 text-xs text-destructive">
                {error}
              </p>
            )}
            {!error && tikTokBlocker && selected.size > 0 && (
              <p className="text-[11px] text-amber-700 dark:text-amber-400">{tikTokBlocker}</p>
            )}

            <div className="flex justify-end gap-2 pt-1">
              <Button variant="outline" size="sm" onClick={onClose}>
                Cancel
              </Button>
              <Button size="sm" disabled={!canSubmit} onClick={submit}>
                {busy
                  ? "Queuing…"
                  : mode === "now"
                    ? `Publish to ${selected.size || 0}`
                    : "Schedule"}
              </Button>
            </div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

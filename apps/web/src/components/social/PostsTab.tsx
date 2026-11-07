"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, CalendarClock, ExternalLink, Inbox, RefreshCw, X } from "lucide-react";
import type { ScheduledPost } from "@acme/shared-types";
import { describePublishStatus, publishStatusTone } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { PlatformIcon } from "./PlatformIcon";

const PAGE_SIZE = 100;

/** How far ahead a scheduled post counts as "about to go". */
const SOON_MS = 2 * 60_000;

function isAboutToMove(p: ScheduledPost): boolean {
  if (p.status === "Publishing") return true;
  if (p.status !== "Scheduled") return false;
  const due = p.nextAttemptAtUtc ?? p.scheduledForUtc;
  return !due || new Date(due).getTime() - Date.now() < SOON_MS;
}

const TONE = {
  neutral: "bg-muted text-muted-foreground",
  good: "bg-primary/10 text-primary",
  warn: "bg-amber-500/15 text-amber-700 dark:text-amber-400",
  bad: "bg-destructive/10 text-destructive",
} as const;

/**
 * Scheduled and published posts.
 *
 * One list, filtered — rather than separate "scheduled" and "history" screens —
 * because a post moves between those states on its own and a pharmacy looking
 * for "the one I queued this morning" should not have to guess which screen it
 * ended up on.
 *
 * Per-target results are always shown. A post that reached Facebook and was
 * refused by Instagram is not a failure or a success, and collapsing it to
 * either loses the only detail worth reading.
 */
export function PostsTab({ filter }: { filter: "upcoming" | "history" }) {
  const [posts, setPosts] = useState<ScheduledPost[] | null>(null);
  const [total, setTotal] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      // Filtered on the server. Taking a page of everything and filtering
      // here let a long history push this week's scheduled posts off the end.
      const page = await api.social.posts(undefined, 0, PAGE_SIZE, filter);
      setPosts(page.items);
      setTotal(page.total);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load posts.");
    }
  }, [filter]);

  useEffect(() => {
    setPosts(null);
    void load();
  }, [load]);

  // Refreshes itself only while something is about to change on its own:
  // publishing now, or due within the next couple of minutes. A post
  // scheduled for next Tuesday does not need checking every ten seconds, and
  // a hidden tab does not need checking at all.
  useEffect(() => {
    if (!posts?.some(isAboutToMove)) return;
    const timer = setInterval(() => {
      if (document.visibilityState === "visible") void load();
    }, 10_000);
    return () => clearInterval(timer);
  }, [posts, load]);

  async function act(id: string, fn: () => Promise<unknown>) {
    setBusy(id);
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

  function retry(post: ScheduledPost) {
    // An uncertain target may already be live. Retrying it is the pharmacy's
    // call, made after looking — so ask them to look.
    const uncertain = post.targets.filter((t) => t.status === "Uncertain");
    if (
      uncertain.length > 0 &&
      !window.confirm(
        `${uncertain.map((t) => t.accountName).join(", ")} may already have this post. ` +
          "Check the account first — retrying will post it again if it did go out. Retry anyway?",
      )
    )
      return;
    void act(post.id, () => api.social.retryPost(post.id));
  }

  if (posts === null) {
    return error ? (
      <div className="flex flex-col items-center gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-6 py-10 text-center">
        <AlertTriangle className="h-5 w-5 text-destructive" aria-hidden />
        <p className="text-sm text-destructive">{error}</p>
        <Button size="sm" variant="outline" onClick={() => void load()}>
          Try again
        </Button>
      </div>
    ) : (
      <p className="p-6 text-sm text-muted-foreground">Loading…</p>
    );
  }

  if (posts.length === 0) {
    return (
      <div className="flex flex-col items-center gap-2 rounded-lg border border-border/60 bg-card px-6 py-12 text-center">
        <div className="rounded-full bg-muted p-3">
          <Inbox className="h-5 w-5 text-muted-foreground" aria-hidden />
        </div>
        <p className="text-sm font-medium">
          {filter === "upcoming" ? "Nothing scheduled" : "Nothing published yet"}
        </p>
        <p className="max-w-sm text-xs text-muted-foreground">
          Open a poster or video and use Publish to send it to your connected
          accounts.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {error && (
        <p className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive">
          {error}
        </p>
      )}

      {posts.map((post) => (
        <article key={post.id} className="rounded-lg border border-border/60 bg-card">
          <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border/40 px-4 py-3">
            <div className="min-w-0">
              <p className="truncate text-sm font-medium">
                {post.sourceName}
                {post.seriesId && (
                  <span className="ml-1.5 rounded bg-primary/10 px-1.5 py-0.5 text-[10px] font-normal text-primary">
                    Recurring
                  </span>
                )}
              </p>
              <p className="mt-0.5 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                <span
                  className={cn(
                    "rounded-full px-2 py-0.5 text-[11px] font-medium",
                    TONE[publishStatusTone(post.status)],
                  )}
                >
                  {describePublishStatus(post.status)}
                </span>
                {post.scheduledForUtc ? (
                  <span className="flex items-center gap-1">
                    <CalendarClock className="h-3 w-3" />
                    {new Date(post.scheduledForUtc).toLocaleString()}
                  </span>
                ) : (
                  <span>Queued immediately</span>
                )}
                {post.nextAttemptAtUtc && (
                  <span className="flex items-center gap-1 text-amber-700 dark:text-amber-400">
                    <RefreshCw className="h-3 w-3" />
                    Trying again at{" "}
                    {new Date(post.nextAttemptAtUtc).toLocaleTimeString([], {
                      hour: "2-digit",
                      minute: "2-digit",
                    })}
                  </span>
                )}
              </p>
            </div>

            <div className="flex gap-1.5">
              {post.canRetry && (
                <Button
                  size="sm"
                  variant="outline"
                  className="h-7 gap-1 text-xs"
                  disabled={busy === post.id}
                  onClick={() => retry(post)}
                >
                  <RefreshCw className="h-3.5 w-3.5" />
                  Retry
                </Button>
              )}
              {post.canCancel && (
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-7 gap-1 text-xs text-muted-foreground"
                  disabled={busy === post.id}
                  onClick={() => act(post.id, () => api.social.cancelPost(post.id))}
                >
                  <X className="h-3.5 w-3.5" />
                  Cancel
                </Button>
              )}
            </div>
          </header>

          {post.caption && (
            <p className="border-b border-border/40 px-4 py-2 text-xs text-muted-foreground">
              {post.caption}
            </p>
          )}

          <ul className="divide-y divide-border/30">
            {post.targets.map((t) => (
              <li key={t.connectionId} className="flex items-start gap-3 px-4 py-2">
                <PlatformIcon platform={t.platform} className="mt-0.5 h-4 w-4 shrink-0" />

                <div className="min-w-0 flex-1">
                  <p className="truncate text-xs font-medium">{t.accountName}</p>
                  {t.error && (
                    <p
                      className={cn(
                        "mt-0.5 text-[11px]",
                        t.status === "Skipped"
                          ? "text-muted-foreground"
                          : t.status === "Uncertain" || t.status === "Pending"
                            ? "text-amber-700 dark:text-amber-400"
                            : "text-destructive",
                      )}
                    >
                      {t.error}
                      {t.attemptCount > 1 && ` (attempt ${t.attemptCount})`}
                    </p>
                  )}
                </div>

                {t.permalink ? (
                  <a
                    href={t.permalink}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="flex items-center gap-1 text-[11px] text-primary hover:underline"
                  >
                    View <ExternalLink className="h-3 w-3" />
                  </a>
                ) : (
                  <span className="text-[11px] text-muted-foreground">
                    {t.status === "Uncertain" ? "Check account" : t.status}
                  </span>
                )}
              </li>
            ))}
          </ul>
        </article>
      ))}

      {total > posts.length && (
        <p className="text-center text-xs text-muted-foreground">
          Showing the {posts.length} most recent of {total}.
        </p>
      )}
    </div>
  );
}

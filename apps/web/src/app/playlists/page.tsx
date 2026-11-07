"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import {
  ArrowDown,
  ArrowUp,
  Film,
  Loader2,
  Monitor,
  Plus,
  Trash2,
  TriangleAlert,
  X,
} from "lucide-react";
import {
  PLAYLIST_DEFAULT_SECONDS,
  PLAYLIST_MAX_ITEMS,
  PLAYLIST_MAX_SECONDS,
  PLAYLIST_MAX_VIDEOS,
  PLAYLIST_MIN_SECONDS,
  type Playlist,
  type PlaylistItem,
  type PlaylistItemKind,
  type PosterSummary,
  type Video,
} from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api";
import {
  canAddToPlaylist,
  formatClock,
  screenReadyVideos,
  videoCount,
} from "@/lib/playlist-items";

/**
 * Playlists: what a screen loops, and for how long each poster holds it.
 *
 * A playlist is edited whole and saved whole. The alternative — add, move and
 * remove each hitting the server — needs a concurrency story for every one of
 * them, and buys nothing here: these are short lists, edited by one person
 * standing at one counter.
 *
 * Only published posters can be added, because a screen in a shop window *is*
 * publication. A poster that is later unpublished stays in the list, struck
 * through, rather than vanishing — the loop getting shorter with no explanation
 * is the confusing outcome, not the honest one.
 *
 * Finished videos can be added too (from My Videos, Ready only, a few per
 * playlist). A video plays once, muted, for its own length — so it has no
 * seconds box. One that is later deleted, fails, or is being re-cut stays in
 * the list, marked, and the screen skips it until it is Ready again.
 */
export default function PlaylistsPage() {
  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  const [posters, setPosters] = useState<PosterSummary[]>([]);
  const [videos, setVideos] = useState<Video[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [newName, setNewName] = useState("");

  const load = useCallback(async () => {
    try {
      const [lists, posterPage, videoList] = await Promise.all([
        api.displays.listPlaylists(),
        api.listPosters({ take: 100 }),
        // Videos are an extra, not the page. If they will not load, the
        // playlists and posters still should.
        api.listVideos().catch(() => [] as Video[]),
      ]);
      setPlaylists(lists);
      setPosters(posterPage.items.filter((p) => p.status === "Published"));
      setVideos(screenReadyVideos(videoList));
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load playlists.");
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

  return (
    <main className="container max-w-4xl py-8">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Playlists</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Published posters and finished videos, in the order your screens show them.
          </p>
        </div>
        <Button asChild variant="outline" size="sm" className="gap-1.5">
          <Link href="/displays">
            <Monitor className="h-4 w-4" />
            Screens
          </Link>
        </Button>
      </header>

      {error && (
        <p
          role="alert"
          className="mt-4 rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {error}
        </p>
      )}

      <form
        className="mt-5 flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          void run("new", async () => {
            await api.displays.createPlaylist({ name: newName });
            setNewName("");
          });
        }}
      >
        <Input
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          placeholder="New playlist name — e.g. Winter vaccinations"
          aria-label="New playlist name"
        />
        <Button type="submit" size="sm" className="shrink-0 gap-1.5" disabled={busy === "new" || !newName.trim()}>
          <Plus className="h-4 w-4" />
          Create
        </Button>
      </form>

      {posters.length === 0 && videos.length === 0 && !loading && (
        <p className="mt-4 flex items-start gap-2 rounded-md bg-muted/60 p-3 text-xs text-muted-foreground">
          <TriangleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
          <span>
            You have no published posters or finished videos yet. A screen only shows
            published work —{" "}
            <Link href="/posters" className="underline">
              publish a poster
            </Link>{" "}
            or{" "}
            <Link href="/videos" className="underline">
              make a video
            </Link>{" "}
            and it will appear here.
          </span>
        </p>
      )}

      {loading ? (
        <p className="mt-8 flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          Loading playlists…
        </p>
      ) : playlists.length === 0 ? (
        <p className="mt-8 rounded-lg border border-dashed border-border/60 p-10 text-center text-sm text-muted-foreground">
          No playlists yet. Create one above, then add posters to it.
        </p>
      ) : (
        <ul className="mt-6 space-y-5">
          {playlists.map((playlist) => (
            <PlaylistCard
              key={playlist.id}
              playlist={playlist}
              posters={posters}
              videos={videos}
              busy={busy}
              onRun={run}
            />
          ))}
        </ul>
      )}
    </main>
  );
}

function PlaylistCard({
  playlist,
  posters,
  videos,
  busy,
  onRun,
}: {
  playlist: Playlist;
  posters: PosterSummary[];
  videos: Video[];
  busy: string | null;
  onRun: (key: string, fn: () => Promise<unknown>) => Promise<void>;
}) {
  // Edited locally and saved in one go, so reordering four posters is four
  // clicks and one request rather than four requests.
  const [items, setItems] = useState<PlaylistItem[]>(playlist.items);
  const [name, setName] = useState(playlist.name);
  const [picking, setPicking] = useState<PlaylistItemKind | null>(null);

  // Re-seed when the server's copy changes underneath us — after a save, or
  // after another screen's edit lands in a reload.
  useEffect(() => {
    setItems(playlist.items);
    setName(playlist.name);
  }, [playlist]);

  const dirty =
    name !== playlist.name ||
    items.length !== playlist.items.length ||
    items.some((item, i) => {
      const original = playlist.items[i];
      return (
        !original ||
        original.kind !== item.kind ||
        original.refId !== item.refId ||
        original.durationSeconds !== item.durationSeconds
      );
    });

  const anyBusy = busy !== null;
  const total = items.reduce((sum, i) => sum + i.durationSeconds, 0);

  function move(index: number, by: number) {
    const next = [...items];
    const target = index + by;
    if (target < 0 || target >= next.length) return;
    [next[index], next[target]] = [next[target], next[index]];
    setItems(next);
  }

  const alreadyIn = new Set(items.map((i) => `${i.kind}:${i.refId}`));
  const videosIn = videoCount(items);
  const full = items.length >= PLAYLIST_MAX_ITEMS;

  function add(kind: PlaylistItemKind, refId: string, itemName: string, seconds: number) {
    setItems((prev) => [
      ...prev,
      { kind, refId, name: itemName, durationSeconds: seconds, isPlayable: true },
    ]);
    setPicking(null);
  }

  return (
    <li className="rounded-lg border border-border/60 p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            aria-label={`Name of playlist ${playlist.name}`}
            className="h-9 max-w-sm font-medium"
          />
          <p className="mt-1.5 text-xs text-muted-foreground">
            {items.length} item{items.length === 1 ? "" : "s"} ·{" "}
            {Math.floor(total / 60)}m {total % 60}s per loop
            {playlist.screenNames.length > 0 && (
              <> · showing on {playlist.screenNames.join(", ")}</>
            )}
          </p>
        </div>

        <div className="flex gap-2">
          {dirty && (
            <Button
              size="sm"
              disabled={anyBusy}
              onClick={() =>
                void onRun(`save-${playlist.id}`, () =>
                  api.displays.updatePlaylist(playlist.id, {
                    name,
                    items: items.map((i) => ({
                      kind: i.kind,
                      refId: i.refId,
                      durationSeconds: i.durationSeconds,
                    })),
                  }),
                )
              }
            >
              {busy === `save-${playlist.id}` ? "Saving…" : "Save"}
            </Button>
          )}
          <Button
            size="sm"
            variant="ghost"
            className="gap-1.5 text-destructive hover:text-destructive"
            disabled={anyBusy}
            onClick={() => {
              const warning =
                playlist.screenNames.length > 0
                  ? `Delete "${playlist.name}"? ${playlist.screenNames.length} screen(s) showing it will go blank until you assign another.`
                  : `Delete "${playlist.name}"?`;
              if (!confirm(warning)) return;
              void onRun(`del-${playlist.id}`, () =>
                api.displays.deletePlaylist(playlist.id),
              );
            }}
          >
            <Trash2 className="h-3.5 w-3.5" />
            Delete
          </Button>
        </div>
      </div>

      {items.length > 0 && (
        <ol className="mt-4 space-y-2">
          {items.map((item, i) => (
            <li
              key={`${item.refId}-${i}`}
              className="flex flex-wrap items-center gap-2 rounded-md border border-border/40 px-3 py-2"
            >
              <span className="w-5 text-xs text-muted-foreground">{i + 1}</span>

              {item.kind === "Video" && (
                <Film className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-label="Video" />
              )}

              <span
                className={
                  item.isPlayable
                    ? "min-w-0 flex-1 truncate text-sm"
                    : "min-w-0 flex-1 truncate text-sm text-muted-foreground line-through"
                }
              >
                {item.name}
              </span>

              {!item.isPlayable && (
                <Badge variant="outline" className="gap-1 text-[10px]">
                  <TriangleAlert className="h-3 w-3" />
                  {item.kind === "Video" ? "Not ready — skipped" : "Not published — skipped"}
                </Badge>
              )}

              {item.kind === "Video" ? (
                // A video plays once, for its own length. The server sets the
                // time; a box here would only offer a number it ignores.
                <span className="w-[6.5rem] text-xs text-muted-foreground">
                  {formatClock(item.durationSeconds)} · plays once
                </span>
              ) : (
                <div className="flex items-center gap-1">
                  <Input
                    type="number"
                    min={PLAYLIST_MIN_SECONDS}
                    max={PLAYLIST_MAX_SECONDS}
                    value={item.durationSeconds}
                    aria-label={`Seconds on screen for ${item.name}`}
                    className="h-8 w-20 text-xs"
                    onChange={(e) => {
                      const seconds = Number(e.target.value);
                      setItems((prev) =>
                        prev.map((p, idx) =>
                          idx === i ? { ...p, durationSeconds: seconds } : p,
                        ),
                      );
                    }}
                  />
                  <span className="text-xs text-muted-foreground">sec</span>
                </div>
              )}

              <div className="flex gap-0.5">
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-8 w-8 p-0"
                  aria-label={`Move ${item.name} earlier`}
                  disabled={i === 0}
                  onClick={() => move(i, -1)}
                >
                  <ArrowUp className="h-3.5 w-3.5" />
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-8 w-8 p-0"
                  aria-label={`Move ${item.name} later`}
                  disabled={i === items.length - 1}
                  onClick={() => move(i, 1)}
                >
                  <ArrowDown className="h-3.5 w-3.5" />
                </Button>
                <Button
                  size="sm"
                  variant="ghost"
                  className="h-8 w-8 p-0 text-destructive hover:text-destructive"
                  aria-label={`Remove ${item.name}`}
                  onClick={() => setItems((prev) => prev.filter((_, idx) => idx !== i))}
                >
                  <X className="h-3.5 w-3.5" />
                </Button>
              </div>
            </li>
          ))}
        </ol>
      )}

      <div className="mt-3">
        {picking === "Poster" ? (
          <Picker title="Add a published poster" onCancel={() => setPicking(null)}>
            {posters.map((poster) => (
              <PickRow
                key={poster.id}
                label={poster.name}
                note={alreadyIn.has(`Poster:${poster.id}`) ? "already in this loop" : null}
                // The server refuses the same item twice in a row, because on
                // a wall it looks like the screen has frozen. Disabling the
                // last-added one says so before the save fails.
                disabled={!canAddToPlaylist(items, "Poster", poster.id)}
                onPick={() => add("Poster", poster.id, poster.name, PLAYLIST_DEFAULT_SECONDS)}
              />
            ))}
          </Picker>
        ) : picking === "Video" ? (
          <Picker title="Add a finished video from My Videos" onCancel={() => setPicking(null)}>
            {videos.map((video) => (
              <PickRow
                key={video.id}
                label={video.name}
                note={[
                  formatClock(video.durationSeconds),
                  alreadyIn.has(`Video:${video.id}`) ? "already in this loop" : null,
                ].filter(Boolean).join(" · ")}
                disabled={!canAddToPlaylist(items, "Video", video.id)}
                onPick={() => add(
                  "Video", video.id, video.name,
                  Math.min(PLAYLIST_MAX_SECONDS, Math.max(PLAYLIST_MIN_SECONDS, Math.round(video.durationSeconds))),
                )}
              />
            ))}
          </Picker>
        ) : (
          <div className="flex flex-wrap items-center gap-2">
            <Button
              size="sm"
              variant="outline"
              className="gap-1.5"
              disabled={posters.length === 0 || full}
              onClick={() => setPicking("Poster")}
            >
              <Plus className="h-3.5 w-3.5" />
              Add poster
            </Button>
            <Button
              size="sm"
              variant="outline"
              className="gap-1.5"
              disabled={videos.length === 0 || full || videosIn >= PLAYLIST_MAX_VIDEOS}
              onClick={() => setPicking("Video")}
              title={videos.length === 0 ? "No finished videos yet" : undefined}
            >
              <Film className="h-3.5 w-3.5" />
              Add video
            </Button>
            {full ? (
              <span className="text-xs text-muted-foreground">
                Limit of {PLAYLIST_MAX_ITEMS} items reached
              </span>
            ) : videosIn >= PLAYLIST_MAX_VIDEOS ? (
              <span className="text-xs text-muted-foreground">
                Up to {PLAYLIST_MAX_VIDEOS} videos per playlist
              </span>
            ) : null}
          </div>
        )}
      </div>
    </li>
  );
}

function Picker({
  title,
  onCancel,
  children,
}: {
  title: string;
  onCancel: () => void;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-md border border-border/60 p-3">
      <p className="text-xs font-medium">{title}</p>
      <ul className="mt-2 max-h-56 space-y-1 overflow-y-auto">{children}</ul>
      <Button size="sm" variant="ghost" className="mt-2 h-7 text-xs" onClick={onCancel}>
        Cancel
      </Button>
    </div>
  );
}

function PickRow({
  label,
  note,
  disabled,
  onPick,
}: {
  label: string;
  note: string | null;
  disabled: boolean;
  onPick: () => void;
}) {
  return (
    <li>
      <button
        type="button"
        className="w-full rounded px-2 py-1.5 text-left text-sm hover:bg-muted disabled:opacity-40"
        disabled={disabled}
        onClick={onPick}
      >
        {label}
        {note && <span className="ml-2 text-xs text-muted-foreground">{note}</span>}
      </button>
    </li>
  );
}

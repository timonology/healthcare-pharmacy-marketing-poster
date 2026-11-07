"use client";

import { useEffect, useState } from "react";
import { Check, Image as ImageIcon, Loader2 } from "lucide-react";
import {
  MAX_SHOTS_PER_CHANGE,
  MAX_SHOT_DESCRIPTION_CHARS,
  type VideoShot,
} from "@acme/shared-types";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";
import { shotLabel } from "@/lib/video-refine";

/**
 * Every shot of a finished video, as tiles. Tap the ones to change; say what
 * each should show.
 *
 * <b>The pharmacist chooses the shot. Nothing here, or behind it, guesses.</b>
 * The shot used to be inferred from a sentence by a model reading stock-library
 * titles, and it refused "remove the last shot which shows medicine" because
 * the title said "employees on a couch" — the pharmacist had watched the video.
 * A tile is the shot; `shows` is alt text and nothing more.
 *
 * `selected` maps a tapped shot's index to what it should show (possibly "",
 * which the worker reads as "the video's own subject").
 */
export function ShotPicker({
  videoId,
  version,
  selected,
  onChange,
  disabled,
}: {
  videoId: string;
  /**
   * Changes whenever the video is re-cut (its `updatedAtUtc`). Added to each
   * still's URL: a re-cut writes new stills under the same keys, and without
   * it the browser shows the previous render's stills from its cache.
   */
  version?: string;
  selected: ReadonlyMap<number, string>;
  onChange: (next: Map<number, string>) => void;
  disabled?: boolean;
}) {
  const [shots, setShots] = useState<VideoShot[] | null>(null);
  const [failed, setFailed] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    setShots(null);
    setFailed(null);
    api.listVideoShots(videoId)
      .then((list) => { if (live) setShots(list); })
      .catch((err: unknown) => {
        if (live) setFailed(err instanceof Error ? err.message : "The shots could not be loaded.");
      });
    return () => { live = false; };
  }, [videoId]);

  function toggle(index: number) {
    const next = new Map(selected);
    if (next.has(index)) next.delete(index);
    else if (next.size < MAX_SHOTS_PER_CHANGE) next.set(index, "");
    onChange(next);
  }

  function describe(index: number, show: string) {
    const next = new Map(selected);
    next.set(index, show);
    onChange(next);
  }

  if (failed) {
    return <p className="text-xs text-muted-foreground">{failed}</p>;
  }
  if (shots === null) {
    return (
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Loader2 className="h-3.5 w-3.5 animate-spin" /> Loading the shots…
      </p>
    );
  }
  if (shots.length === 0) {
    return (
      <p className="text-xs text-muted-foreground">
        This video&apos;s shots are not on record, so one cannot be picked. Making a new
        video will record them.
      </p>
    );
  }

  const full = selected.size >= MAX_SHOTS_PER_CHANGE;

  return (
    <div className="space-y-3">
      <div className="grid grid-cols-3 gap-2 sm:grid-cols-4" role="group" aria-label="Shots in this video">
        {shots.map((shot, at) => {
          const on = selected.has(shot.index);
          const label = shotLabel(shot, shots.length, at);
          return (
            <button
              key={shot.index}
              type="button"
              onClick={() => toggle(shot.index)}
              disabled={disabled || (!on && full)}
              aria-pressed={on}
              aria-label={`${label}${shot.isPhoto ? " · your photo" : ""}: ${shot.caption}`}
              title={shot.caption}
              className={[
                "group relative aspect-[9/16] overflow-hidden rounded-md border text-left transition",
                on ? "border-primary ring-2 ring-primary" : "border-border hover:border-primary/60",
                "disabled:cursor-not-allowed disabled:opacity-50",
              ].join(" ")}
            >
              {shot.hasThumbnail ? (
                // eslint-disable-next-line @next/next/no-img-element
                <img
                  src={`/api/videos/${videoId}/shots/${shot.index}/thumbnail${
                    version ? `?v=${encodeURIComponent(version)}` : ""}`}
                  alt={shot.shows ?? ""}
                  loading="lazy"
                  className="h-full w-full object-cover"
                />
              ) : (
                <span className="flex h-full w-full items-center justify-center bg-muted text-lg font-semibold text-muted-foreground">
                  {shot.index + 1}
                </span>
              )}
              <span className="absolute inset-x-0 bottom-0 bg-black/60 px-1 py-0.5 text-[10px] leading-tight text-white">
                {label}
              </span>
              {shot.isPhoto && (
                <span className="absolute left-1 top-1 flex items-center gap-0.5 rounded bg-black/60 px-1 text-[10px] text-white">
                  <ImageIcon className="h-2.5 w-2.5" /> Your photo
                </span>
              )}
              {on && (
                <span className="absolute right-1 top-1 rounded-full bg-primary p-0.5 text-primary-foreground">
                  <Check className="h-3 w-3" />
                </span>
              )}
            </button>
          );
        })}
      </div>

      {[...selected.keys()].sort((a, b) => a - b).map((index) => {
        const at = shots.findIndex((s) => s.index === index);
        const shot = shots[at];
        if (!shot) return null;
        return (
          <label key={index} className="block space-y-1 text-xs">
            <span className="font-medium">
              {shotLabel(shot, shots.length, at)} should show
              {shot.isPhoto ? " (replaces your photo here)" : ""}
            </span>
            <Input
              value={selected.get(index) ?? ""}
              onChange={(e) => describe(index, e.target.value)}
              maxLength={MAX_SHOT_DESCRIPTION_CHARS}
              placeholder="e.g. a person stepping on a scale — leave empty for something on this video's topic"
              disabled={disabled}
              aria-label={`What ${shotLabel(shot, shots.length, at)} should show`}
            />
          </label>
        );
      })}

      {full && (
        <p className="text-xs text-muted-foreground">
          Up to {MAX_SHOTS_PER_CHANGE} shots at a time.
        </p>
      )}
    </div>
  );
}

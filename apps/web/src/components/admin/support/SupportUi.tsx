"use client";

import type {
  CampaignStatus,
  PosterStatus,
  VideoStatus,
} from "@acme/shared-types";
import { cn } from "@/lib/utils";

/**
 * Small shared pieces for the pharmacy support view.
 *
 * The status pills live here rather than in each tab so that "Failed" is the
 * same red everywhere. An administrator scanning three tabs for the cause of a
 * complaint should be able to spot trouble by colour without re-reading the
 * legend on each one.
 */

const TONE = {
  neutral: "bg-muted text-muted-foreground",
  good: "bg-primary/10 text-primary",
  warn: "bg-amber-500/15 text-amber-700 dark:text-amber-400",
  bad: "bg-destructive/10 text-destructive",
} as const;

type Tone = keyof typeof TONE;

function Pill({ tone, children }: { tone: Tone; children: React.ReactNode }) {
  return (
    <span
      className={cn(
        "inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium",
        TONE[tone],
      )}
    >
      {children}
    </span>
  );
}

// Exhaustive by type, which is why adding Queued to CampaignStatus failed the
// build here rather than rendering an admin row with no badge.
const CAMPAIGN_TONE: Record<CampaignStatus, Tone> = {
  Draft: "neutral",
  // Waiting for the dispatch worker, normally a second or two. Neutral rather
  // than warn: nothing is wrong, and support seeing amber on every campaign in
  // the moments after it is created would learn to ignore the colour.
  Queued: "neutral",
  Sending: "warn",
  Sent: "good",
  Stopped: "warn",
  Failed: "bad",
};

export function CampaignStatusPill({ status }: { status: CampaignStatus }) {
  return <Pill tone={CAMPAIGN_TONE[status] ?? "neutral"}>{status}</Pill>;
}

const POSTER_TONE: Record<PosterStatus, Tone> = {
  Draft: "neutral",
  Published: "good",
  Archived: "warn",
};

export function PosterStatusPill({ status }: { status: PosterStatus }) {
  return <Pill tone={POSTER_TONE[status] ?? "neutral"}>{status}</Pill>;
}

const VIDEO_TONE: Record<VideoStatus, Tone> = {
  Pending: "neutral",
  Rendering: "warn",
  Ready: "good",
  Failed: "bad",
};

export function VideoStatusPill({ status }: { status: VideoStatus }) {
  return <Pill tone={VIDEO_TONE[status] ?? "neutral"}>{status}</Pill>;
}

/**
 * A masked contact value.
 *
 * Rendered in a monospace face on purpose: the bullets line up, so a column of
 * them reads as a deliberate redaction rather than as text that failed to
 * load. `title` says so out loud for anyone who hovers, and for screen
 * readers.
 */
export function Masked({ value }: { value: string | null }) {
  if (!value) return <span className="text-muted-foreground">—</span>;
  return (
    <span
      className="font-mono text-xs text-muted-foreground"
      title="Hidden. Use “Reveal” to see the full details — it is recorded in the audit log."
    >
      {value}
    </span>
  );
}

/** Label/value pair for the overview grid. */
export function Fact({
  label,
  value,
}: {
  label: string;
  value: React.ReactNode;
}) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="mt-0.5 text-sm font-medium">{value}</dd>
    </div>
  );
}

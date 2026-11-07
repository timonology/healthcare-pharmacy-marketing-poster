"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import {
  AlertTriangle,
  CheckCircle2,
  Clock,
  Pause,
  Pencil,
  Play,
  Plus,
  Repeat,
  Square,
  XCircle,
} from "lucide-react";
import {
  RECURRING_PLATFORMS,
  type PublishStatus,
  type RecurringSeries,
  type RecurringSeriesList,
  type SocialConnection,
} from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { MetaConnectButton } from "./ConnectionsTab";
import { PlatformIcon } from "./PlatformIcon";
import { RecurringDialog } from "./RecurringDialog";
import { SeriesCalendarStrip } from "./SeriesCalendarStrip";

/**
 * Social & Ads → Recurring: every series, what is coming, and the controls.
 */
export function RecurringTab() {
  const [data, setData] = useState<RecurringSeriesList | null>(null);
  const [connections, setConnections] = useState<SocialConnection[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<RecurringSeries | null>(null);

  const load = useCallback(async () => {
    try {
      const [list, conns] = await Promise.all([api.social.series(), api.social.connections()]);
      setData(list);
      setConnections(conns);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load your recurring posts.");
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  async function act(series: RecurringSeries, action: "pause" | "resume" | "end") {
    if (action === "end"
        && !window.confirm(`End "${series.name}"? Posts already sent stay. It cannot be restarted — you would set up a new one.`))
      return;
    setBusyId(series.id);
    setError(null);
    try {
      if (action === "pause") await api.social.pauseSeries(series.id);
      else if (action === "resume") await api.social.resumeSeries(series.id);
      else await api.social.endSeries(series.id);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "That did not work. Try again.");
    } finally {
      setBusyId(null);
    }
  }

  if (data === null || connections === null) {
    return error
      ? <ErrorNote message={error} />
      : <p className="text-sm text-muted-foreground">Loading…</p>;
  }

  const hasAccount = connections.some(
    (c) => RECURRING_PLATFORMS.includes(c.platform) && c.status !== "Revoked",
  );
  const planAllows = data.maxActive > 0;
  const atLimit = data.activeCount >= data.maxActive;
  const active = data.items.filter((s) => s.status === "Active");
  const zone = active[0]?.timeZoneId ?? data.items[0]?.timeZoneId ?? "Europe/London";

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <p className="text-sm font-medium">Recurring posts</p>
          <p className="text-xs text-muted-foreground">
            {planAllows
              ? `${data.activeCount} of ${data.maxActive} active on your plan`
              : "Not included in your plan"}
          </p>
        </div>
        {planAllows && hasAccount && (
          <Button
            size="sm"
            disabled={atLimit}
            title={atLimit ? "Pause or end one first, or upgrade for more." : undefined}
            onClick={() => setCreating(true)}
          >
            <Plus className="mr-1 h-4 w-4" /> New recurring post
          </Button>
        )}
      </header>

      {!planAllows && (
        <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-primary/30 bg-primary/5 p-4">
          <p className="text-sm">{data.planReason ?? "Recurring posts are on the Starter and Pro plans."}</p>
          <Button asChild size="sm">
            <Link href="/pricing">See plans</Link>
          </Button>
        </div>
      )}

      {planAllows && !hasAccount && (
        <div className="rounded-lg border border-dashed p-8 text-center">
          <Repeat className="mx-auto h-6 w-6 text-muted-foreground" />
          <p className="mt-2 text-sm font-medium">Connect an account first</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Recurring posts go to your Facebook Page, Instagram, YouTube and LinkedIn. Connect an account, then set one up here.
          </p>
          <MetaConnectButton className="mt-4 flex flex-col items-center" />
        </div>
      )}

      {planAllows && hasAccount && data.items.length === 0 && (
        <div className="rounded-lg border border-dashed p-8 text-center">
          <Repeat className="mx-auto h-6 w-6 text-muted-foreground" />
          <p className="mt-2 text-sm font-medium">No recurring posts yet</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Pick a poster or video, choose where it goes and how often, and it posts itself.
          </p>
          <Button size="sm" className="mt-4" onClick={() => setCreating(true)}>
            <Plus className="mr-1 h-4 w-4" /> New recurring post
          </Button>
        </div>
      )}

      {active.length > 0 && (
        <SeriesCalendarStrip
          timeZoneId={zone}
          series={active.map((s) => ({ id: s.id, name: s.name, upcoming: s.upcoming }))}
        />
      )}

      {data.items.length > 0 && (
        <ul className="space-y-3">
          {data.items.map((s) => (
            <SeriesCard
              key={s.id}
              series={s}
              busy={busyId === s.id}
              canResume={!atLimit}
              onEdit={() => setEditing(s)}
              onPause={() => act(s, "pause")}
              onResume={() => act(s, "resume")}
              onEnd={() => act(s, "end")}
            />
          ))}
        </ul>
      )}

      <RecurringDialog open={creating} onClose={() => setCreating(false)} onSaved={() => void load()} />
      <RecurringDialog
        open={editing !== null}
        editing={editing}
        onClose={() => setEditing(null)}
        onSaved={() => void load()}
      />
    </div>
  );
}

function SeriesCard({
  series: s,
  busy,
  canResume,
  onEdit,
  onPause,
  onResume,
  onEnd,
}: {
  series: RecurringSeries;
  busy: boolean;
  canResume: boolean;
  onEdit: () => void;
  onPause: () => void;
  onResume: () => void;
  onEnd: () => void;
}) {
  const image = s.sourceKind === "Video" ? `/api/videos/${s.sourceId}/thumbnail` : `/api/posters/${s.sourceId}/card`;
  const broken = s.destinations.filter((d) => d.needsReconnect);

  return (
    <li className={cn("rounded-lg border bg-card p-4", s.status === "Ended" && "opacity-70")}>
      <div className="flex gap-4">
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src={image} alt="" className="h-20 w-16 shrink-0 rounded bg-muted object-cover" loading="lazy" />

        <div className="min-w-0 flex-1 space-y-1.5">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="truncate text-sm font-semibold">{s.name}</h3>
            <StatusBadge status={s.status} />
          </div>

          <p className="text-sm">{s.cadenceText}</p>

          <p className="text-xs text-muted-foreground">
            {s.status === "Active" && s.nextLabel
              ? <>Next post: <span className="font-medium text-foreground">{s.nextLabel}</span></>
              : s.status === "Paused"
                ? "Paused — nothing will post until you resume it."
                : "Ended."}
          </p>

          {s.statusReason && (
            <p className="flex items-start gap-1.5 text-xs text-amber-700 dark:text-amber-400">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              {s.statusReason}
            </p>
          )}

          <ul className="flex flex-wrap gap-1.5">
            {s.destinations.map((d) => (
              <li
                key={d.connectionId}
                className={cn(
                  "flex items-center gap-1 rounded-full border px-2 py-0.5 text-[11px]",
                  d.needsReconnect ? "border-amber-400 text-amber-700 dark:text-amber-400" : "border-border/60",
                )}
                title={d.needsReconnect ? "Needs reconnecting: posts to this account will fail" : d.platformName}
              >
                <PlatformIcon platform={d.platform} />
                {d.displayName}
              </li>
            ))}
          </ul>
          {broken.length > 0 && s.status === "Active" && (
            <p className="text-xs text-amber-700 dark:text-amber-400">
              {broken.map((d) => d.displayName).join(", ")} need{broken.length === 1 ? "s" : ""} reconnecting.
              Other accounts still get each post.{" "}
              <Link href="/social?tab=accounts" className="underline">Reconnect</Link>
            </p>
          )}

          {s.recent.length > 0 && (
            <ul className="flex flex-wrap gap-x-3 gap-y-1 pt-1 text-[11px] text-muted-foreground" aria-label="Recent posts">
              {s.recent.slice(0, 4).map((o) => (
                <li key={o.postId} className="flex items-center gap-1" title={describe(o.status)}>
                  <OutcomeIcon status={o.status} />
                  {o.label}
                </li>
              ))}
            </ul>
          )}
          {s.missedCount > 0 && (
            <p className="text-[11px] text-muted-foreground">
              {s.missedCount} post{s.missedCount === 1 ? " was" : "s were"} skipped because they could not go out within two hours.
            </p>
          )}
        </div>

        <div className="flex shrink-0 flex-col items-end gap-1.5">
          {s.status === "Active" && (
            <Button size="sm" variant="outline" disabled={busy} onClick={onPause}>
              <Pause className="mr-1 h-3.5 w-3.5" /> Pause
            </Button>
          )}
          {s.status === "Paused" && (
            <Button
              size="sm"
              variant="outline"
              disabled={busy || !canResume}
              title={!canResume ? "Your plan's active limit is reached." : undefined}
              onClick={onResume}
            >
              <Play className="mr-1 h-3.5 w-3.5" /> Resume
            </Button>
          )}
          {s.status !== "Ended" && (
            <>
              <Button size="sm" variant="ghost" disabled={busy} onClick={onEdit}>
                <Pencil className="mr-1 h-3.5 w-3.5" /> Edit
              </Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={onEnd} className="text-destructive">
                <Square className="mr-1 h-3.5 w-3.5" /> End
              </Button>
            </>
          )}
        </div>
      </div>
    </li>
  );
}

function StatusBadge({ status }: { status: RecurringSeries["status"] }) {
  return (
    <Badge
      variant={status === "Active" ? "default" : "secondary"}
      className={cn("h-5 text-[10px]", status === "Paused" && "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300")}
    >
      {status}
    </Badge>
  );
}

function OutcomeIcon({ status }: { status: PublishStatus }) {
  if (status === "Published") return <CheckCircle2 className="h-3 w-3 text-emerald-600" />;
  if (status === "PartiallyPublished") return <AlertTriangle className="h-3 w-3 text-amber-600" />;
  if (status === "Failed") return <XCircle className="h-3 w-3 text-destructive" />;
  if (status === "Cancelled") return <Square className="h-3 w-3" />;
  return <Clock className="h-3 w-3" />;
}

function describe(status: PublishStatus): string {
  switch (status) {
    case "Published": return "Posted";
    case "PartiallyPublished": return "Posted to some accounts";
    case "Failed": return "Did not post";
    case "Cancelled": return "Not sent";
    default: return "Queued";
  }
}

function ErrorNote({ message }: { message: string }) {
  return (
    <p className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive" role="alert">
      {message}
    </p>
  );
}

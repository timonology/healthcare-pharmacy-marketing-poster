"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { AlertTriangle, Check, ChevronLeft, ChevronRight, Film, Image as ImageIcon, Loader2, Repeat, X } from "lucide-react";
import type {
  PosterSummary,
  PublishSourceKind,
  RecurringSeries,
  SeriesNotify,
  SeriesPreview,
  SocialConnection,
  Video,
  Weekday,
} from "@acme/shared-types";
import { RECURRING_PLATFORMS, WEEKDAYS } from "@acme/shared-types";
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
import {
  TIME_ZONES,
  addDate,
  browserTimeZone,
  emptyForm,
  formFrom,
  toCadence,
  whenProblem,
  type SeriesForm,
} from "@/lib/series";
import { cn } from "@/lib/utils";
import { MetaConnectButton } from "./ConnectionsTab";
import { PlatformIcon } from "./PlatformIcon";
import { SeriesCalendarStrip } from "./SeriesCalendarStrip";

const CAPTION_LIMIT = 2200;
const STEPS = ["What", "Where", "When", "Review"] as const;

export interface SeriesSource {
  kind: PublishSourceKind;
  id: string;
  name: string;
  /** A picture to show; the card or thumbnail route if omitted. */
  imageUrl?: string | null;
}

function sourceImage(s: SeriesSource): string {
  return s.imageUrl ?? (s.kind === "Video" ? `/api/videos/${s.id}/thumbnail` : `/api/posters/${s.id}/card`);
}

/** Only these take recurring posts for now; Google Business Profile comes later. */
function isSeriesPlatform(c: SocialConnection) {
  return RECURRING_PLATFORMS.includes(c.platform);
}

/**
 * Set up (or change) a recurring post in four steps: What → Where → When → Review.
 *
 * The server works out every date, in the pharmacy's time zone, including
 * clock changes; the When and Review steps show its answer ("Tue 28 Oct,
 * 09:00 UK") rather than one computed here, so what they see is what will
 * happen.
 */
export function RecurringDialog({
  open,
  onClose,
  onSaved,
  source,
  editing,
}: {
  open: boolean;
  onClose: () => void;
  onSaved?: (series: RecurringSeries) => void;
  /** Preset from a poster or video's "Make recurring". Skips the picker. */
  source?: SeriesSource | null;
  /** Edit this series instead of creating one. Its source cannot change. */
  editing?: RecurringSeries | null;
}) {
  const [step, setStep] = useState(0);
  const [chosen, setChosen] = useState<SeriesSource | null>(null);
  const [form, setForm] = useState<SeriesForm>(() => emptyForm());
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [connections, setConnections] = useState<SocialConnection[] | null>(null);
  const [posters, setPosters] = useState<PosterSummary[] | null>(null);
  const [videos, setVideos] = useState<Video[] | null>(null);
  const [preview, setPreview] = useState<SeriesPreview | null>(null);
  const [newDate, setNewDate] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // A fresh form each time it opens.
  useEffect(() => {
    if (!open) return;
    setError(null);
    setPreview(null);
    setNewDate("");
    if (editing) {
      setChosen({ kind: editing.sourceKind, id: editing.sourceId, name: editing.sourceName });
      setForm(formFrom(editing));
      setSelected(new Set(editing.destinations.map((d) => d.connectionId)));
      setStep(1);
    } else {
      const zone = TIME_ZONES.some((z) => z.id === browserTimeZone()) ? browserTimeZone() : "Europe/London";
      setChosen(source ?? null);
      setForm({ ...emptyForm(zone), name: source?.name ?? "" });
      setSelected(new Set());
      setStep(source ? 1 : 0);
    }

    api.social.connections()
      .then(setConnections)
      .catch((err: unknown) => setError(err instanceof Error ? err.message : "Could not load your accounts."));

    if (!source && !editing) {
      api.listPosters({ status: "Published", take: 60 })
        .then((p) => setPosters(p.items))
        .catch(() => setPosters([]));
      api.listVideos()
        .then((v) => setVideos(v.filter((x) => x.status === "Ready")))
        .catch(() => setVideos([]));
    }
    // Keyed on identity, not the objects: a parent that builds `source` inline
    // would otherwise reset the form on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, source?.kind, source?.id, editing?.id]);

  const isVideo = chosen?.kind === "Video";

  const usable = useMemo(
    () => (connections ?? []).filter((c) =>
      isSeriesPlatform(c) && c.canPublish && (isVideo ? c.supportsVideo : c.supportsImages !== false)),
    [connections, isVideo],
  );
  const needsReconnect = (connections ?? []).filter((c) => isSeriesPlatform(c) && c.status === "NeedsReauth");

  // On edit, an account already on the series that now needs reconnecting is
  // still listed — ticked, and untickable — rather than hidden while staying
  // selected. The server keeps it (its posts fail one at a time); a newly
  // added account must work.
  const kept = useMemo(
    () => (editing
      ? needsReconnect.filter((c) =>
          editing.destinations.some((d) => d.connectionId === c.id) && (!isVideo || c.supportsVideo))
      : []),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [connections, editing?.id, isVideo],
  );
  const listed = useMemo(() => [...usable, ...kept], [usable, kept]);

  // What is sent: only accounts on screen. One disconnected and removed since
  // the series was set up is dropped rather than failing the whole save.
  const chosenIds = [...selected].filter((id) => listed.some((c) => c.id === id));

  // The server's dates for what they have picked, as they pick it.
  const cadence = toCadence(form);
  const problem = whenProblem(form);
  const previewKey = JSON.stringify([cadence, form.timeZoneId, form.startDate, form.until, form.endDate]);
  useEffect(() => {
    if (!open || step < 2 || problem) {
      setPreview(null);
      return;
    }
    const t = setTimeout(() => {
      api.social
        .previewSeries({
          cadence,
          timeZoneId: form.timeZoneId,
          startDate: form.startDate,
          endDate: form.until === "date" ? form.endDate : null,
          count: 14,
        })
        .then(setPreview)
        .catch(() => setPreview(null));
    }, 250);
    return () => clearTimeout(t);
    // previewKey stands for the inputs above.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, step, previewKey, problem]);

  function update<K extends keyof SeriesForm>(key: K, value: SeriesForm[K]) {
    setForm((f) => ({ ...f, [key]: value }));
  }

  function toggleDay(day: Weekday) {
    setForm((f) => ({
      ...f,
      days: f.days.includes(day) ? f.days.filter((d) => d !== day) : [...f.days, day],
    }));
  }

  const canNext =
    step === 0 ? chosen !== null
      : step === 1 ? chosenIds.length > 0
        : step === 2 ? problem === null && (preview?.problem ?? null) === null
          : true;

  async function save() {
    if (!chosen) return;
    setBusy(true);
    setError(null);
    try {
      const body = {
        name: form.name.trim() || chosen.name,
        sourceKind: chosen.kind,
        sourceId: chosen.id,
        caption: form.caption,
        connectionIds: chosenIds,
        cadence,
        timeZoneId: form.timeZoneId,
        startDate: form.startDate,
        endDate: form.until === "date" ? form.endDate : null,
        notify: form.notify,
        postNow: editing ? false : form.postNow,
      };
      const saved = editing
        ? await api.social.updateSeries(editing.id, body)
        : await api.social.createSeries(body);
      onSaved?.(saved);
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not save this recurring post.");
    } finally {
      setBusy(false);
    }
  }

  if (!open) return null;

  const zones = TIME_ZONES.some((z) => z.id === form.timeZoneId)
    ? TIME_ZONES
    : [{ id: form.timeZoneId, label: form.timeZoneId }, ...TIME_ZONES];

  return (
    <Dialog open onOpenChange={(v) => !v && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Repeat className="h-4 w-4 text-primary" />
            {editing ? "Change recurring post" : "New recurring post"}
          </DialogTitle>
          <DialogDescription>
            {editing
              ? "Changes apply to posts from now on. Anything already posted stays as it is."
              : "Set it up once. It posts on your schedule until you pause or end it."}
          </DialogDescription>
        </DialogHeader>

        {/* Stepper */}
        <ol className="flex items-center gap-2 text-xs" aria-label="Steps">
          {STEPS.map((label, i) => (
            <li key={label} className="flex items-center gap-2">
              <span
                aria-current={i === step ? "step" : undefined}
                className={cn(
                  "flex h-6 items-center gap-1 rounded-full px-2.5 font-medium",
                  i === step
                    ? "bg-primary text-primary-foreground"
                    : i < step
                      ? "bg-primary/15 text-primary"
                      : "bg-muted text-muted-foreground",
                )}
              >
                {i < step ? <Check className="h-3 w-3" /> : <span>{i + 1}</span>}
                {label}
              </span>
              {i < STEPS.length - 1 && <span className="h-px w-4 bg-border" aria-hidden />}
            </li>
          ))}
        </ol>

        {error && (
          <p className="rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive" role="alert">
            {error}
          </p>
        )}

        {/* 1. What */}
        {step === 0 && (
          <div className="space-y-4">
            {chosen && <SourceCard source={chosen} />}
            <SourcePicker
              title="Published posters"
              empty="No published posters yet. Publish one from My Posters first."
              loading={posters === null}
              items={(posters ?? []).map((p) => ({
                kind: "Poster" as const,
                id: p.id,
                name: p.name,
                imageUrl: p.thumbnailUrl,
              }))}
              chosen={chosen}
              onChoose={(s) => { setChosen(s); update("name", s.name); }}
            />
            <SourcePicker
              title="Ready videos"
              empty="No finished videos yet."
              loading={videos === null}
              items={(videos ?? []).map((v) => ({
                kind: "Video" as const,
                id: v.id,
                name: v.name,
                imageUrl: v.thumbnailUrl,
              }))}
              chosen={chosen}
              onChoose={(s) => { setChosen(s); update("name", s.name); }}
            />
          </div>
        )}

        {/* 2. Where */}
        {step === 1 && (
          <div className="space-y-3">
            {chosen && <SourceCard source={chosen} compact />}
            {connections === null ? (
              <p className="text-sm text-muted-foreground">Loading your accounts…</p>
            ) : listed.length === 0 ? (
              <div className="rounded-lg border border-dashed p-6 text-center">
                <p className="text-sm font-medium">Connect an account first</p>
                <p className="mt-1 text-xs text-muted-foreground">
                  Recurring posts go to your Facebook Page, Instagram, LinkedIn and (for videos) YouTube.
                </p>
                <MetaConnectButton className="mt-3 flex flex-col items-center" />
              </div>
            ) : (
              <fieldset className="space-y-2">
                <legend className="mb-1 text-sm font-medium">Post to</legend>
                {listed.map((c) => (
                  <label
                    key={c.id}
                    className={cn(
                      "flex cursor-pointer items-center gap-3 rounded-md border p-3 text-sm",
                      selected.has(c.id) ? "border-primary bg-primary/5" : "border-border/60",
                    )}
                  >
                    <input
                      type="checkbox"
                      className="h-4 w-4 accent-primary"
                      checked={selected.has(c.id)}
                      onChange={() =>
                        setSelected((prev) => {
                          const next = new Set(prev);
                          if (next.has(c.id)) next.delete(c.id);
                          else next.add(c.id);
                          return next;
                        })
                      }
                    />
                    <PlatformIcon platform={c.platform} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-medium">{c.displayName}</span>
                      <span className="block text-xs text-muted-foreground">
                        {c.platformName}
                        {c.status === "NeedsReauth" && (
                          <span className="text-amber-700 dark:text-amber-400"> · needs reconnecting, posts to it will fail</span>
                        )}
                      </span>
                    </span>
                  </label>
                ))}
              </fieldset>
            )}
            {needsReconnect.length > 0 && (
              <p className="flex items-start gap-2 text-xs text-amber-700 dark:text-amber-400">
                <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                {needsReconnect.map((c) => c.displayName).join(", ")} need
                {needsReconnect.length === 1 ? "s" : ""} reconnecting before you can post to{" "}
                {needsReconnect.length === 1 ? "it" : "them"}.{" "}
                <Link href="/social?tab=accounts" className="underline">Reconnect</Link>
              </p>
            )}
            {isVideo && (
              <p className="text-xs text-muted-foreground">Only accounts that can take a video are shown.</p>
            )}
          </div>
        )}

        {/* 3. When */}
        {step === 2 && (
          <div className="space-y-4">
            <div className="flex flex-wrap gap-1 rounded-lg bg-muted p-1" role="radiogroup" aria-label="How often">
              {(["Daily", "Weekly", "Custom"] as const).map((f) => (
                <button
                  key={f}
                  type="button"
                  role="radio"
                  aria-checked={form.frequency === f}
                  onClick={() => update("frequency", f)}
                  className={cn(
                    "flex-1 rounded-md px-3 py-1.5 text-sm font-medium transition-colors",
                    form.frequency === f ? "bg-background shadow-sm" : "text-muted-foreground hover:text-foreground",
                  )}
                >
                  {f}
                </button>
              ))}
            </div>

            {form.frequency === "Weekly" && (
              <div>
                <p className="mb-1.5 text-sm font-medium">On</p>
                <div className="flex flex-wrap gap-1.5">
                  {WEEKDAYS.map((d) => (
                    <button
                      key={d.value}
                      type="button"
                      aria-pressed={form.days.includes(d.value)}
                      onClick={() => toggleDay(d.value)}
                      className={cn(
                        "h-9 w-12 rounded-md border text-sm",
                        form.days.includes(d.value)
                          ? "border-primary bg-primary text-primary-foreground"
                          : "border-border/60 hover:bg-accent",
                      )}
                    >
                      {d.short}
                    </button>
                  ))}
                </div>
              </div>
            )}

            {form.frequency === "Custom" && (
              <div className="space-y-3">
                <div className="flex flex-wrap gap-4 text-sm">
                  <label className="flex items-center gap-2">
                    <input
                      type="radio"
                      name="custom"
                      checked={form.custom === "EveryNDays"}
                      onChange={() => update("custom", "EveryNDays")}
                    />
                    Every few days
                  </label>
                  <label className="flex items-center gap-2">
                    <input
                      type="radio"
                      name="custom"
                      checked={form.custom === "SpecificDates"}
                      onChange={() => update("custom", "SpecificDates")}
                    />
                    On specific dates
                  </label>
                </div>
                {form.custom === "EveryNDays" ? (
                  <div className="flex items-center gap-2 text-sm">
                    <span>Every</span>
                    <Input
                      type="number"
                      min={1}
                      max={365}
                      value={form.intervalDays}
                      onChange={(e) => update("intervalDays", Number(e.target.value))}
                      className="h-9 w-20"
                      aria-label="Number of days"
                    />
                    <span>days, counting from the start date</span>
                  </div>
                ) : (
                  <div className="space-y-2">
                    <div className="flex items-center gap-2">
                      <Input
                        type="date"
                        value={newDate}
                        min={form.startDate}
                        onChange={(e) => setNewDate(e.target.value)}
                        className="h-9 w-44"
                        aria-label="Add a date"
                      />
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        disabled={!newDate}
                        onClick={() => { update("dates", addDate(form.dates, newDate)); setNewDate(""); }}
                      >
                        Add date
                      </Button>
                    </div>
                    {form.dates.length > 0 && (
                      <ul className="flex flex-wrap gap-1.5">
                        {form.dates.map((d) => (
                          <li key={d} className="flex items-center gap-1 rounded-full bg-muted px-2.5 py-1 text-xs">
                            {d}
                            <button
                              type="button"
                              aria-label={`Remove ${d}`}
                              onClick={() => update("dates", form.dates.filter((x) => x !== d))}
                            >
                              <X className="h-3 w-3" />
                            </button>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                )}
              </div>
            )}

            <div className="grid gap-3 sm:grid-cols-2">
              <div className="space-y-1">
                <Label htmlFor="series-time">Time</Label>
                <Input id="series-time" type="time" value={form.time} onChange={(e) => update("time", e.target.value)} />
              </div>
              <div className="space-y-1">
                <Label htmlFor="series-zone">Time zone</Label>
                <select
                  id="series-zone"
                  value={form.timeZoneId}
                  onChange={(e) => update("timeZoneId", e.target.value)}
                  className="flex h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
                >
                  {zones.map((z) => (
                    <option key={z.id} value={z.id}>{z.label}</option>
                  ))}
                </select>
              </div>
              <div className="space-y-1">
                <Label htmlFor="series-start">Start</Label>
                <Input
                  id="series-start"
                  type="date"
                  value={form.startDate}
                  onChange={(e) => update("startDate", e.target.value)}
                />
              </div>
              <div className="space-y-1">
                <span className="text-sm font-medium leading-none">End</span>
                <div className="flex flex-col gap-1.5 pt-1 text-sm">
                  <label className="flex items-center gap-2">
                    <input type="radio" name="until" checked={form.until === "stop"} onChange={() => update("until", "stop")} />
                    Until I stop it
                  </label>
                  <label className="flex items-center gap-2">
                    <input type="radio" name="until" checked={form.until === "date"} onChange={() => update("until", "date")} />
                    On
                    <Input
                      type="date"
                      value={form.endDate}
                      min={form.startDate}
                      disabled={form.until !== "date"}
                      onChange={(e) => update("endDate", e.target.value)}
                      className="h-8 w-40"
                      aria-label="End date"
                    />
                  </label>
                </div>
              </div>
            </div>

            <NextDates preview={preview} problem={problem} />
          </div>
        )}

        {/* 4. Review */}
        {step === 3 && chosen && (
          <div className="space-y-4">
            <SourceCard source={chosen} compact />
            <dl className="grid gap-x-4 gap-y-2 text-sm sm:grid-cols-[8rem_1fr]">
              <dt className="text-muted-foreground">Where</dt>
              <dd>
                {listed
                  .filter((c) => chosenIds.includes(c.id))
                  .map((c) => `${c.displayName} (${c.platformName})`)
                  .join(", ")}
              </dd>
              <dt className="text-muted-foreground">When</dt>
              <dd>{preview?.cadenceText ?? ""} {preview ? `· ${preview.timeZoneLabel} time` : ""}</dd>
            </dl>

            <NextDates preview={preview} problem={null} limit={3} />
            {preview && (
              <SeriesCalendarStrip
                compact
                timeZoneId={form.timeZoneId}
                series={[{ id: "new", name: form.name || chosen.name, upcoming: preview.next }]}
              />
            )}

            <div className="space-y-1">
              <Label htmlFor="series-name">Name</Label>
              <Input
                id="series-name"
                value={form.name}
                maxLength={80}
                placeholder={chosen.name}
                onChange={(e) => update("name", e.target.value)}
              />
            </div>
            <div className="space-y-1">
              <Label htmlFor="series-caption">Caption</Label>
              <Textarea
                id="series-caption"
                rows={3}
                value={form.caption}
                onChange={(e) => update("caption", e.target.value)}
                placeholder="The same caption goes out each time."
              />
              <p className={cn("text-right text-[11px]", form.caption.length > CAPTION_LIMIT ? "text-destructive" : "text-muted-foreground")}>
                {form.caption.length}/{CAPTION_LIMIT}
              </p>
            </div>
            <div className="space-y-1">
              <Label htmlFor="series-notify">Email me</Label>
              <select
                id="series-notify"
                value={form.notify}
                onChange={(e) => update("notify", e.target.value as SeriesNotify)}
                className="flex h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
              >
                <option value="All">After every post, and if anything needs me</option>
                <option value="ProblemsOnly">Only if something needs me</option>
                <option value="Off">Never</option>
              </select>
            </div>
            {!editing && (
              <label className="flex items-center gap-2 text-sm">
                <input
                  type="checkbox"
                  className="h-4 w-4 accent-primary"
                  checked={form.postNow}
                  onChange={(e) => update("postNow", e.target.checked)}
                />
                Also post now
              </label>
            )}
          </div>
        )}

        <div className="flex items-center justify-between gap-2 border-t pt-4">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => (step === 0 || (step === 1 && (source || editing)) ? onClose() : setStep(step - 1))}
          >
            {step === 0 || (step === 1 && (source || editing)) ? "Cancel" : (<><ChevronLeft className="mr-1 h-4 w-4" />Back</>)}
          </Button>
          {step < STEPS.length - 1 ? (
            <Button size="sm" disabled={!canNext} onClick={() => setStep(step + 1)}>
              Next <ChevronRight className="ml-1 h-4 w-4" />
            </Button>
          ) : (
            <Button
              size="sm"
              disabled={busy || form.caption.length > CAPTION_LIMIT || !preview || preview.next.length === 0}
              onClick={save}
            >
              {busy && <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />}
              {editing ? "Save changes" : "Start recurring post"}
            </Button>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}

function SourceCard({ source, compact = false }: { source: SeriesSource; compact?: boolean }) {
  return (
    <div className="flex items-center gap-3 rounded-lg border border-border/60 bg-muted/30 p-2">
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src={sourceImage(source)}
        alt=""
        className={cn("rounded object-cover", compact ? "h-12 w-10" : "h-20 w-16")}
      />
      <div className="min-w-0">
        <p className="truncate text-sm font-medium">{source.name}</p>
        <p className="flex items-center gap-1 text-xs text-muted-foreground">
          {source.kind === "Video" ? <Film className="h-3 w-3" /> : <ImageIcon className="h-3 w-3" />}
          {source.kind === "Video" ? "Video" : "Poster"}
        </p>
      </div>
    </div>
  );
}

function SourcePicker({
  title,
  empty,
  loading,
  items,
  chosen,
  onChoose,
}: {
  title: string;
  empty: string;
  loading: boolean;
  items: SeriesSource[];
  chosen: SeriesSource | null;
  onChoose: (s: SeriesSource) => void;
}) {
  return (
    <section>
      <h3 className="mb-2 text-sm font-medium">{title}</h3>
      {loading ? (
        <p className="text-xs text-muted-foreground">Loading…</p>
      ) : items.length === 0 ? (
        <p className="text-xs text-muted-foreground">{empty}</p>
      ) : (
        <ul className="grid max-h-56 grid-cols-3 gap-2 overflow-y-auto sm:grid-cols-5">
          {items.map((s) => {
            const on = chosen?.id === s.id && chosen.kind === s.kind;
            return (
              <li key={`${s.kind}-${s.id}`}>
                <button
                  type="button"
                  aria-pressed={on}
                  onClick={() => onChoose(s)}
                  className={cn(
                    "w-full overflow-hidden rounded-md border text-left",
                    on ? "border-primary ring-2 ring-primary/40" : "border-border/60 hover:border-primary/50",
                  )}
                >
                  {/* eslint-disable-next-line @next/next/no-img-element */}
                  <img src={sourceImage(s)} alt="" className="aspect-[3/4] w-full bg-muted object-cover" loading="lazy" />
                  <span className="block truncate px-1.5 py-1 text-[11px]">{s.name}</span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function NextDates({
  preview,
  problem,
  limit = 3,
}: {
  preview: SeriesPreview | null;
  problem: string | null;
  limit?: number;
}) {
  const message = problem ?? preview?.problem ?? null;
  if (message)
    return (
      <p className="flex items-start gap-2 rounded-md bg-amber-50 p-2.5 text-xs text-amber-800 dark:bg-amber-950/40 dark:text-amber-300">
        <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
        {message}
      </p>
    );
  if (!preview) return <p className="text-xs text-muted-foreground">Working out the dates…</p>;
  return (
    <div className="rounded-md border border-border/60 p-3">
      <p className="text-xs font-medium text-muted-foreground">{preview.cadenceText}. Next posts:</p>
      <ul className="mt-1.5 space-y-0.5 text-sm">
        {preview.next.slice(0, limit).map((t) => (
          <li key={t.atUtc}>{t.label}</li>
        ))}
      </ul>
    </div>
  );
}

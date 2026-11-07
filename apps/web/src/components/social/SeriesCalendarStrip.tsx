"use client";

import type { SeriesTime } from "@acme/shared-types";
import { byDay, stripDays } from "@/lib/series";
import { cn } from "@/lib/utils";

export interface StripSeries {
  id: string;
  name: string;
  upcoming: SeriesTime[];
}

/** One colour per series, in order. Enough to tell a handful apart. */
const DOTS = [
  "bg-primary",
  "bg-sky-500",
  "bg-amber-500",
  "bg-emerald-500",
  "bg-rose-500",
  "bg-violet-500",
];

/**
 * The next 14 days, with a dot on each day something will post.
 *
 * So a pharmacy can see the pattern at a glance — "Mon and Thu", or the
 * bunching when three series land on the same Tuesday — rather than read it
 * out of a list of dates. Days are on the pharmacy's clock (the server sends
 * each occurrence's local date), not the browser's.
 */
export function SeriesCalendarStrip({
  series,
  timeZoneId,
  className,
  compact = false,
}: {
  series: StripSeries[];
  timeZoneId: string;
  className?: string;
  /** Smaller, label-free version for inside the stepper. */
  compact?: boolean;
}) {
  const days = stripDays(timeZoneId, 14);
  const perSeries = series.map((s, i) => ({ ...s, dot: DOTS[i % DOTS.length], byDay: byDay(s.upcoming) }));
  const total = series.reduce((n, s) => n + s.upcoming.length, 0);

  return (
    <div className={cn("rounded-lg border border-border/60 bg-card p-3", className)}>
      {!compact && (
        <div className="mb-2 flex items-baseline justify-between">
          <p className="text-xs font-medium text-muted-foreground">Next 14 days</p>
          <p className="text-xs text-muted-foreground">
            {total === 0 ? "Nothing scheduled" : `${total} post${total === 1 ? "" : "s"}`}
          </p>
        </div>
      )}
      <ol className="grid grid-cols-7 gap-1" aria-label="Posts in the next 14 days">
        {days.map((d) => {
          const today = perSeries.flatMap((s) => (s.byDay.get(d.date) ?? []).map((t) => ({ s, t })));
          const label =
            today.length === 0
              ? `${d.weekday} ${d.day}${d.month ? ` ${d.month}` : ""}: nothing`
              : `${d.weekday} ${d.day}${d.month ? ` ${d.month}` : ""}: ` +
                today.map(({ s, t }) => `${s.name} at ${t.localTime}`).join(", ");
          return (
            <li
              key={d.date}
              title={label}
              aria-label={label}
              className={cn(
                "flex min-h-[3.25rem] flex-col items-center rounded-md border px-1 py-1 text-center",
                d.isToday ? "border-primary/50 bg-primary/5" : "border-transparent bg-muted/40",
              )}
            >
              <span className="text-[10px] uppercase text-muted-foreground">{d.weekday}</span>
              <span className="text-sm font-medium leading-tight">{d.day}</span>
              <span className="mt-0.5 flex flex-wrap justify-center gap-0.5">
                {today.slice(0, 4).map(({ s, t }) => (
                  <span key={`${s.id}-${t.atUtc}`} className={cn("h-1.5 w-1.5 rounded-full", s.dot)} />
                ))}
              </span>
            </li>
          );
        })}
      </ol>
      {!compact && series.length > 1 && (
        <ul className="mt-2 flex flex-wrap gap-x-3 gap-y-1">
          {perSeries.map((s) => (
            <li key={s.id} className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
              <span className={cn("h-2 w-2 rounded-full", s.dot)} />
              {s.name}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

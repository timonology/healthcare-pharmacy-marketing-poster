"use client";

import { useId, useState, useEffect } from "react";
import { CalendarRange } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import {
  DATE_PRESETS,
  SORT_OPTIONS,
  dayOf,
  presetOf,
  presetRange,
  validDay,
  type DatePreset,
  type ListSort,
  type ListWindow,
} from "@/lib/list-window";

const selectClass =
  "h-9 rounded-md border border-input bg-background px-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring";

/**
 * Sort and Date, the same on every list. Sits at the right-hand end of the
 * page's toolbar, after its search box and chips.
 *
 * Native selects and date inputs: they are keyboard- and screen-reader-ready
 * as they stand, and a phone shows its own pickers for them.
 *
 * Choosing Custom opens From/To with this month filled in, so it is never an
 * empty, unexplained pair of boxes. A custom range is applied once both ends
 * are valid; until then the list keeps showing what it showed.
 */
export function SortDateControls({
  value,
  onChange,
  className,
  /** What the date filters on, for the tooltip: "Created", "Last updated"… */
  dateField,
}: {
  value: ListWindow;
  onChange: (next: ListWindow) => void;
  className?: string;
  dateField?: string;
}) {
  const sortId = useId();
  const dateId = useId();
  const fromId = useId();
  const toId = useId();

  const derived = presetOf(value);
  // "Custom" chosen but not yet applied is local; the URL only learns of a
  // range once it is a range.
  const [customOpen, setCustomOpen] = useState(derived === "custom");
  const [from, setFrom] = useState(value.from ?? "");
  const [to, setTo] = useState(value.to ?? "");

  // Follow the range when it changes from outside — Clear filters, Back, a
  // preset. No range at all is All time, so Custom closes; a range that is no
  // preset is Custom, so it opens. A preset range leaves it as it is, so
  // choosing Custom from All time (which applies this month) stays on Custom.
  useEffect(() => {
    setFrom(value.from ?? "");
    setTo(value.to ?? "");
    if (!value.from && !value.to) setCustomOpen(false);
    else if (presetOf(value) === "custom") setCustomOpen(true);
  }, [value.from, value.to]); // eslint-disable-line react-hooks/exhaustive-deps

  const preset: DatePreset = customOpen ? "custom" : derived;

  function choosePreset(p: DatePreset) {
    if (p === "custom") {
      setCustomOpen(true);
      // Start from the range already showing; from All time, start at this
      // month so the boxes are filled and the list says what they mean.
      if (!value.from && !value.to) onChange({ ...value, ...presetRange("month") });
      return;
    }
    setCustomOpen(false);
    onChange({ ...value, ...presetRange(p) });
  }

  function applyCustom(nextFrom: string, nextTo: string) {
    setFrom(nextFrom);
    setTo(nextTo);
    const f = validDay(nextFrom);
    const t = validDay(nextTo);
    // Either end may be open; both empty is All time.
    if ((nextFrom && !f) || (nextTo && !t)) return;
    if (f && t && f > t) return;
    onChange({ ...value, from: f, to: t });
  }

  const reversed = Boolean(from && to && validDay(from) && validDay(to) && from > to);
  const today = dayOf(new Date());

  return (
    <div className={cn("flex flex-wrap items-end gap-3 md:ml-auto md:justify-end", className)}>
      <div className="flex flex-col gap-1">
        <label htmlFor={sortId} className="text-xs font-medium text-muted-foreground">
          Sort
        </label>
        <select
          id={sortId}
          value={value.sort}
          onChange={(e) => onChange({ ...value, sort: e.target.value as ListSort })}
          className={selectClass}
        >
          {SORT_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      </div>

      <div className="flex flex-col gap-1">
        <label
          htmlFor={dateId}
          className="text-xs font-medium text-muted-foreground"
          title={dateField ? `Filters on: ${dateField}` : undefined}
        >
          Date{dateField ? <span className="font-normal"> · {dateField.toLowerCase()}</span> : null}
        </label>
        <select
          id={dateId}
          value={preset}
          onChange={(e) => choosePreset(e.target.value as DatePreset)}
          className={selectClass}
        >
          {DATE_PRESETS.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      </div>

      {preset === "custom" && (
        <fieldset className="flex flex-wrap items-end gap-2">
          <legend className="sr-only">Custom date range</legend>
          <div className="flex flex-col gap-1">
            <label htmlFor={fromId} className="text-xs font-medium text-muted-foreground">
              From
            </label>
            <input
              id={fromId}
              type="date"
              value={from}
              max={to || today}
              onChange={(e) => applyCustom(e.target.value, to)}
              className={selectClass}
            />
          </div>
          <div className="flex flex-col gap-1">
            <label htmlFor={toId} className="text-xs font-medium text-muted-foreground">
              To
            </label>
            <input
              id={toId}
              type="date"
              value={to}
              min={from || undefined}
              onChange={(e) => applyCustom(from, e.target.value)}
              className={selectClass}
            />
          </div>
          {reversed && (
            <p className="w-full text-xs text-destructive" role="alert">
              &ldquo;From&rdquo; must be on or before &ldquo;To&rdquo;.
            </p>
          )}
        </fieldset>
      )}
    </div>
  );
}

/**
 * What a list shows when its date range matches nothing.
 *
 * Only for that case: a list that is empty with no range has its own empty
 * state ("No posters yet — create one"), which is a different message.
 */
export function NoResultsInRange({
  onClear,
  className,
}: {
  onClear: () => void;
  className?: string;
}) {
  return (
    <div
      className={cn(
        "col-span-full flex flex-col items-center gap-3 rounded-lg border border-dashed p-8 text-center",
        className,
      )}
      role="status"
    >
      <CalendarRange className="h-6 w-6 text-muted-foreground" aria-hidden />
      <p className="text-sm font-medium">No results in this date range</p>
      <Button variant="outline" size="sm" onClick={onClear}>
        Clear filters
      </Button>
    </div>
  );
}

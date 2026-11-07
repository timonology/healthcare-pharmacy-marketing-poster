/**
 * Sort and date range for every searchable list, and how they live in the URL.
 *
 * One model for the templates gallery, My Posters, Videos, Campaigns, Patients
 * and the admin users, pharmacies and audit tables, so the controls mean the
 * same thing everywhere and a shared link opens on the same view.
 *
 * **In the URL**, `sort=newest|oldest`, `from=YYYY-MM-DD`, `to=YYYY-MM-DD`.
 * Dates are calendar days in the viewer's own time zone, both ends inclusive —
 * what a person picked, not an instant. Defaults are left out, so an unfiltered
 * list has a clean URL.
 *
 * **To the API**, `from` is the start of the first day and `to` the start of
 * the day *after* the last, both as UTC instants. The server treats `from` as
 * inclusive and `to` as exclusive, so "1–31 October" includes 23:59 on the
 * 31st wherever the viewer is. Only the browser knows the viewer's time zone,
 * which is why the day arithmetic happens here.
 *
 * Pure functions: no React, no `window`. The hook in
 * `hooks/use-list-window.ts` wires them to the address bar.
 */

export type ListSort = "newest" | "oldest";

/** The date menu's choices. "custom" is any range that is not one of the presets. */
export type DatePreset = "all" | "7d" | "30d" | "month" | "custom";

export interface ListWindow {
  sort: ListSort;
  /** First day, inclusive, as `YYYY-MM-DD`. Null: no lower bound. */
  from: string | null;
  /** Last day, inclusive, as `YYYY-MM-DD`. Null: no upper bound. */
  to: string | null;
}

export const DEFAULT_WINDOW: ListWindow = { sort: "newest", from: null, to: null };

export const SORT_OPTIONS: { value: ListSort; label: string }[] = [
  { value: "newest", label: "Newest first" },
  { value: "oldest", label: "Oldest first" },
];

export const DATE_PRESETS: { value: DatePreset; label: string }[] = [
  { value: "all", label: "All time" },
  { value: "7d", label: "Last 7 days" },
  { value: "30d", label: "Last 30 days" },
  { value: "month", label: "This month" },
  { value: "custom", label: "Custom" },
];

const DAY = /^\d{4}-\d{2}-\d{2}$/;

/** A real calendar day as `YYYY-MM-DD`, or null. Rejects 2026-02-31. */
export function validDay(value: string | null | undefined): string | null {
  if (!value || !DAY.test(value)) return null;
  const [y, m, d] = value.split("-").map(Number);
  const date = new Date(y, m - 1, d);
  return date.getFullYear() === y && date.getMonth() === m - 1 && date.getDate() === d
    ? value
    : null;
}

/** `YYYY-MM-DD` for a local date. */
export function dayOf(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, "0");
  const d = String(date.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

function addDays(day: string, n: number): string {
  const [y, m, d] = day.split("-").map(Number);
  return dayOf(new Date(y, m - 1, d + n));
}

/** Reads sort/from/to from a query string. Anything malformed is ignored, not fatal. */
export function parseListWindow(search: string | URLSearchParams): ListWindow {
  const params = typeof search === "string" ? new URLSearchParams(search) : search;
  const sort: ListSort = params.get("sort")?.toLowerCase() === "oldest" ? "oldest" : "newest";
  let from = validDay(params.get("from"));
  let to = validDay(params.get("to"));
  // A reversed range from a hand-edited link: swap rather than show nothing.
  if (from && to && from > to) [from, to] = [to, from];
  return { sort, from, to };
}

/**
 * Writes the window into a copy of `params`, leaving every other key alone
 * (the gallery's `use=`, the social page's `tab=`). Defaults are removed.
 */
export function writeListWindow(params: URLSearchParams, w: ListWindow): URLSearchParams {
  const next = new URLSearchParams(params);
  if (w.sort === "oldest") next.set("sort", "oldest");
  else next.delete("sort");
  if (w.from) next.set("from", w.from);
  else next.delete("from");
  if (w.to) next.set("to", w.to);
  else next.delete("to");
  return next;
}

/** The range a preset stands for, as of `today`. Null bounds for "all". */
export function presetRange(
  preset: Exclude<DatePreset, "custom">,
  today: Date = new Date(),
): { from: string | null; to: string | null } {
  const t = dayOf(today);
  switch (preset) {
    case "7d":
      return { from: addDays(t, -6), to: t };
    case "30d":
      return { from: addDays(t, -29), to: t };
    case "month":
      return { from: dayOf(new Date(today.getFullYear(), today.getMonth(), 1)), to: t };
    default:
      return { from: null, to: null };
  }
}

/**
 * Which menu entry describes this window today.
 *
 * Derived rather than stored: the URL carries only dates, so a "Last 7 days"
 * link opened next week shows as Custom with the dates it was made with —
 * which is what it now is.
 */
export function presetOf(w: Pick<ListWindow, "from" | "to">, today: Date = new Date()): DatePreset {
  if (!w.from && !w.to) return "all";
  for (const p of ["7d", "30d", "month"] as const) {
    const r = presetRange(p, today);
    if (r.from === w.from && r.to === w.to) return p;
  }
  return "custom";
}

/** True when a date range is set. Sort alone is not a filter. */
export function hasDateRange(w: ListWindow): boolean {
  return Boolean(w.from || w.to);
}

/** Start of a local day as a UTC ISO instant. */
function startOfDayUtc(day: string): string {
  const [y, m, d] = day.split("-").map(Number);
  return new Date(y, m - 1, d).toISOString();
}

/**
 * Query parameters for the API: `sort` always, `from`/`to` as instants when set.
 * `to` is the start of the following day, so the last chosen day is included.
 */
export function listWindowParams(w: ListWindow): { sort: ListSort; from?: string; to?: string } {
  const out: { sort: ListSort; from?: string; to?: string } = { sort: w.sort };
  if (w.from) out.from = startOfDayUtc(w.from);
  if (w.to) out.to = startOfDayUtc(addDays(w.to, 1));
  return out;
}

/** Same, appended to a URLSearchParams (for endpoints built that way). */
export function appendListWindow(q: URLSearchParams, w: ListWindow): URLSearchParams {
  const p = listWindowParams(w);
  q.set("sort", p.sort);
  if (p.from) q.set("from", p.from);
  if (p.to) q.set("to", p.to);
  return q;
}

/** A stable string for effect dependencies. */
export function listWindowKey(w: ListWindow): string {
  return `${w.sort}|${w.from ?? ""}|${w.to ?? ""}`;
}

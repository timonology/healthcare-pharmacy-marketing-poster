/**
 * The recurring-post form, as plain data, and the calendar strip.
 *
 * Pure: no React, no fetch. The server works out the actual dates (it owns
 * time zones and clock changes); this only shapes what the pharmacy picked
 * into a request, says early what is missing, and lays out the 14 days.
 */
import type {
  CadenceRequest,
  RecurringSeries,
  SeriesNotify,
  SeriesTime,
  Weekday,
} from "@acme/shared-types";

export type Frequency = "Daily" | "Weekly" | "Custom";
export type CustomKind = "EveryNDays" | "SpecificDates";

export interface SeriesForm {
  frequency: Frequency;
  custom: CustomKind;
  /** "HH:mm", local. */
  time: string;
  days: Weekday[];
  intervalDays: number;
  /** "yyyy-MM-dd", local, sorted. */
  dates: string[];
  timeZoneId: string;
  startDate: string;
  until: "stop" | "date";
  endDate: string;
  name: string;
  caption: string;
  notify: SeriesNotify;
  postNow: boolean;
}

/** The zones offered. The pharmacy's browser zone is added if it is not here. */
export const TIME_ZONES: { id: string; label: string }[] = [
  { id: "Europe/London", label: "UK (London)" },
  { id: "Europe/Dublin", label: "Ireland (Dublin)" },
  { id: "Europe/Paris", label: "Central Europe (Paris)" },
  { id: "UTC", label: "UTC" },
];

/** The browser's IANA zone, or London if it cannot say. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || "Europe/London";
  } catch {
    return "Europe/London";
  }
}

/** Today on a zone's clock, "yyyy-MM-dd". */
export function todayIn(timeZoneId: string, now: Date = new Date()): string {
  try {
    // en-CA formats as yyyy-mm-dd.
    return new Intl.DateTimeFormat("en-CA", {
      timeZone: timeZoneId,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).format(now);
  } catch {
    return now.toISOString().slice(0, 10);
  }
}

function addDays(day: string, n: number): string {
  const [y, m, d] = day.split("-").map(Number);
  const date = new Date(Date.UTC(y, m - 1, d + n));
  return date.toISOString().slice(0, 10);
}

/** A fresh form: weekly on Monday at 9, starting today, until stopped. */
export function emptyForm(timeZoneId = "Europe/London", now: Date = new Date()): SeriesForm {
  return {
    frequency: "Weekly",
    custom: "EveryNDays",
    time: "09:00",
    days: ["Monday"],
    intervalDays: 3,
    dates: [],
    timeZoneId,
    startDate: todayIn(timeZoneId, now),
    until: "stop",
    endDate: "",
    name: "",
    caption: "",
    notify: "All",
    postNow: false,
  };
}

/** An existing series back into the form, for editing. */
export function formFrom(series: RecurringSeries): SeriesForm {
  const custom = series.cadenceKind === "EveryNDays" || series.cadenceKind === "SpecificDates";
  return {
    frequency: series.cadenceKind === "Daily" ? "Daily" : series.cadenceKind === "Weekly" ? "Weekly" : "Custom",
    custom: custom ? (series.cadenceKind as CustomKind) : "EveryNDays",
    time: series.time,
    days: series.days.length ? series.days : ["Monday"],
    intervalDays: series.intervalDays > 1 ? series.intervalDays : 3,
    dates: [...series.dates].sort(),
    timeZoneId: series.timeZoneId,
    startDate: series.startDate,
    until: series.endDate ? "date" : "stop",
    endDate: series.endDate ?? "",
    name: series.name,
    caption: series.caption,
    notify: series.notify,
    postNow: false,
  };
}

/** What the server is sent for "how often". */
export function toCadence(form: SeriesForm): CadenceRequest {
  switch (form.frequency) {
    case "Daily":
      return { kind: "Daily", time: form.time };
    case "Weekly":
      return { kind: "Weekly", time: form.time, days: form.days };
    default:
      return form.custom === "EveryNDays"
        ? { kind: "EveryNDays", time: form.time, intervalDays: form.intervalDays }
        : { kind: "SpecificDates", time: form.time, dates: form.dates };
  }
}

/** What is missing before the When step can go on, in words, or null. */
export function whenProblem(form: SeriesForm): string | null {
  if (!/^\d{2}:\d{2}$/.test(form.time)) return "Choose a time of day.";
  if (form.frequency === "Weekly" && form.days.length === 0) return "Choose at least one day.";
  if (form.frequency === "Custom" && form.custom === "EveryNDays"
      && !(form.intervalDays >= 1 && form.intervalDays <= 365))
    return "Choose a gap between 1 and 365 days.";
  if (form.frequency === "Custom" && form.custom === "SpecificDates" && form.dates.length === 0)
    return "Add at least one date.";
  if (!/^\d{4}-\d{2}-\d{2}$/.test(form.startDate)) return "Choose a start date.";
  if (form.until === "date") {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(form.endDate)) return "Choose an end date, or post until you stop it.";
    if (form.endDate < form.startDate) return "The end date must be on or after the start date.";
  }
  return null;
}

/** Adds a date to the specific-dates list, kept sorted and without repeats. */
export function addDate(dates: string[], day: string): string[] {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(day) || dates.includes(day)) return dates;
  return [...dates, day].sort();
}

export interface StripDay {
  /** "yyyy-MM-dd" */
  date: string;
  /** "Mon" */
  weekday: string;
  /** "28" */
  day: string;
  /** "Oct" when the month starts, or on the first day of the strip. */
  month: string | null;
  isToday: boolean;
}

const WEEKDAY_SHORT = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const MONTH_SHORT = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** The next `count` calendar days, starting today on the zone's clock. */
export function stripDays(timeZoneId: string, count = 14, now: Date = new Date()): StripDay[] {
  const today = todayIn(timeZoneId, now);
  return Array.from({ length: count }, (_, i) => {
    const date = addDays(today, i);
    const [y, m, d] = date.split("-").map(Number);
    const weekday = WEEKDAY_SHORT[new Date(Date.UTC(y, m - 1, d)).getUTCDay()];
    return {
      date,
      weekday,
      day: String(d),
      month: i === 0 || d === 1 ? MONTH_SHORT[m - 1] : null,
      isToday: i === 0,
    };
  });
}

/** Occurrences grouped by local day, for the strip. */
export function byDay(times: SeriesTime[]): Map<string, SeriesTime[]> {
  const map = new Map<string, SeriesTime[]>();
  for (const t of times) {
    const list = map.get(t.localDate) ?? [];
    list.push(t);
    map.set(t.localDate, list);
  }
  return map;
}

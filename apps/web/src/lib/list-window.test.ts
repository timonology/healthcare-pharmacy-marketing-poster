import { describe, expect, it } from "vitest";
import {
  DEFAULT_WINDOW,
  hasDateRange,
  listWindowParams,
  parseListWindow,
  presetOf,
  presetRange,
  validDay,
  writeListWindow,
} from "./list-window";

// A fixed "today": Wednesday 22 October 2026, mid-afternoon local time.
const TODAY = new Date(2026, 9, 22, 15, 30);

describe("the URL", () => {
  it("reads sort, from and to", () => {
    expect(parseListWindow("?sort=oldest&from=2026-10-01&to=2026-10-31")).toEqual({
      sort: "oldest",
      from: "2026-10-01",
      to: "2026-10-31",
    });
  });

  it("defaults to newest first, all time", () => {
    expect(parseListWindow("")).toEqual(DEFAULT_WINDOW);
    expect(parseListWindow("?sort=sideways")).toEqual(DEFAULT_WINDOW);
  });

  it("ignores dates that are not real days", () => {
    expect(parseListWindow("?from=2026-02-31&to=yesterday")).toEqual(DEFAULT_WINDOW);
    expect(validDay("2026-13-01")).toBeNull();
    expect(validDay("2024-02-29")).toBe("2024-02-29");
  });

  it("swaps a reversed range rather than showing nothing", () => {
    expect(parseListWindow("?from=2026-10-31&to=2026-10-01")).toMatchObject({
      from: "2026-10-01",
      to: "2026-10-31",
    });
  });

  it("writes only what differs from the default and keeps other parameters", () => {
    const base = new URLSearchParams("use=tmpl-1&sort=oldest&from=2026-01-01");
    const next = writeListWindow(base, { sort: "newest", from: "2026-10-01", to: null });
    expect(next.get("use")).toBe("tmpl-1");
    expect(next.has("sort")).toBe(false);
    expect(next.get("from")).toBe("2026-10-01");
    expect(next.has("to")).toBe(false);
  });

  it("round-trips", () => {
    const w = { sort: "oldest" as const, from: "2026-09-01", to: "2026-09-30" };
    expect(parseListWindow(writeListWindow(new URLSearchParams(), w))).toEqual(w);
  });
});

describe("date presets", () => {
  it("Last 7 days is today and the six before it", () => {
    expect(presetRange("7d", TODAY)).toEqual({ from: "2026-10-16", to: "2026-10-22" });
  });

  it("Last 30 days is today and the 29 before it", () => {
    expect(presetRange("30d", TODAY)).toEqual({ from: "2026-09-23", to: "2026-10-22" });
  });

  it("This month runs from the 1st to today", () => {
    expect(presetRange("month", TODAY)).toEqual({ from: "2026-10-01", to: "2026-10-22" });
  });

  it("All time has no bounds", () => {
    expect(presetRange("all", TODAY)).toEqual({ from: null, to: null });
  });

  it("names the preset a range matches, and Custom otherwise", () => {
    expect(presetOf({ from: null, to: null }, TODAY)).toBe("all");
    expect(presetOf({ from: "2026-10-16", to: "2026-10-22" }, TODAY)).toBe("7d");
    expect(presetOf({ from: "2026-10-01", to: "2026-10-22" }, TODAY)).toBe("month");
    expect(presetOf({ from: "2026-10-02", to: "2026-10-05" }, TODAY)).toBe("custom");
  });

  it("a Last 7 days link opened a week later is Custom with its original dates", () => {
    const later = new Date(2026, 9, 29);
    expect(presetOf({ from: "2026-10-16", to: "2026-10-22" }, later)).toBe("custom");
  });
});

describe("to the API", () => {
  it("sends from as the start of the first day and to as the start of the day after the last", () => {
    const p = listWindowParams({ sort: "newest", from: "2026-10-01", to: "2026-10-31" });
    expect(p.sort).toBe("newest");
    expect(p.from).toBe(new Date(2026, 9, 1).toISOString());
    expect(p.to).toBe(new Date(2026, 10, 1).toISOString());
  });

  it("an item late on the last chosen day is inside the range", () => {
    const p = listWindowParams({ sort: "newest", from: "2026-10-31", to: "2026-10-31" });
    const lateOnThe31st = new Date(2026, 9, 31, 23, 59).getTime();
    expect(Date.parse(p.from!)).toBeLessThanOrEqual(lateOnThe31st);
    expect(Date.parse(p.to!)).toBeGreaterThan(lateOnThe31st);
  });

  it("sends no dates when cleared, and that is not a range", () => {
    const cleared = { sort: "oldest" as const, from: null, to: null };
    expect(listWindowParams(cleared)).toEqual({ sort: "oldest" });
    expect(hasDateRange(cleared)).toBe(false);
    expect(hasDateRange({ ...cleared, to: "2026-10-01" })).toBe(true);
  });
});

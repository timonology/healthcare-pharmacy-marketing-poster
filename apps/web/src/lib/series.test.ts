import { describe, expect, it } from "vitest";
import { addDate, emptyForm, stripDays, toCadence, todayIn, whenProblem } from "./series";

describe("the When step", () => {
  const base = emptyForm("Europe/London", new Date("2026-10-26T10:00:00Z"));

  it("sends weekly days, every-n days and specific dates as the server expects", () => {
    expect(toCadence({ ...base, frequency: "Weekly", days: ["Monday", "Thursday"] })).toEqual({
      kind: "Weekly",
      time: "09:00",
      days: ["Monday", "Thursday"],
    });
    expect(toCadence({ ...base, frequency: "Custom", custom: "EveryNDays", intervalDays: 3 })).toEqual({
      kind: "EveryNDays",
      time: "09:00",
      intervalDays: 3,
    });
    expect(toCadence({ ...base, frequency: "Custom", custom: "SpecificDates", dates: ["2026-11-02"] })).toEqual({
      kind: "SpecificDates",
      time: "09:00",
      dates: ["2026-11-02"],
    });
    expect(toCadence({ ...base, frequency: "Daily" })).toEqual({ kind: "Daily", time: "09:00" });
  });

  it("says what is missing before it lets them go on", () => {
    expect(whenProblem({ ...base, frequency: "Weekly", days: [] })).toMatch(/day/);
    expect(whenProblem({ ...base, frequency: "Custom", custom: "SpecificDates", dates: [] })).toMatch(/date/);
    expect(whenProblem({ ...base, until: "date", endDate: "" })).toMatch(/end date/);
    expect(whenProblem({ ...base, until: "date", endDate: "2026-10-01" })).toMatch(/on or after/);
    expect(whenProblem(base)).toBeNull();
  });

  it("keeps specific dates sorted and unique", () => {
    expect(addDate(["2026-11-09"], "2026-11-02")).toEqual(["2026-11-02", "2026-11-09"]);
    expect(addDate(["2026-11-02"], "2026-11-02")).toEqual(["2026-11-02"]);
    expect(addDate([], "not-a-date")).toEqual([]);
  });
});

describe("the calendar strip", () => {
  it("is 14 days from today on the pharmacy's clock", () => {
    const days = stripDays("Europe/London", 14, new Date("2026-10-26T10:00:00Z"));
    expect(days).toHaveLength(14);
    expect(days[0]).toMatchObject({ date: "2026-10-26", weekday: "Mon", isToday: true, month: "Oct" });
    expect(days[6].date).toBe("2026-11-01");
    expect(days[6].month).toBe("Nov");
    expect(days[13].date).toBe("2026-11-08");
  });

  it("uses the pharmacy's date, not UTC's, late in the evening", () => {
    // 23:30 UTC on 31 Oct is already 1 Nov in Paris.
    expect(todayIn("Europe/Paris", new Date("2026-10-31T23:30:00Z"))).toBe("2026-11-01");
    expect(todayIn("Europe/London", new Date("2026-10-31T23:30:00Z"))).toBe("2026-10-31");
  });
});

import { describe, expect, it } from "vitest";
import { fitsX, xWeightedLength, X_MAX_WEIGHTED_LENGTH } from "./x-text";

// The same cases as XTextTests on the server: the two must agree.
describe("xWeightedLength", () => {
  it("counts plain Latin text one per character", () => {
    expect(xWeightedLength("Flu jabs now available")).toBe(22);
    expect(xWeightedLength("")).toBe(0);
    expect(xWeightedLength(null)).toBe(0);
  });

  it("counts accented letters once, composed or not", () => {
    expect(xWeightedLength("café")).toBe(4);
    expect(xWeightedLength("café")).toBe(4);
  });

  it("counts every link as 23, long or short", () => {
    expect(xWeightedLength("https://example.com")).toBe(23);
    expect(xWeightedLength("Book: https://example.com/a/very/long/path?with=query")).toBe(6 + 23);
    expect(xWeightedLength("Visit pharmacy.co.uk today")).toBe(6 + 23 + 6);
    expect(xWeightedLength("www.nhs.uk")).toBe(23);
  });

  it("does not count an email address as a link", () => {
    expect(xWeightedLength("me@x.co")).toBe(7); // "x.co" straight after @ is not a link
  });

  it("counts emoji as 2, including joined sequences", () => {
    expect(xWeightedLength("💊")).toBe(2);
    expect(xWeightedLength("👩‍⚕️")).toBe(2);
    expect(xWeightedLength("👍🏽")).toBe(2);
    expect(xWeightedLength("❤️")).toBe(2);
  });

  it("counts CJK as 2", () => {
    expect(xWeightedLength("薬局")).toBe(4);
  });

  it("counts curly quotes and dashes as 1", () => {
    expect(xWeightedLength("“hi” — ok")).toBe(9);
  });

  it("fits at the limit, not over it", () => {
    expect(fitsX("a".repeat(X_MAX_WEIGHTED_LENGTH))).toBe(true);
    expect(fitsX("a".repeat(X_MAX_WEIGHTED_LENGTH + 1))).toBe(false);
    expect(fitsX("💊".repeat(140))).toBe(true);
    expect(fitsX("💊".repeat(141))).toBe(false);
  });
});

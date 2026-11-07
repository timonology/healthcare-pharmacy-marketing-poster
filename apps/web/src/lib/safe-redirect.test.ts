import { describe, expect, it } from "vitest";
import { safeRedirect } from "./safe-redirect";

const ORIGIN = "https://app.marketing25.test";

/** Security review R2-04: no way off this origin after sign-in. */
describe("safeRedirect", () => {
  it.each([
    ["/dashboard", "/dashboard"],
    ["/posters/abc?tab=edit#top", "/posters/abc?tab=edit#top"],
    ["/a/../dashboard", "/dashboard"],
  ])("keeps our own path %s", (raw, expected) => {
    expect(safeRedirect(raw, ORIGIN)).toBe(expected);
  });

  it.each([
    "//evil.example",
    "/\\evil.example",
    "/\\/evil.example",
    "/\t/evil.example",
    "/%5Cevil.example".replace("%5C", "\\"),
    "https://evil.example/",
    "javascript:alert(1)",
    "evil.example",
    "",
  ])("refuses %j", (raw) => {
    expect(safeRedirect(raw, ORIGIN)).toBeNull();
  });

  it("refuses null and undefined", () => {
    expect(safeRedirect(null, ORIGIN)).toBeNull();
    expect(safeRedirect(undefined, ORIGIN)).toBeNull();
  });
});

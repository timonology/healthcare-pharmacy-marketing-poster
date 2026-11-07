import { describe, expect, it } from "vitest";
import { isSafeApiPath } from "./api-path";

describe("isSafeApiPath (security review R3-05)", () => {
  it.each([
    "/api/posters/abc/export.png",
    "/api/admin/users?skip=0&take=20",
    "/api/templates/sys-house-flu-vaccination",
    "/api/social/connections/abc%2F..%2Fx", // still encoded: fetch will not walk it
  ])("allows %s", (path) => expect(isSafeApiPath(path)).toBe(true));

  it.each([
    "/api/posters/../admin/users/export.png",
    "/api/posters/../../health",
    "/api/admin/./users",
    "/api/posters/x#fragment",
    "/api/posters/x\\..\\admin",
    "/health",
    "//evil.example/api/x",
  ])("refuses %s", (path) => expect(isSafeApiPath(path)).toBe(false));

  it("does not look for dot-segments in the query string", () => {
    expect(isSafeApiPath("/api/admin/audit?search=../x")).toBe(true);
  });
});

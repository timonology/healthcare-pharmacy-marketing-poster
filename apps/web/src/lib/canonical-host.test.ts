import { describe, expect, it } from "vitest";
import { canonicalRedirect } from "./canonical-host";

const SITE = "https://marketing25.co.uk";

describe("canonicalRedirect", () => {
  it("sends www to the bare domain, keeping path and query", () => {
    expect(canonicalRedirect("www.marketing25.co.uk", "/social?tab=connections", SITE)).toBe(
      "https://marketing25.co.uk/social?tab=connections",
    );
  });

  it("ignores case, a port and a forwarded list", () => {
    expect(canonicalRedirect("WWW.Marketing25.co.uk:443, 10.0.0.1", "/", SITE)).toBe(
      "https://marketing25.co.uk/",
    );
  });

  it("leaves the canonical host alone, so there is no loop", () => {
    expect(canonicalRedirect("marketing25.co.uk", "/", SITE)).toBeNull();
  });

  it("leaves every other host alone", () => {
    for (const host of ["localhost:3000", "sonar-marketing-web.azurewebsites.net", "evil.example", "www.evil.example"]) {
      expect(canonicalRedirect(host, "/", SITE)).toBeNull();
    }
  });

  it("does nothing when the canonical address is itself www", () => {
    expect(canonicalRedirect("www.marketing25.co.uk", "/", "https://www.marketing25.co.uk")).toBeNull();
  });

  it("does nothing without a host or with a broken SITE_URL", () => {
    expect(canonicalRedirect(null, "/", SITE)).toBeNull();
    expect(canonicalRedirect("www.marketing25.co.uk", "/", "not a url")).toBeNull();
  });
});

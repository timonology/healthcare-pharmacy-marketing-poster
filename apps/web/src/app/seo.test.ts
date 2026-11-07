import { afterEach, describe, expect, it, vi } from "vitest";
import robots from "./robots";
import sitemap from "./sitemap";

/**
 * The search-engine surface: robots.txt keeps crawlers in the public site,
 * the sitemap lists it and the templates, and both name the configured host.
 */

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe("robots.txt", () => {
  it("lets crawlers into the public pages and keeps them out of the app", () => {
    vi.stubEnv("SITE_URL", "https://example.test");
    const r = robots();
    const rule = Array.isArray(r.rules) ? r.rules[0] : r.rules;
    expect(rule.allow).toBe("/");
    for (const area of ["/api/", "/dashboard", "/posters", "/admin", "/patients", "/campaigns"]) {
      expect(rule.disallow).toContain(area);
    }
    for (const open of ["/templates", "/pricing", "/register", "/privacy", "/terms"]) {
      expect(rule.disallow).not.toContain(open);
    }
    expect(r.sitemap).toBe("https://example.test/sitemap.xml");
  });
});

describe("sitemap.xml", () => {
  it("lists the public pages and every published template", async () => {
    vi.stubEnv("SITE_URL", "https://example.test/");
    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({
      items: [{ id: "sys-house-flu-vaccination", updatedAtUtc: "2026-10-01T00:00:00Z" }],
      total: 1,
    }))));

    const urls = (await sitemap()).map((e) => e.url);
    expect(urls).toContain("https://example.test/");
    expect(urls).toContain("https://example.test/pricing");
    expect(urls).toContain("https://example.test/templates/sys-house-flu-vaccination");
    expect(urls.some((u) => u.includes("/dashboard"))).toBe(false);
  });

  it("still lists the public pages when the API is down", async () => {
    vi.stubEnv("SITE_URL", "https://example.test");
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("ECONNREFUSED"); }));

    const urls = (await sitemap()).map((e) => e.url);
    expect(urls).toContain("https://example.test/templates");
    expect(urls.filter((u) => u.includes("/templates/"))).toEqual([]);
  });
});

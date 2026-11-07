import { describe, expect, it } from "vitest";
import { buildCsp, newNonce } from "./csp";

describe("Content-Security-Policy (SEC-09)", () => {
  it("allows scripts only with this request's nonce", () => {
    const csp = buildCsp("abc123");
    const script = csp.split("; ").find((d) => d.startsWith("script-src "))!;
    expect(script).toContain("'nonce-abc123'");
    expect(script).toContain("'strict-dynamic'");
    expect(script).not.toContain("'unsafe-inline'");
    expect(script).not.toContain("'unsafe-eval'");
  });

  it("allows eval only in development", () => {
    expect(buildCsp("n", { dev: true })).toContain("'unsafe-eval'");
  });

  it("blocks plugins and base-tag hijacks, and reports violations", () => {
    const csp = buildCsp("n");
    expect(csp).toContain("object-src 'none'");
    expect(csp).toContain("base-uri 'self'");
    expect(csp).toContain("report-uri /api/csp-report");
  });

  it("makes a fresh, unguessable nonce each time", () => {
    const a = newNonce();
    const b = newNonce();
    expect(a).not.toBe(b);
    expect(atob(a)).toHaveLength(16);
  });
});

describe("frame-ancestors", () => {
  it("is only added when asked (enforce mode), never in report-only", () => {
    expect(buildCsp("n")).not.toContain("frame-ancestors");
    expect(buildCsp("n", { frameAncestors: "'none'" })).toContain("frame-ancestors 'none'");
  });
});

import { describe, expect, it } from "vitest";
import { clientAddress, normaliseAddress, signClientIp } from "./forwarding";

/** Security review R2-03: the address passed to the API is the platform's, and signed. */
describe("clientAddress", () => {
  it("takes the entry the front end appended, not the caller's own", () => {
    expect(clientAddress("6.6.6.6, 203.0.113.7", null)).toBe("203.0.113.7");
    expect(clientAddress("1.1.1.1, 2.2.2.2, 203.0.113.7:51234", null)).toBe("203.0.113.7");
  });

  it("counts further hops when another proxy sits in front", () => {
    expect(clientAddress("6.6.6.6, 203.0.113.7, 10.0.0.4", null, 2)).toBe("203.0.113.7");
  });

  it("falls back to x-real-ip, and to nothing", () => {
    expect(clientAddress(null, "198.51.100.2")).toBe("198.51.100.2");
    expect(clientAddress(null, null)).toBeNull();
  });

  it("refuses anything that is not an address", () => {
    expect(clientAddress("not-an-ip", null)).toBeNull();
    expect(clientAddress("999.1.1.1", null)).toBeNull();
  });
});

describe("normaliseAddress", () => {
  it.each([
    ["203.0.113.7", "203.0.113.7"],
    ["203.0.113.7:8080", "203.0.113.7"],
    ["[2001:db8::1]:443", "2001:db8::1"],
    ["2001:db8::1", "2001:db8::1"],
  ])("%s → %s", (raw, expected) => {
    expect(normaliseAddress(raw)).toBe(expected);
  });
});

describe("signClientIp", () => {
  it("matches the API's signature for the same key, address and minute", () => {
    // Asserted with the same vector in Acme.Tests SecurityFollowupFixTests.
    expect(
      signClientIp("test-client-ip-key-0123456789abcdef", "203.0.113.7", 29_500_000 * 60_000),
    ).toBe("Lx2LqHyIx0sny-WwI4erc2aKJCW-Pp-wqyral1WdUbw");
  });
});

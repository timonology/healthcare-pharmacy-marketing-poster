import { describe, expect, it } from "vitest";
import { expiryState, reconnectKind } from "./social-connections";

describe("reconnecting", () => {
  it("sends each platform back through the sign-in that made it", () => {
    expect(reconnectKind("FacebookPage")).toBe("Meta");
    expect(reconnectKind("InstagramBusiness")).toBe("Meta");
    expect(reconnectKind("YouTubeChannel")).toBe("YouTube");
    expect(reconnectKind("LinkedInProfile")).toBe("LinkedIn");
    expect(reconnectKind("LinkedInPage")).toBe("LinkedIn");
    expect(reconnectKind("TikTokAccount")).toBe("TikTok");
    expect(reconnectKind("XAccount")).toBe("X");
    expect(reconnectKind("GoogleAds")).toBe("Google");
  });
});

describe("expiring sign-ins", () => {
  const now = new Date("2026-10-29T12:00:00Z");

  it("says nothing for a connection with no expiry or plenty of time", () => {
    expect(expiryState({ status: "Active", expiresAtUtc: null }, now).kind).toBe("none");
    expect(expiryState({ status: "Active", expiresAtUtc: "2026-12-20T00:00:00Z" }, now).kind).toBe("none");
  });

  it("warns within two weeks, and says expired once it has", () => {
    expect(expiryState({ status: "Active", expiresAtUtc: "2026-11-05T00:00:00Z" }, now).kind).toBe("soon");
    expect(expiryState({ status: "Active", expiresAtUtc: "2026-10-28T00:00:00Z" }, now).kind).toBe("expired");
  });

  it("leaves a connection already marked for reconnecting to its own message", () => {
    expect(expiryState({ status: "NeedsReauth", expiresAtUtc: "2026-10-28T00:00:00Z" }, now).kind).toBe("none");
  });
});

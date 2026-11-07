import { describe, expect, it } from "vitest";
import type { NextResponse } from "next/server";
import {
  MANAGE_AS_COOKIE,
  MANAGE_AS_LABEL_COOKIE,
  WORKSPACE_COOKIE,
  applyWorkspaceCookies,
  clearAuthCookies,
} from "./cookies";

type Written = { value: string; options: { maxAge?: number; expires?: Date; httpOnly?: boolean } };

function recordingResponse() {
  const written = new Map<string, Written>();
  const response = {
    cookies: {
      set: (name: string, value: string, options: Written["options"] = {}) => {
        written.set(name, { value, options });
      },
    },
  } as unknown as NextResponse;
  return { response, written };
}

const inAnHour = () => new Date(Date.now() + 60 * 60 * 1000).toISOString();

describe("signed-out people are not told they are managing another pharmacy", () => {
  // The name label lived thirty days; the session it labelled lived an hour.
  // After signing out, the banner still said "you are managing New Home".
  it("the name label expires with the delegated token, not weeks later", () => {
    const { response, written } = recordingResponse();

    applyWorkspaceCookies(response, {
      token: "t", expiresAtUtc: inAnHour(), pharmacyId: "p2", pharmacyName: "New Home",
    });

    const token = written.get(MANAGE_AS_COOKIE)!.options.maxAge!;
    const label = written.get(MANAGE_AS_LABEL_COOKIE)!.options.maxAge!;
    expect(label).toBe(token);
    expect(label).toBeLessThan(2 * 60 * 60);
  });

  it("the delegated token is never readable by page scripts", () => {
    const { response, written } = recordingResponse();

    applyWorkspaceCookies(response, {
      token: "t", expiresAtUtc: inAnHour(), pharmacyId: "p2", pharmacyName: "New Home",
    });

    expect(written.get(MANAGE_AS_COOKIE)!.options.httpOnly).toBe(true);
  });

  it("signing out clears the delegated token, the workspace and the label", () => {
    // Leaving the delegated token behind meant signing out while managing
    // another pharmacy did not sign you out: requests still carried it.
    const { response, written } = recordingResponse();

    clearAuthCookies(response);

    for (const name of [MANAGE_AS_COOKIE, WORKSPACE_COOKIE, MANAGE_AS_LABEL_COOKIE]) {
      expect(written.get(name)?.value).toBe("");
      expect(written.get(name)!.options.expires!.getTime()).toBe(0);
    }
  });
});

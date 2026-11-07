import { describe, expect, it } from "vitest";
import type { TikTokCreatorInfo } from "@acme/shared-types";
import {
  commercialLabel,
  consentText,
  emptyTikTokForm,
  privacyDisabled,
  tikTokProblem,
  toTikTokOptions,
} from "./tiktok-post";

const info: TikTokCreatorInfo = {
  username: "highstpharmacy",
  nickname: "High St Pharmacy",
  avatarUrl: null,
  privacyLevelOptions: ["PUBLIC_TO_EVERYONE", "MUTUAL_FOLLOW_FRIENDS", "SELF_ONLY"],
  commentDisabled: false,
  duetDisabled: true,
  stitchDisabled: false,
  maxVideoPostDurationSec: 600,
};

describe("the TikTok share settings", () => {
  it("has no audience until one is chosen, and posts nothing without one", () => {
    const form = emptyTikTokForm();
    expect(form.privacyLevel).toBe("");
    expect(tikTokProblem(form, info)).toMatch(/Choose who can see/);
    expect(toTikTokOptions(form, info)).toBeNull();
  });

  it("starts with comments, Duet and Stitch off", () => {
    const form = emptyTikTokForm();
    expect([form.allowComments, form.allowDuet, form.allowStitch]).toEqual([false, false, false]);
  });

  it("refuses an audience TikTok does not offer this account", () => {
    expect(tikTokProblem({ ...emptyTikTokForm(), privacyLevel: "FOLLOWER_OF_CREATOR" }, info)).toMatch(/not available/);
  });

  it("waits for TikTok's answer before allowing a post", () => {
    expect(tikTokProblem({ ...emptyTikTokForm(), privacyLevel: "SELF_ONLY" }, null)).toMatch(/Waiting/);
  });

  it("needs a kind of commercial content once the switch is on", () => {
    const form = { ...emptyTikTokForm(), privacyLevel: "PUBLIC_TO_EVERYONE" as const, discloseCommercial: true };
    expect(tikTokProblem(form, info)).toMatch(/Your brand/);
    expect(tikTokProblem({ ...form, promotesOwnBusiness: true }, info)).toBeNull();
    expect(commercialLabel({ ...form, promotesOwnBusiness: true })).toMatch(/Promotional content/);
  });

  it("does not let branded content be private", () => {
    const form = { ...emptyTikTokForm(), privacyLevel: "SELF_ONLY" as const, discloseCommercial: true, isPaidPartnership: true };
    expect(tikTokProblem(form, info)).toMatch(/cannot be private/);
    expect(privacyDisabled(form, "SELF_ONLY")).toBe(true);
    expect(privacyDisabled(form, "PUBLIC_TO_EVERYONE")).toBe(false);
    expect(commercialLabel(form)).toMatch(/Paid partnership/);
  });

  it("changes the consent wording for branded content", () => {
    expect(consentText(emptyTikTokForm())).toBe("By posting, you agree to TikTok's Music Usage Confirmation.");
    expect(consentText({ ...emptyTikTokForm(), discloseCommercial: true, isPaidPartnership: true })).toMatch(/Branded Content Policy/);
  });

  it("sends an interaction the creator switched off as off", () => {
    const options = toTikTokOptions(
      { ...emptyTikTokForm(), privacyLevel: "PUBLIC_TO_EVERYONE", allowComments: true, allowDuet: true },
      info,
    );
    expect(options).toEqual({
      privacyLevel: "PUBLIC_TO_EVERYONE",
      allowComments: true,
      allowDuet: false,   // duet_disabled
      allowStitch: false,
      promotesOwnBusiness: false,
      isPaidPartnership: false,
    });
  });

  it("drops a declaration when the commercial switch is off again", () => {
    const options = toTikTokOptions(
      { ...emptyTikTokForm(), privacyLevel: "SELF_ONLY", discloseCommercial: false, isPaidPartnership: true },
      info,
    );
    expect(options?.isPaidPartnership).toBe(false);
  });
});

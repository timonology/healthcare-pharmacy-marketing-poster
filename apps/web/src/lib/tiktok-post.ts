import type { TikTokCreatorInfo, TikTokPostOptions, TikTokPrivacyLevel } from "@acme/shared-types";

/**
 * The TikTok part of the share screen, as TikTok's Content Sharing
 * Guidelines require it:
 *
 * - the audience is chosen from the options TikTok returns for the account,
 *   with **no default** — nothing is posted until someone picks one;
 * - comments, Duet and Stitch start **off**, and stay off (greyed) when the
 *   creator has switched them off in TikTok;
 * - commercial content is declared with a switch and, when on, at least one
 *   of "Your brand" or "Branded content"; branded content cannot be private;
 * - the consent line changes with what was declared.
 */
export interface TikTokForm {
  privacyLevel: TikTokPrivacyLevel | "";
  allowComments: boolean;
  allowDuet: boolean;
  allowStitch: boolean;
  discloseCommercial: boolean;
  promotesOwnBusiness: boolean;
  isPaidPartnership: boolean;
}

export const emptyTikTokForm = (): TikTokForm => ({
  privacyLevel: "",
  allowComments: false,
  allowDuet: false,
  allowStitch: false,
  discloseCommercial: false,
  promotesOwnBusiness: false,
  isPaidPartnership: false,
});

const LABELS: Record<TikTokPrivacyLevel, string> = {
  PUBLIC_TO_EVERYONE: "Everyone",
  MUTUAL_FOLLOW_FRIENDS: "Friends (followers you follow back)",
  FOLLOWER_OF_CREATOR: "Followers",
  SELF_ONLY: "Only me",
};

export const privacyLabel = (level: TikTokPrivacyLevel): string => LABELS[level] ?? level;

/** What stops posting, in the words shown beside the button; null when ready. */
export function tikTokProblem(form: TikTokForm, info: TikTokCreatorInfo | null): string | null {
  if (!info) return "Waiting for TikTok to confirm the account's settings.";
  if (!form.privacyLevel) return "Choose who can see the TikTok post.";
  if (!info.privacyLevelOptions.includes(form.privacyLevel))
    return "That audience is not available for this TikTok account. Choose again.";
  if (form.discloseCommercial && !form.promotesOwnBusiness && !form.isPaidPartnership)
    return 'You turned on commercial content: choose "Your brand", "Branded content" or both.';
  if (form.discloseCommercial && form.isPaidPartnership && form.privacyLevel === "SELF_ONLY")
    return "Branded content cannot be private. Choose another audience.";
  return null;
}

/** Whether an audience can be chosen right now (branded content rules out "Only me"). */
export function privacyDisabled(form: TikTokForm, level: TikTokPrivacyLevel): boolean {
  return form.discloseCommercial && form.isPaidPartnership && level === "SELF_ONLY";
}

/** TikTok's required consent wording for what was declared. */
export function consentText(form: TikTokForm): string {
  const branded = form.discloseCommercial && form.isPaidPartnership;
  return branded
    ? "By posting, you agree to TikTok's Branded Content Policy and Music Usage Confirmation."
    : "By posting, you agree to TikTok's Music Usage Confirmation.";
}

/** The label TikTok will put on the post, if any. */
export function commercialLabel(form: TikTokForm): string | null {
  if (!form.discloseCommercial) return null;
  if (form.isPaidPartnership) return 'Your video will be labelled "Paid partnership".';
  if (form.promotesOwnBusiness) return 'Your video will be labelled "Promotional content".';
  return null;
}

/** What the API is sent. Interactions the creator disabled are sent off. */
export function toTikTokOptions(form: TikTokForm, info: TikTokCreatorInfo): TikTokPostOptions | null {
  if (tikTokProblem(form, info) || !form.privacyLevel) return null;
  return {
    privacyLevel: form.privacyLevel,
    allowComments: form.allowComments && !info.commentDisabled,
    allowDuet: form.allowDuet && !info.duetDisabled,
    allowStitch: form.allowStitch && !info.stitchDisabled,
    promotesOwnBusiness: form.discloseCommercial && form.promotesOwnBusiness,
    isPaidPartnership: form.discloseCommercial && form.isPaidPartnership,
  };
}

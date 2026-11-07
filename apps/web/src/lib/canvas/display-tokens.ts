/**
 * What a `{{token}}` shows on screen, without changing the stored design.
 *
 * <b>The stored text keeps its tokens.</b> A pharmacy that signed up and
 * skipped onboarding has no details yet, so its posters still say
 * `{{address}}` underneath. Shown raw, that is a mistake anyone can see; filled
 * by rewriting the text, it would be lost for good — and the moment the
 * pharmacy finishes onboarding the poster should simply fill in, as one made
 * afterwards would. So the editor *draws* the value (or a blank) and saves the
 * token, and print does the same on the API (`BrandCanvasAssets.ResolveTokens`).
 *
 * Values follow the print rules exactly, so screen and paper agree: the
 * address carries its postcode, and anything not set is a gap.
 */
import type { PharmacyProfile } from "@acme/shared-types";
import { TOKENS, type ReplacementValues } from "./apply-pharmacy";

export type TokenValues = Partial<ReplacementValues>;

/** The values a token resolves to for this pharmacy. Missing details are blanks. */
export function tokenValuesFrom(profile: Partial<PharmacyProfile> | null | undefined): TokenValues {
  const t = (v: string | null | undefined) => v?.trim() ?? "";
  return {
    pharmacyName: t(profile?.pharmacyName),
    // As printed: "12 High Street, AB1 2CD".
    address: [t(profile?.address), t(profile?.postCode)].filter(Boolean).join(", "),
    postCode: t(profile?.postCode),
    contactName: t(profile?.contactName),
    contactPhone: t(profile?.contactPhone),
    bookingUrl: t(profile?.bookingUrl),
  };
}

/**
 * The text as it should appear: each token replaced by its value, or nothing.
 * `null` values (not loaded yet) also blank — a token flashing on screen while
 * the profile loads is the thing this exists to stop.
 */
export function displayTokens(text: string, values: TokenValues | null): string {
  if (!text.includes("{{")) return text;
  let out = text;
  for (const [pattern, key] of TOKENS) {
    pattern.lastIndex = 0;
    out = out.replace(pattern, values?.[key] ?? "");
  }
  return out;
}

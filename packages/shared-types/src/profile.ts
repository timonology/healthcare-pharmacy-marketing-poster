import type { SubscriptionTier } from "./subscription";

export interface PharmacyProfile {
  pharmacyName: string;
  address: string;
  postCode: string;
  description: string;
  contactName: string;
  contactPhone: string;
  sonarFCode: string | null;
  onboardingCompleted: boolean;

  /**
   * Where a patient books an appointment, or null if none is set.
   *
   * **Read-only here.** The value is stored on the brand kit — see
   * {@link BrandKit} — and the profile shows it so a pharmacist looking at
   * their details can see what patients will scan. To change it, go to Brand
   * Kit; {@link UpsertProfileRequest} does not accept it, so there is exactly
   * one copy and one place that writes it.
   *
   * Null also means "no brand kit yet", which is the same thing from the
   * pharmacist's point of view: nothing for a QR code to point at.
   *
   * Always absolute and always http/https: the API normalises it on save, so
   * anything read from here is safe to put straight into an href or a QR.
   */
  bookingUrl: string | null;
}

export interface Me {
  id: string;
  email: string;
  displayName: string;
  tier: SubscriptionTier;
  profile: PharmacyProfile;
  createdAtUtc: string;
  role: import("./admin").UserRole;
}

export interface UpsertProfileRequest {
  pharmacyName: string;
  address: string;
  postCode: string;
  description: string;
  contactName: string;
  contactPhone: string;
  sonarFCode?: string | null;

  // No bookingUrl. It is written on the brand kit and only displayed here —
  // two write paths for one patient-facing URL is how a pharmacy ends up with a
  // printed QR code pointing somewhere its own profile page disagrees with.
}

export interface OnboardRequest extends UpsertProfileRequest {
  tier?: SubscriptionTier;
}

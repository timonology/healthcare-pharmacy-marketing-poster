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
}

export interface Me {
  id: string;
  email: string;
  displayName: string;
  tier: SubscriptionTier;
  profile: PharmacyProfile;
  createdAtUtc: string;
}

export interface UpsertProfileRequest {
  pharmacyName: string;
  address: string;
  postCode: string;
  description: string;
  contactName: string;
  contactPhone: string;
  sonarFCode?: string | null;
}

export interface OnboardRequest extends UpsertProfileRequest {
  tier?: SubscriptionTier;
}

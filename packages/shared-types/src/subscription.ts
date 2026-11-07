export type SubscriptionTier = "Free" | "Starter" | "Pro";

export const TIERS: SubscriptionTier[] = ["Free", "Starter", "Pro"];

export const UNLIMITED = -1;

export interface Plan {
  tier: SubscriptionTier;
  displayName: string;
  monthlyPriceGbp: number;
  maxPosters: number;
  maxAiGenerationsPerMonth: number;
  maxCampaignRecipientsPerMonth: number;
  maxPatients: number;
  maxVideosPerMonth: number;
  aiVideoEnabled: boolean;
  watermark: boolean;
  analyticsDashboard: boolean;
  highlights: string[];
  /** Recurring social series active at once. 0: not on this plan. */
  maxRecurringSeries?: number;
}

export interface Usage {
  posters: number;
  aiGenerationsThisMonth: number;
  campaignRecipientsThisMonth: number;
  patients: number;
  videosThisMonth: number;
}

export interface CurrentSubscription {
  plan: Plan;
  usage: Usage;
}

export interface UpgradeRequest {
  tier: SubscriptionTier;
}

export function isUnlimited(value: number): boolean {
  return value === UNLIMITED;
}

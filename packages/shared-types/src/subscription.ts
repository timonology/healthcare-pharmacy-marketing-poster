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
  watermark: boolean;
  customTemplates: boolean;
  emailExport: boolean;
  teamCollaboration: boolean;
  customBranding: boolean;
  analyticsDashboard: boolean;
  priorityAi: boolean;
  highlights: string[];
}

export interface Usage {
  posters: number;
  aiGenerationsThisMonth: number;
  campaignRecipientsThisMonth: number;
  patients: number;
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

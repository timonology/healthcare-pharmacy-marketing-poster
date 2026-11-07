import type { SubscriptionTier } from "./subscription";

export type BillingInterval = "Monthly" | "Yearly";

/**
 * Mirrors the .NET SubscriptionStatus enum, serialised as its name.
 * "PastDue" still grants the paid tier — Stripe is mid-dunning and will
 * cancel on our behalf if retries are exhausted.
 */
export type BillingStatus =
  | "None"
  | "Active"
  | "PastDue"
  | "Canceled"
  | "Incomplete"
  | "Unknown";

export interface CreditPack {
  key: string;
  displayName: string;
  credits: number;
  priceGbp: number;
}

export interface BillingStatusResponse {
  /** False when the API has no Stripe credentials — hide checkout UI. */
  stripeEnabled: boolean;
  tier: SubscriptionTier;
  status: BillingStatus;
  currentPeriodEndUtc: string | null;
  cancelAtPeriodEnd: boolean;
  videoCredits: number;
  hasPaymentMethod: boolean;
  creditPacks: CreditPack[];
  /**
   * False while somebody is working as this pharmacy (a head office managing a
   * branch, a colleague, support) rather than as its own account holder. They
   * can see the plan; changing it, buying top-ups and the billing portal are
   * refused by the API (security review R3-06).
   */
  canManageBilling: boolean;
}

export interface StartCheckoutRequest {
  tier: SubscriptionTier;
  interval: BillingInterval;
}

export interface BuyCreditsRequest {
  packKey: string;
}

/** The hosted Stripe URL to send the browser to. */
export interface CheckoutSession {
  url: string;
}

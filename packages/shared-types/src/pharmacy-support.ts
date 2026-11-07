import type { CampaignChannel, CampaignStatus, RecipientStatus } from "./campaign";
import type { PosterStatus } from "./poster";
import type { SubscriptionTier } from "./subscription";
import type { VideoStatus } from "./video";

/**
 * The admin support view of one pharmacy.
 *
 * These types describe a read-only window onto a customer's own workspace, for
 * answering support queries. Two things about them are deliberate:
 *
 *  1. Patient contact details arrive **masked**. The API does the masking, so
 *     the full address is not sitting in the browser behind a CSS blur — it
 *     was never sent. Unmasking is a separate, audited request.
 *
 *  2. Nothing here mutates the pharmacy's data. The single exception is
 *     resending a campaign that has already failed, which is its own endpoint
 *     and its own audit entry.
 */

/* ------------------------------------------------------------------ *
 * Overview
 * ------------------------------------------------------------------ */

export interface PharmacySupportOverview {
  userId: string;
  pharmacyName: string;
  contactName: string;
  email: string;
  postCode: string;
  tier: SubscriptionTier;
  isActive: boolean;
  onboardingCompleted: boolean;
  managedByAdminName: string | null;
  billingStatus: string;
  videoCredits: number;
  lastLoginAtUtc: string | null;
  createdAtUtc: string;

  patientCount: number;
  groupCount: number;
  posterCount: number;
  publishedPosterCount: number;
  videoCount: number;
  campaignCount: number;
  campaignsSentCount: number;
  /** Campaigns currently in the Failed state — the usual reason for a query. */
  campaignsFailedCount: number;
  /** Sum of per-campaign sent counts. Messages delivered, not patients on file. */
  recipientsReached: number;
  lastCampaignAtUtc: string | null;

  /**
   * Where this pharmacy sits in a group, if it is in one.
   *
   * Optional because it is newer than some deployed APIs: an instance that has
   * not been updated yet omits it, and the UI has to survive that rather than
   * crash the whole support page over one panel.
   */
  hierarchy?: PharmacyHierarchy;
}

/**
 * A pharmacy's position in a group.
 *
 * A pharmacy is either a parent or a branch, never both: `parentId` set means
 * `branches` is empty, and vice versa. Both fields are always present so the
 * UI can render either case without a second request.
 */
export interface PharmacyHierarchy {
  parentId: string | null;
  /**
   * `"Unknown pharmacy"` when a parent id points at an account that no longer
   * exists — an orphaned branch, which is worth seeing rather than hiding.
   */
  parentName: string | null;
  branches: PharmacyBranch[];
}

export interface PharmacyBranch {
  userId: string;
  pharmacyName: string;
  contactName: string;
  email: string;
  isActive: boolean;
  /** Invited but never activated: nobody has claimed the account yet. */
  awaitingActivation: boolean;
}

/* ------------------------------------------------------------------ *
 * Patients
 * ------------------------------------------------------------------ */

/**
 * A patient as an administrator sees them.
 *
 * `email` and `phone` are masked renderings such as `j••••@gmail.com` and
 * `•••••••0123`, produced server-side. They are display strings, not data —
 * never use them to contact anyone, and never try to reverse them.
 *
 * `hasEmail` / `hasPhone` exist so the UI can distinguish "no email on file"
 * from "email withheld" without needing the value.
 */
export interface SupportPatient {
  id: string;
  fullName: string;
  maskedEmail: string | null;
  maskedPhone: string | null;
  hasEmail: boolean;
  hasPhone: boolean;
  groupNames: string[];
  notes: string;
  createdAtUtc: string;
}

export interface SupportPatientList {
  items: SupportPatient[];
  total: number;
  skip: number;
  take: number;
}

/** The result of an audited reveal. Both fields are the real values. */
export interface RevealedPatientContact {
  patientId: string;
  fullName: string;
  email: string | null;
  phone: string | null;
  /** Echoed back so the UI can show what was written to the audit trail. */
  reason: string;
}

export interface SupportGroup {
  id: string;
  name: string;
  description: string;
  patientCount: number;
  createdAtUtc: string;
}

/* ------------------------------------------------------------------ *
 * Posters and videos
 * ------------------------------------------------------------------ */

export interface SupportPoster {
  id: string;
  name: string;
  status: PosterStatus;
  sourceTemplateId: string | null;
  thumbnailUrl: string | null;
  /** How many campaigns have used this poster. Explains "why can't I delete it?". */
  usedInCampaigns: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface SupportVideo {
  id: string;
  name: string;
  topic: string;
  status: VideoStatus;
  durationSeconds: number;
  /** Failure reason when status is Failed. */
  note: string | null;
  createdAtUtc: string;
}

/* ------------------------------------------------------------------ *
 * Campaigns
 * ------------------------------------------------------------------ */

export interface SupportCampaign {
  id: string;
  name: string;
  channel: CampaignChannel;
  status: CampaignStatus;
  posterId: string;
  posterName: string;
  recipientCount: number;
  sentCount: number;
  failedCount: number;
  sentAtUtc: string | null;
  note: string | null;
  createdAtUtc: string;
}

/** One row of the delivery report. The recipient address is masked. */
export interface SupportRecipientResult {
  maskedRecipient: string;
  status: RecipientStatus;
  reason: string | null;
  atUtc: string;
}

export interface SupportCampaignDetail extends SupportCampaign {
  videoIds: string[];
  deliveryReport: SupportRecipientResult[];
  /** True when the campaign is Failed and so may be re-sent. */
  canResend: boolean;
}

/* ------------------------------------------------------------------ *
 * Requests
 * ------------------------------------------------------------------ */

export interface RevealPatientRequest {
  /** Why this administrator needs the details. Recorded verbatim in the log. */
  reason: string;
}

export interface SupportPatientFilters {
  search?: string;
  groupId?: string;
  skip?: number;
  take?: number;
}

/** The tabs on /admin/pharmacies/[id], in display order. */
export type SupportTab =
  | "overview"
  | "patients"
  | "groups"
  | "posters"
  | "campaigns"
  | "videos";

export const SUPPORT_TABS: { key: SupportTab; label: string }[] = [
  { key: "overview", label: "Overview" },
  { key: "patients", label: "Patients" },
  { key: "groups", label: "Groups" },
  { key: "posters", label: "Posters" },
  { key: "campaigns", label: "Campaigns" },
  { key: "videos", label: "Videos" },
];

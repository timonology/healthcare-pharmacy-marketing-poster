export type CampaignChannel = "Email" | "Sms";
/**
 * `Queued` means accepted and durable, waiting for the dispatch worker —
 * usually for a second or two. `Sending` now means a worker holds it.
 *
 * Sending used to happen inside the request that created the campaign, so this
 * state did not exist: a campaign went from nothing to Sent or Failed while the
 * browser waited. It is here because a thousand-recipient send takes minutes and
 * the pharmacist should be able to close the tab.
 */
export type CampaignStatus =
  | "Draft"
  | "Queued"
  | "Sending"
  | "Sent"
  | "Stopped"
  | "Failed";
export type RecipientStatus = "Pending" | "Sent" | "Failed";

export interface RecipientResult {
  recipient: string;
  status: RecipientStatus;
  reason: string | null;
  atUtc: string;
}

export interface Campaign {
  id: string;
  ownerId: string;
  posterId: string;
  videoIds: string[];
  name: string;
  channel: CampaignChannel;
  status: CampaignStatus;
  recipientCount: number;
  sentCount: number;
  sentAtUtc: string | null;
  note: string | null;
  deliveryReport: RecipientResult[];
  createdAtUtc: string;
}

export interface CreateCampaignRequest {
  posterId: string;
  name: string;
  channel: CampaignChannel;
  recipients?: string[];
  patientIds?: string[];
  groupIds?: string[];
  videoIds?: string[];
}

export interface CampaignAudienceRequest {
  channel: CampaignChannel;
  recipients?: string[];
  patientIds?: string[];
  groupIds?: string[];
}

export interface CampaignAudiencePreview {
  totalUnique: number;
  fromManual: number;
  fromPatients: number;
  fromGroups: number;
  invalidCount: number;
  sampleRecipients: string[];
  /** Left out because they unsubscribed from this pharmacy's emails. */
  unsubscribedCount?: number;
}

/**
 * What the public unsubscribe page shows (security review R2-06). The address
 * is masked: the page is reachable by anyone holding the link.
 */
export interface UnsubscribePreview {
  pharmacyName: string;
  maskedEmail: string;
  unsubscribed: boolean;
}

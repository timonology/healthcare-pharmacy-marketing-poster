export type CampaignChannel = "Email" | "Sms";
export type CampaignStatus = "Draft" | "Sending" | "Sent" | "Stopped" | "Failed";

export interface Campaign {
  id: string;
  ownerId: string;
  posterId: string;
  name: string;
  channel: CampaignChannel;
  status: CampaignStatus;
  recipientCount: number;
  sentCount: number;
  sentAtUtc: string | null;
  note: string | null;
  createdAtUtc: string;
}

export interface CreateCampaignRequest {
  posterId: string;
  name: string;
  channel: CampaignChannel;
  recipients?: string[];
  patientIds?: string[];
  groupIds?: string[];
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
}

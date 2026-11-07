/**
 * Pharmacist invitations.
 *
 * Two audiences with opposite trust levels. Everything under `InvitedList` is
 * SuperAdmin-only. `InvitationPreview` is fetched by someone holding an
 * activation link and nothing else, so it deliberately carries only enough to
 * confirm they are activating the right account — no phone, no address.
 */

export type InvitationStatus = "Invited" | "Activated" | "Expired";

/**
 * Where an account is in its lifecycle.
 *
 * Distinct from active/deactivated: an invited account has simply never been
 * claimed, which is not the same as one an administrator switched off.
 */
export type AccountStatus = "Active" | "Invited";

export interface InvitePharmacistRequest {
  fullName: string;
  email: string;
  /** ODS / F-code. The same field the pharmacy profile calls sonarFCode. */
  fCode?: string | null;
  address?: string | null;
  postCode?: string | null;
  phone?: string | null;
  pharmacyName?: string | null;
}

export interface InviteResult {
  email: string;
  success: boolean;
  userId: string | null;
  /** Present on failure, and also on partial success — account made, email didn't send. */
  error: string | null;
  emailSent: boolean;
}

export interface BulkInviteResult {
  invited: number;
  failed: number;
  emailsSent: number;
  results: InviteResult[];
}

/** One row the CSV parser could not use. */
export interface CsvRowError {
  /** 1-based, header included, so it matches what the spreadsheet shows. */
  line: number;
  reason: string;
  email: string | null;
}

export interface CsvPreview {
  valid: InvitePharmacistRequest[];
  errors: CsvRowError[];
  totalRows: number;
}

export interface InvitedPharmacist {
  userId: string;
  email: string;
  displayName: string;
  pharmacyName: string;
  fCode: string | null;
  postCode: string;
  status: InvitationStatus;
  invitedAtUtc: string | null;
  expiresAtUtc: string | null;
  activatedAtUtc: string | null;
  invitedByEmail: string | null;
  /** How many invitations have been sent for this account, including resends. */
  timesSent: number;
}

export interface InvitedList {
  items: InvitedPharmacist[];
  total: number;
  skip: number;
  take: number;
}

export interface InvitationPreview {
  email: string;
  displayName: string;
  pharmacyName: string;
  expiresAtUtc: string;
}

export interface AcceptInvitationRequest {
  token: string;
  password: string;
}

/** Tone for a status pill, matching the rest of the admin console. */
export function invitationStatusTone(
  status: InvitationStatus,
): "neutral" | "good" | "warn" {
  switch (status) {
    case "Activated":
      return "good";
    case "Expired":
      return "warn";
    default:
      return "neutral";
  }
}

/**
 * True when a resend would help.
 *
 * Expired obviously. Invited too — the commonest support request is "I never
 * got the email", and reissuing is the answer to that as much as to expiry.
 */
export function canResendInvitation(status: InvitationStatus): boolean {
  return status !== "Activated";
}

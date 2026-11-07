export interface Patient {
  id: string;
  ownerId: string;
  fullName: string;
  email: string | null;
  phone: string | null;
  notes: string;
  groupIds: string[];
  createdAtUtc: string;
  updatedAtUtc: string;
  /** When agreement to marketing email was recorded (security review R2-06). */
  marketingConsentAtUtc?: string | null;
  marketingConsentSource?: string | null;
  /** This address unsubscribed from this pharmacy's emails; campaigns leave it out. */
  unsubscribed?: boolean;
}

export interface UpsertPatientRequest {
  fullName: string;
  email?: string | null;
  phone?: string | null;
  notes?: string;
  groupIds?: string[];
  /** Omitted leaves consent as it is; true records it; false clears it. */
  marketingConsent?: boolean;
}

export interface PatientListResponse {
  total: number;
  skip: number;
  take: number;
  items: Patient[];
}

export interface BulkImportRequest {
  patients: UpsertPatientRequest[];
}

export interface BulkImportResult {
  imported: number;
  skipped: number;
  errors: string[];
}

export interface BulkDeleteRequest {
  ids: string[];
}

export interface BulkAddToGroupRequest {
  patientIds: string[];
  groupId: string;
}

export interface AddRemoveGroupRequest {
  groupId: string;
}

/** One row's outcome from a CSV import. */
export interface PatientImportRow {
  lineNumber: number;
  /** "created" | "skipped" | "failed" */
  outcome: string;
  detail: string;
  contact: string | null;
}

/**
 * The report shown after importing a CSV.
 *
 * Every row that did not become a contact is listed. A bare count leaves
 * somebody with a messy spreadsheet unable to fix anything.
 */
export interface PatientImportReport {
  totalRows: number;
  created: number;
  skipped: number;
  failed: number;
  rows: PatientImportRow[];
}

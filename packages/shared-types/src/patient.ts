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
}

export interface UpsertPatientRequest {
  fullName: string;
  email?: string | null;
  phone?: string | null;
  notes?: string;
  groupIds?: string[];
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

export interface PatientGroup {
  id: string;
  ownerId: string;
  name: string;
  description: string;
  patientCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface UpsertPatientGroupRequest {
  name: string;
  description?: string;
}

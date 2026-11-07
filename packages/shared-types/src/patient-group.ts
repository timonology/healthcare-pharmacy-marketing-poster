export interface PatientGroup {
  id: string;
  /** Empty for a general group — it belongs to the platform, not a pharmacy. */
  ownerId: string;
  name: string;
  description: string;
  /**
   * Your own members only.
   *
   * For a general group this is how many of *your* patients are in it, never
   * the platform-wide total — pharmacies in the same town are competitors, and
   * a shared figure would leak their patient volumes.
   */
  patientCount: number;
  /** Defined by an administrator and visible to every pharmacy. */
  isGlobal: boolean;
  /**
   * False for a general group. You can still add and remove your own patients;
   * only the name and description are off limits, because changing them would
   * change them for everyone.
   */
  canEdit: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface UpsertPatientGroupRequest {
  name: string;
  description?: string;
}

/** Admin-only view of a general group, including platform-wide figures. */
export interface GlobalGroup {
  id: string;
  name: string;
  description: string;
  /** Across every pharmacy. Admin console only — never shown to a pharmacy. */
  totalPatients: number;
  pharmaciesUsing: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

/**
 * Splits groups into the two lists the pharmacy UI shows.
 *
 * Kept here rather than in a component so the Patients page and the campaign
 * audience picker cannot drift apart about what counts as "mine".
 */
export function partitionGroups(groups: PatientGroup[]): {
  mine: PatientGroup[];
  general: PatientGroup[];
} {
  return {
    mine: groups.filter((g) => !g.isGlobal),
    general: groups.filter((g) => g.isGlobal),
  };
}

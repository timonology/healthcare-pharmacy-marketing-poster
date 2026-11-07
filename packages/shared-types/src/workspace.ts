/** One pharmacy a person may work as. */
export interface Workspace {
  pharmacyId: string;
  pharmacyName: string;
  email: string;
  /** True for the person's own pharmacy. */
  isOwn: boolean;
  /** True when they may manage it, not merely work as it. */
  canManage: boolean;
  isCurrent: boolean;
}

/** The pharmacies available to a person, and which one they are in. */
export interface Workspaces {
  currentPharmacyId: string;
  currentPharmacyName: string;
  /** True when the current pharmacy is not their own — the header says so. */
  isActingAsAnother: boolean;
  workspaces: Workspace[];
  /**
   * Whether the signed-in account runs branches.
   *
   * Read from the account, not from the pharmacy currently being worked as: a
   * head office acting as one of its own branches is still a head office, and
   * the badge should not blink off when they switch.
   */
  isHeadOffice: boolean;
}

/** A request for access to somebody else's pharmacy, as the requester sees it. */
export interface AccessRequest {
  id: string;
  pharmacyId: string;
  pharmacyEmail: string;
  pharmacyName: string;
  /** "Pending" | "Approved" | "Rejected" */
  status: string;
  reason: string;
  /** What the reviewer said, once decided. */
  reviewNote: string | null;
  requestedAtUtc: string;
  reviewedAtUtc: string | null;
}

/** The same request as an administrator sees it, with who is asking. */
export interface AccessRequestForReview extends AccessRequest {
  userId: string;
  userEmail: string;
  reviewedByEmail: string | null;
}

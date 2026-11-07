import type { SubscriptionTier } from "./subscription";

export type UserRole = "PharmacyUser" | "Admin" | "SuperAdmin";

/** Staff roles — may enter /admin at all. */
export function isStaffRole(role: UserRole | undefined | null): boolean {
  return role === "Admin" || role === "SuperAdmin";
}

export type AdminActionType =
  | "CreateAdmin"
  | "UpdateAdmin"
  | "DeactivateUser"
  | "ReactivateUser"
  | "AssignUserToAdmin"
  | "UnassignUserFromAdmin"
  | "OverrideSubscriptionTier"
  | "ChangeRole"
  | "GrantVideoCredits"
  | "GenerateTemplate"
  | "ViewPatientContact"
  | "ResendCampaign"
  | "ChangePlatformSetting"
  | "StartActingAs"
  | "StopActingAs"
  | "RequestPharmacyAccess"
  | "ReviewPharmacyAccess";

export interface AdminUser {
  id: string;
  email: string;
  displayName: string;
  role: UserRole;
  tier: SubscriptionTier;
  isActive: boolean;
  managedByAdminId: string | null;
  managedByAdminName: string | null;
  pharmacyName: string;
  postCode: string;
  onboardingCompleted: boolean;
  /**
   * Whether this pharmacy may run branches.
   *
   * Granted by Marketing25 staff, because creating a branch creates a real
   * login for another pharmacy and emails a credential to whatever address was
   * typed. Shown here so an administrator can see the current state before
   * changing it, rather than toggling blind.
   */
  isHeadOffice: boolean;
  /**
   * Whether this pharmacy sits under another one.
   *
   * A branch can never be a head office — the server refuses it, to keep the
   * hierarchy one level deep. Carried here so the admin console can disable
   * the grant and say why, instead of offering a button that always fails.
   */
  isChildPharmacy: boolean;
  videoCredits: number;
  billingStatus: string;
  currentPeriodEndUtc: string | null;
  lastLoginAtUtc: string | null;
  createdAtUtc: string;
}

export interface AdminUserList {
  items: AdminUser[];
  total: number;
  skip: number;
  take: number;
}

export interface AdminSummary {
  id: string;
  email: string;
  displayName: string;
  isActive: boolean;
  managedUserCount: number;
  lastLoginAtUtc: string | null;
  createdAtUtc: string;
}

export interface AdminStats {
  totalUsers: number;
  activeUsers: number;
  inactiveUsers: number;
  superAdmins: number;
  admins: number;
  /** Accounts whose *role* is PharmacyUser. Sums with admins + superAdmins to totalUsers. */
  pharmacyUsers: number;
  /**
   * Accounts that own a pharmacy, including any held by staff. This is what
   * the Pharmacies page lists, and it is >= pharmacyUsers. The gap is the
   * number of pharmacy owners who have been given an admin role.
   */
  pharmacyAccounts: number;
  freeTier: number;
  starterTier: number;
  proTier: number;
  newUsersThisMonth: number;
  activeLast30Days: number;
  videosThisMonth: number;
  totalVideos: number;
  postersThisMonth: number;
  totalPosters: number;
  /** Campaigns that actually went out — drafts are not counted. */
  campaignsSentThisMonth: number;
  totalCampaignsSent: number;
  /** Messages delivered, not patient records on file. */
  recipientsReached: number;
  /** List-price estimate, not accounting revenue. Stripe is authoritative. */
  estimatedMonthlyRevenueGbp: number;
  estimatedAnnualRunRateGbp: number;
}

export interface AuditEntry {
  id: string;
  actorEmail: string;
  action: AdminActionType;
  targetEmail: string | null;
  detail: string | null;
  createdAtUtc: string;
}

export interface AuditList {
  items: AuditEntry[];
  total: number;
  skip: number;
  take: number;
}

export interface AuditFilters {
  action?: AdminActionType;
  /** Matches actor email, target email, or the detail text. */
  search?: string;
  /** Restrict to the last N days. */
  days?: number;
  skip?: number;
  take?: number;
}

/** Every action type, in the order the filter dropdown should show them. */
export const ADMIN_ACTIONS: AdminActionType[] = [
  "CreateAdmin",
  "UpdateAdmin",
  "ChangeRole",
  "DeactivateUser",
  "ReactivateUser",
  "AssignUserToAdmin",
  "UnassignUserFromAdmin",
  "OverrideSubscriptionTier",
  "GrantVideoCredits",
  "GenerateTemplate",
  "ViewPatientContact",
  "ResendCampaign",
  "ChangePlatformSetting",
  "StartActingAs",
  "StopActingAs",
  "RequestPharmacyAccess",
  "ReviewPharmacyAccess",
];

/** Human label for an audit action. */
export function describeAction(action: AdminActionType | string): string {
  const map: Record<string, string> = {
    StartActingAs: "Started working as a pharmacy",
    StopActingAs: "Stopped working as a pharmacy",
    RequestPharmacyAccess: "Requested access to a pharmacy",
    ReviewPharmacyAccess: "Reviewed a pharmacy access request",
    CreateAdmin: "Created administrator",
    UpdateAdmin: "Updated administrator",
    ChangeRole: "Changed role",
    DeactivateUser: "Deactivated user",
    ReactivateUser: "Reactivated user",
    AssignUserToAdmin: "Assigned pharmacy",
    UnassignUserFromAdmin: "Unassigned pharmacy",
    OverrideSubscriptionTier: "Changed plan",
    GrantVideoCredits: "Granted credits",
    GenerateTemplate: "Generated template",
    ViewPatientContact: "Revealed patient contact",
    ResendCampaign: "Re-sent campaign",
    ChangePlatformSetting: "Changed a platform setting",
  };
  return map[action] ?? action;
}

/**
 * Actions that touched personal data or contacted patients.
 *
 * Kept separate from {@link isPrivilegeAction} because they answer a different
 * question. Privilege actions are "who can do what"; these are "who saw a
 * patient's details, and who emailed them" — the entries a pharmacy would ask
 * about if they queried what we had done with their list.
 */
export function isPatientDataAction(action: AdminActionType | string): boolean {
  return action === "ViewPatientContact" || action === "ResendCampaign";
}

/**
 * Actions that change who can do what. Worth highlighting in the log —
 * these are the entries an auditor cares about most.
 */
export function isPrivilegeAction(action: AdminActionType | string): boolean {
  return action === "CreateAdmin" || action === "ChangeRole" || action === "UpdateAdmin";
}

/**
 * Sort options for the admin user table.
 *
 * Must stay in step with the C# UserSort enum — the API binds these by name,
 * so a value not in this union is rejected rather than silently ignored.
 */
export type UserSortOption =
  | "NewestFirst"
  | "OldestFirst"
  | "NameAsc"
  | "NameDesc"
  | "EmailAsc"
  | "EmailDesc"
  | "LastLoginDesc"
  | "TierDesc";

export interface AdminUserFilters {
  search?: string;
  role?: UserRole;
  isActive?: boolean;
  tier?: SubscriptionTier;
  managedBy?: string;
  unassignedOnly?: boolean;
  /**
   * Restrict to accounts that *are* a pharmacy, whatever role they hold.
   *
   * Use this rather than `role: "PharmacyUser"` for anything that means "our
   * customers". A pharmacy owner promoted to Admin keeps their pharmacy, and
   * the role filter drops them.
   */
  pharmacyAccounts?: boolean;
  /** Filter by onboarding completion. Omit for either. */
  onboarded?: boolean;
  /** The shared toolbar's words, or one of the older enum names. */
  sort?: UserSortOption | "newest" | "oldest";
  /** Account created at or after this instant (ISO, inclusive). */
  from?: string;
  /** Account created before this instant (ISO, exclusive). */
  to?: string;
  skip?: number;
  take?: number;
}

/** One day of platform activity. Empty days are included. */
export interface AnalyticsPoint {
  /** UTC day, ISO-8601. */
  date: string;
  posters: number;
  videos: number;
  campaigns: number;
}

export interface TopPharmacy {
  userId: string;
  pharmacyName: string;
  email: string;
  posters: number;
  videos: number;
  campaigns: number;
  total: number;
}

export interface AdminAnalytics {
  days: number;
  series: AnalyticsPoint[];
  topPharmacies: TopPharmacy[];
  postersInPeriod: number;
  videosInPeriod: number;
  campaignsInPeriod: number;
}

export interface CreateAdminRequest {
  email: string;
  password: string;
  displayName: string;
}

export interface UpdateAdminRequest {
  displayName?: string;
  isActive?: boolean;
}

/* ------------------------------------------------------------------ *
 * Platform settings — SuperAdmin only
 * ------------------------------------------------------------------ */

export interface VideoModelOption {
  id: string;
  displayName: string;
  description: string;
  /** Cost per video relative to the cheapest option. Approximate. */
  relativeCost: number;
  isCurrent: boolean;
}

export interface VideoModelSetting {
  currentModel: string;
  /**
   * Where the value in force came from: "database" when a SuperAdmin has set
   * it, otherwise "configuration" or "built-in default".
   */
  source: string;
  updatedByEmail: string | null;
  updatedAtUtc: string | null;
  /** What the environment supplies — what resetting would fall back to. */
  configuredFallback: string;
  options: VideoModelOption[];
}

/**
 * The audit console: who did what, including what they opened.
 *
 * Mirrors Acme.Application.Audit. Two collections of one shape:
 * `audit_events` (changes and security, kept) and `activity_events` (views,
 * kept 90 days).
 */

/** Pharmacy work and staff work are never one list. */
export type AuditTab = "Security" | "Staff" | "Pharmacy";

/** Within the staff and pharmacy tabs. */
export type AuditStreamView = "Changes" | "Views";

/**
 * Within the security tab. Default: staff actions, sign-ins and anything
 * refused, without machines. People: every audit row a person
 * caused. System: seeders and the smoke account.
 */
export type AuditSecurityView = "Default" | "People" | "System";

export type AuditActorKind = "pharmacy" | "admin" | "system";
export type AuditResult = "ok" | "denied" | "failed";

export interface AuditEvent {
  id: string;
  atUtc: string;
  actorId: string;
  actorEmail: string | null;
  actorRole: string | null;
  actorKind: AuditActorKind;
  /** The pharmacy the actor was acting as, or null for staff work outside one. */
  pharmacyId: string | null;
  /** Dotted, e.g. `poster.delete`, `admin.impersonate.start`. */
  action: string;
  entityType: string | null;
  entityId: string | null;
  result: AuditResult;
  ip: string | null;
  requestId: string | null;
  /** Small and sanitised: counts, never lists of people. */
  meta: Record<string, string>;
}

export interface AuditEventList {
  items: AuditEvent[];
  total: number;
  skip: number;
  take: number;
}

export interface AuditEventFilters {
  tab?: AuditTab;
  stream?: AuditStreamView;
  view?: AuditSecurityView;
  /** ISO timestamp, inclusive. */
  from?: string;
  /** ISO timestamp, exclusive. */
  to?: string;
  /** Exact actor id, or part of an email address. */
  actor?: string;
  pharmacyId?: string;
  /** Prefix: `poster.` matches every poster action. */
  action?: string;
  entityType?: string;
  entityId?: string;
  /** By event time. Omitted: newest first. */
  sort?: "newest" | "oldest";
  skip?: number;
  take?: number;
}

/** The action prefixes the console offers as quick filters. */
export const AUDIT_ACTION_PREFIXES = [
  "auth.",
  "admin.",
  "poster.",
  "video.",
  "campaign.",
  "patient.",
  "brand.",
  "display.",
  "template.",
  "subscription.",
  "pharmacy.",
  "profile.",
  "social.",
  "dashboard.",
  "system.",
] as const;

/** Query string for the console's filters; empty values are left out. */
export function auditEventQuery(f: AuditEventFilters): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(f)) {
    if (v === undefined || v === null || v === "") continue;
    q.set(k, String(v));
  }
  return q.toString();
}

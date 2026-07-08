/**
 * Stash for fields collected on /register that the user expects to flow through
 * to /onboarding and /brand-kit. They're not part of the register API payload
 * (which only accepts email/password/displayName), so we keep them in
 * sessionStorage long enough to pre-fill the downstream forms — then clear.
 */

const KEY = "acme_pending_signup";

export interface PendingSignup {
  pharmacyName?: string;
  contactName?: string;
  phone?: string;
  brandColor?: string;
  postCode?: string;
  sonarFCode?: string;
}

function isAvailable(): boolean {
  return typeof window !== "undefined" && !!window.sessionStorage;
}

export function setPendingSignup(data: PendingSignup): void {
  if (!isAvailable()) return;
  try {
    window.sessionStorage.setItem(KEY, JSON.stringify(data));
  } catch {
    /* quota or privacy mode — ignore */
  }
}

export function readPendingSignup(): PendingSignup | null {
  if (!isAvailable()) return null;
  try {
    const raw = window.sessionStorage.getItem(KEY);
    if (!raw) return null;
    return JSON.parse(raw) as PendingSignup;
  } catch {
    return null;
  }
}

export function clearPendingSignup(): void {
  if (!isAvailable()) return;
  try {
    window.sessionStorage.removeItem(KEY);
  } catch {
    /* ignore */
  }
}

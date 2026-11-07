import type { AccountStatus } from "./invitation";

/**
 * Parent → child pharmacy relationships.
 *
 * One level: a parent has children, a child cannot have its own. A child is a
 * full account — it signs in, owns its own patients and posters, and works
 * whether or not the parent ever looks at it. The parent gets access in
 * addition, never instead.
 */
export interface ChildPharmacy {
  userId: string;
  email: string;
  displayName: string;
  pharmacyName: string;
  fCode: string | null;
  postCode: string;
  /** Invited until they set a password from the activation email. */
  status: AccountStatus;
  isActive: boolean;
  lastLoginAtUtc: string | null;
  createdAtUtc: string;
}

/**
 * Where the signed-in pharmacy sits in a group.
 *
 * An object with a flag rather than "the parent, or null". The endpoint used to
 * answer with a nullable parent, and a null body survives no round trip
 * intact — ASP.NET Core sends it as a bodyless 204, and the BFF proxy turned
 * that into `true`, which read as "yes, you have a parent" for every top-level
 * pharmacy on the platform.
 *
 * `isChildPharmacy` is also the only reliable answer when `parent` is null but
 * the account really is a branch: the parent was deleted. It is a branch, and
 * must not be offered branches of its own.
 */
export interface PharmacyContext {
  isChildPharmacy: boolean;
  parent: ChildPharmacy | null;

  /**
   * Whether this pharmacy runs branches.
   *
   * Granted by Marketing25 rather than assumed: creating a branch creates a
   * real login for another pharmacy and emails a credential to whatever address
   * was typed, so it is not something an account gives itself.
   *
   * Drives what the app shows — the head office badge, the branch list, and
   * whether "Add branch" is offered at all rather than offered and then
   * refused by the server.
   */
  isHeadOffice: boolean;
}

export interface CreateChildPharmacyRequest {
  fullName: string;
  email: string;
  pharmacyName?: string | null;
  fCode?: string | null;
  address?: string | null;
  postCode?: string | null;
  phone?: string | null;
}

/**
 * What the browser learns when a "manage as" session starts.
 *
 * Note the absence of a token — it is exchanged server-side and written into an
 * httpOnly cookie. Client code never holds a credential for another account.
 */
export interface ManageAsSession {
  childUserId: string;
  childPharmacyName: string;
  expiresAtUtc: string;
}

/** True while a child pharmacy has not yet claimed its account. */
export function isAwaitingActivation(child: ChildPharmacy): boolean {
  return child.status === "Invited";
}

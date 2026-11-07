import type { SocialConnection, SocialOAuthProviderKind, SocialPlatform } from "@acme/shared-types";

/** Which Connect flow re-authorises a connection on this platform. */
export function reconnectKind(platform: SocialPlatform): SocialOAuthProviderKind {
  switch (platform) {
    case "FacebookPage":
    case "InstagramBusiness":
      return "Meta";
    case "YouTubeChannel":
      return "YouTube";
    case "LinkedInProfile":
    case "LinkedInPage":
      return "LinkedIn";
    case "TikTokAccount":
      return "TikTok";
    case "XAccount":
      return "X";
    default:
      return "Google";
  }
}

/** How many days ahead an expiring connection starts asking to be reconnected. */
export const EXPIRY_WARNING_DAYS = 14;

/**
 * Whether a connection's sign-in runs out soon, or already has.
 *
 * LinkedIn's lasts 60 days and cannot be renewed without the person signing
 * in again; saying so two weeks ahead turns a failed Monday post into a
 * two-click chore done whenever suits.
 */
export function expiryState(
  connection: Pick<SocialConnection, "status" | "expiresAtUtc">,
  now: Date = new Date(),
): { kind: "none" } | { kind: "soon" | "expired"; at: Date } {
  if (connection.status !== "Active" || !connection.expiresAtUtc) return { kind: "none" };
  const at = new Date(connection.expiresAtUtc);
  if (Number.isNaN(at.getTime())) return { kind: "none" };
  if (at.getTime() <= now.getTime()) return { kind: "expired", at };
  return at.getTime() - now.getTime() <= EXPIRY_WARNING_DAYS * 86_400_000
    ? { kind: "soon", at }
    : { kind: "none" };
}

/**
 * Sends `www.` visitors to the one public address (`SITE_URL`).
 *
 * <b>Why this matters beyond SEO.</b> Several things are tied to a single
 * origin, and a second working address breaks each of them quietly:
 *
 * - Social connect: the API only starts OAuth for pages open at
 *   `Public__WebBaseUrl`, and Meta/Google return to that address. A flow
 *   started on `www.` would come back to the bare domain, where the
 *   `sessionStorage` state this tab saved does not exist.
 * - Sign-in cookies are host-scoped, so `www.` and the bare domain are two
 *   separate sessions.
 *
 * Allowing both addresses in the API would only move the failure to the
 * callback. One address, enforced here, removes the whole class.
 *
 * Only the exact `www.` twin of the canonical host is redirected. Any other
 * host (localhost, the azurewebsites.net name, a preview) is left alone, so
 * this cannot loop or lock anyone out of a staging slot.
 */
export function canonicalRedirect(
  requestHost: string | null | undefined,
  pathAndQuery: string,
  canonicalOrigin: string,
): string | null {
  if (!requestHost) return null;

  let canonical: URL;
  try {
    canonical = new URL(canonicalOrigin);
  } catch {
    return null;
  }

  // First entry if a proxy sent a list; ports and case don't matter here.
  const host = requestHost.split(",")[0].trim().toLowerCase().replace(/:\d+$/, "");
  const target = canonical.hostname.toLowerCase();

  if (target.startsWith("www.") || host !== `www.${target}`) return null;

  const path = pathAndQuery.startsWith("/") ? pathAndQuery : `/${pathAndQuery}`;
  return `${canonical.origin}${path}`;
}

/**
 * Whether a path the web app is about to call on the API is the one it means.
 *
 * Route parameters arrive decoded, so a link like
 * `/api/posters/..%2F..%2Fadmin%2Fusers/export.png` hands a route the id
 * `../../admin/users`, and `fetch` then normalises the upstream URL to a
 * different endpoint — called with the signed-in visitor's session. The API
 * still checks every permission, so this grants nothing the visitor lacks,
 * but a crafted link should not be able to choose which endpoint a victim's
 * browser calls (security review R3-05). Refused here, once, for every proxy.
 */
export function isSafeApiPath(path: string): boolean {
  if (!path.startsWith("/api/")) return false;
  if (/[#\\\u0000-\u001f]/.test(path)) return false;

  const pathname = path.split("?", 1)[0];
  return !pathname.split("/").some((segment) => segment === "." || segment === "..");
}

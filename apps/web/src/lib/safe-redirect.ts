/**
 * Where to send someone after they sign in, or null when the requested place
 * is not ours (security review R2-04).
 *
 * `?redirect=` is set by the app itself when a session ends, but anyone can
 * craft the link. The old check refused values starting with "//" and missed
 * the forms browsers read the same way: "/\evil.example" and "/\t/evil.example"
 * both resolve to https://evil.example/. A person who had just typed their real
 * password on the real site would land on a page that could ask for it again.
 *
 * So the value is resolved exactly as the browser will resolve it, and kept
 * only when it stays on this origin. What is returned is rebuilt from the
 * parsed URL, never the raw input.
 */
export function safeRedirect(raw: string | null | undefined, origin: string): string | null {
  if (!raw) return null;

  // Ours are always a path. Backslashes and control characters have no place
  // in one and are exactly what browsers "helpfully" reinterpret.
  if (!raw.startsWith("/")) return null;
  if (/[\\\u0000-\u001f\u007f]/.test(raw)) return null;

  let url: URL;
  let base: URL;
  try {
    base = new URL(origin);
    url = new URL(raw, base);
  } catch {
    return null;
  }

  if (url.origin !== base.origin) return null;
  return `${url.pathname}${url.search}${url.hash}`;
}

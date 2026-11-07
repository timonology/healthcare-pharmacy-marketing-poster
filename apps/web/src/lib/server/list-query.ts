/**
 * The list parameters a BFF route passes on to the API, and nothing else.
 *
 * An allow-list rather than `url.search` verbatim, for the routes that used to
 * forward nothing at all: what reaches the API from them is exactly the shared
 * sort and date range, so a stray parameter cannot start meaning something
 * later without anyone deciding it should.
 */
const LIST_KEYS = ["sort", "from", "to"] as const;

export function listQuery(req: Request): string {
  const incoming = new URL(req.url).searchParams;
  const out = new URLSearchParams();
  for (const key of LIST_KEYS) {
    const value = incoming.get(key);
    if (value) out.set(key, value);
  }
  const s = out.toString();
  return s ? `?${s}` : "";
}

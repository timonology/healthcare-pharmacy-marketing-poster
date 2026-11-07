import { createHmac } from "node:crypto";
import { headers } from "next/headers";

/**
 * Headers that carry the browser's identity through to the API: which address
 * called (for rate limits and the audit log) and which request (for matching
 * an error in the browser to the API's record of it).
 *
 * Every call to the API goes through this server, so without them the API
 * would see this server's address on every request.
 *
 * **The address is the platform's, not the caller's** (security review R2-03).
 * `X-Forwarded-For` arrives as a list the caller can start: a browser may send
 * any values it likes, and the front end in front of this app (Azure's, or the
 * Next server itself when nothing is in front) appends the address it actually
 * saw. Only that last entry is trustworthy, so only that one is passed on —
 * the caller's own entries are dropped here. `CLIENT_IP_HOPS` raises the count
 * when another proxy (Front Door, Cloudflare) sits in front as well.
 *
 * **And it is signed** when `CLIENT_IP_SIGNING_KEY` is set (the API's
 * `Edge:ClientIpKey`, same value). The API is reachable on its own, so a header
 * that merely says "the browser was 1.2.3.4" could be sent by anyone; the
 * per-address rate limits on sign-in and password reset would then be per
 * whatever the caller claimed. With the signature, the API believes the
 * address only when this server vouched for it.
 *
 * `X-Request-Id`: the incoming one if it is sane, otherwise a new one. The API
 * echoes it back in its response.
 *
 * Returns nothing outside a request (a build step, a test) rather than throwing.
 */
export async function forwardingHeaders(): Promise<Record<string, string>> {
  let incoming: Headers;
  try {
    incoming = await headers();
  } catch {
    return {};
  }

  const out: Record<string, string> = {};

  const clientIp = clientAddress(
    incoming.get("x-forwarded-for"),
    incoming.get("x-real-ip"),
    hops(),
  );
  if (clientIp) {
    out["X-Forwarded-For"] = clientIp;
    const key = process.env.CLIENT_IP_SIGNING_KEY;
    if (key) {
      out["X-Client-Ip"] = clientIp;
      out["X-Client-Ip-Signature"] = signClientIp(key, clientIp, Date.now());
    }
  }

  const requestId = incoming.get("x-request-id");
  out["X-Request-Id"] =
    requestId && /^[A-Za-z0-9._-]{1,64}$/.test(requestId)
      ? requestId
      : crypto.randomUUID().replace(/-/g, "");

  return out;
}

function hops(): number {
  const n = Number.parseInt(process.env.CLIENT_IP_HOPS ?? "1", 10);
  return Number.isFinite(n) && n >= 1 && n <= 5 ? n : 1;
}

/**
 * The address the trusted front end saw: the `hops`-th entry from the right of
 * `X-Forwarded-For`, without a port. Null when there is nothing usable.
 */
export function clientAddress(
  forwardedFor: string | null,
  realIp: string | null,
  hopCount = 1,
): string | null {
  const entries = (forwardedFor ?? "")
    .split(",")
    .map((e) => e.trim())
    .filter(Boolean);
  const raw = entries.length >= hopCount ? entries[entries.length - hopCount] : realIp?.trim();
  return raw ? normaliseAddress(raw) : null;
}

/** "1.2.3.4:5678" → "1.2.3.4", "[::1]:80" → "::1"; anything that is not an address → null. */
export function normaliseAddress(raw: string): string | null {
  let value = raw.trim();
  const bracketed = /^\[([0-9a-fA-F:.]+)\](?::\d+)?$/.exec(value);
  if (bracketed) value = bracketed[1];
  else if (/^\d{1,3}(\.\d{1,3}){3}:\d+$/.test(value)) value = value.slice(0, value.lastIndexOf(":"));

  const ipv4 = /^\d{1,3}(\.\d{1,3}){3}$/.test(value)
    && value.split(".").every((o) => Number(o) <= 255);
  const ipv6 = /^[0-9a-fA-F:.]{2,45}$/.test(value) && value.includes(":");
  return ipv4 || ipv6 ? value : null;
}

/**
 * HMAC-SHA256 over "address|minute", base64url. The API accepts this minute
 * and the one before, so a captured header is worth two minutes at most.
 * Must match Acme.WebApi.SignedClientIp.
 */
export function signClientIp(key: string, ip: string, nowMs: number): string {
  const minute = Math.floor(nowMs / 60_000);
  return createHmac("sha256", key).update(`${ip}|${minute}`).digest("base64url");
}

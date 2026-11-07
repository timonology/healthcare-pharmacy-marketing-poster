/**
 * Server-side helpers for proxying requests to the .NET API.
 * Used exclusively from Next.js route handlers — never imported by client code.
 */

import { readAccessToken } from "./cookies";
import { isSafeApiPath } from "./api-path";
import { forwardingHeaders } from "./forwarding";

/**
 * Where the .NET API lives, from the Next server's point of view.
 *
 * Exported so a route that cannot use `apiFetch` — a multipart upload, say,
 * whose body must be forwarded as an untouched stream — still talks to the
 * same host rather than deriving its own and drifting.
 */
export const API_BASE =
  process.env.API_INTERNAL_URL ??
  process.env.NEXT_PUBLIC_API_URL ??
  "http://localhost:5050";

export interface ApiResult<T> {
  ok: boolean;
  status: number;
  data: T | null;
  error: string | null;
}

export async function apiFetch<T>(
  path: string,
  /**
   * `true`: the call needs the caller's token (401 without one).
   * `"optional"`: a public endpoint — send the token when there is one, so the
   * API knows who is looking (the audit log records signed-in views).
   */
  init: RequestInit & { auth?: boolean | "optional" } = {},
): Promise<ApiResult<T>> {
  if (!isSafeApiPath(path)) {
    return { ok: false, status: 400, data: null, error: "Invalid request path." };
  }

  const headers = new Headers(init.headers);
  // The browser's address and a request id, for the API's audit log.
  for (const [k, v] of Object.entries((await forwardingHeaders()))) {
    if (!headers.has(k)) headers.set(k, v);
  }
  if (!headers.has("Content-Type") && init.body && typeof init.body === "string") {
    headers.set("Content-Type", "application/json");
  }

  if (init.auth) {
    const token = (await readAccessToken());
    if (!token && init.auth !== "optional") {
      return { ok: false, status: 401, data: null, error: "Not authenticated." };
    }
    if (token) headers.set("Authorization", `Bearer ${token}`);
  }

  const res = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers,
    cache: "no-store",
  });

  const text = await res.text();
  const json = text ? safeJson(text) : null;

  if (!res.ok) {
    const error =
      (json && typeof json === "object" && "error" in json
        ? String((json as { error: unknown }).error)
        : null) ?? res.statusText;
    return { ok: false, status: res.status, data: null, error };
  }

  return { ok: true, status: res.status, data: json as T, error: null };
}

function safeJson(text: string): unknown {
  try { return JSON.parse(text); } catch { return null; }
}

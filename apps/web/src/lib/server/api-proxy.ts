/**
 * Server-side helpers for proxying requests to the .NET API.
 * Used exclusively from Next.js route handlers — never imported by client code.
 */

import { readAccessToken } from "./cookies";

const API_BASE =
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
  init: RequestInit & { auth?: boolean } = {},
): Promise<ApiResult<T>> {
  const headers = new Headers(init.headers);
  if (!headers.has("Content-Type") && init.body && typeof init.body === "string") {
    headers.set("Content-Type", "application/json");
  }

  if (init.auth) {
    const token = readAccessToken();
    if (!token) {
      return { ok: false, status: 401, data: null, error: "Not authenticated." };
    }
    headers.set("Authorization", `Bearer ${token}`);
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

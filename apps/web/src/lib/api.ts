/**
 * Client-side API. All calls go to Next.js BFF route handlers — never
 * directly to the .NET API. The BFF attaches the JWT from an httpOnly cookie,
 * so client code never sees or stores the token.
 */

import type {
  BrandKit,
  CreatePosterRequest,
  CurrentSubscription,
  LoginRequest,
  PagedResponse,
  Plan,
  Poster,
  PosterStatus,
  PosterSummary,
  RegisterRequest,
  SubscriptionTier,
  Template,
  TemplateCategory,
  TemplateSummary,
  UpdatePosterRequest,
  UpsertBrandKitRequest,
  UserProfile,
} from "@acme/shared-types";

interface AuthEnvelope {
  user: UserProfile;
}

async function jsonOrThrow<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(body.error ?? `HTTP ${res.status}`);
  }
  return res.json() as Promise<T>;
}

function qs(params: Record<string, string | number | undefined | null>) {
  const usp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== "") usp.set(k, String(v));
  }
  const s = usp.toString();
  return s ? `?${s}` : "";
}

export const api = {
  // ---- Auth ----
  register: async (body: RegisterRequest) =>
    jsonOrThrow<AuthEnvelope>(
      await fetch("/api/auth/register", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  login: async (body: LoginRequest) =>
    jsonOrThrow<AuthEnvelope>(
      await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  refresh: async () =>
    jsonOrThrow<AuthEnvelope>(
      await fetch("/api/auth/refresh", {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  logout: async () => {
    await fetch("/api/auth/logout", {
      method: "POST",
      credentials: "same-origin",
    });
  },

  me: async () =>
    jsonOrThrow<UserProfile>(
      await fetch("/api/auth/me", { credentials: "same-origin" }),
    ),

  // ---- Brand Kit ----
  getBrandKit: async () => {
    const res = await fetch("/api/brand-kit", { credentials: "same-origin" });
    if (res.status === 404) return null;
    return jsonOrThrow<BrandKit>(res);
  },

  upsertBrandKit: async (body: UpsertBrandKitRequest) =>
    jsonOrThrow<BrandKit>(
      await fetch("/api/brand-kit", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  uploadLogo: async (file: File) => {
    const fd = new FormData();
    fd.append("file", file);
    return jsonOrThrow<BrandKit>(
      await fetch("/api/brand-kit/logo", {
        method: "POST",
        body: fd,
        credentials: "same-origin",
      }),
    );
  },

  // ---- Templates ----
  listTemplates: async (params: {
    category?: TemplateCategory;
    search?: string;
    skip?: number;
    take?: number;
  } = {}) =>
    jsonOrThrow<PagedResponse<TemplateSummary>>(
      await fetch(`/api/templates${qs(params)}`, { credentials: "same-origin" }),
    ),

  getTemplate: async (id: string) =>
    jsonOrThrow<Template>(
      await fetch(`/api/templates/${id}`, { credentials: "same-origin" }),
    ),

  // ---- Posters ----
  listPosters: async (params: {
    status?: PosterStatus;
    search?: string;
    skip?: number;
    take?: number;
  } = {}) =>
    jsonOrThrow<PagedResponse<PosterSummary>>(
      await fetch(`/api/posters${qs(params)}`, { credentials: "same-origin" }),
    ),

  getPoster: async (id: string) =>
    jsonOrThrow<Poster>(
      await fetch(`/api/posters/${id}`, { credentials: "same-origin" }),
    ),

  createPoster: async (body: CreatePosterRequest) =>
    jsonOrThrow<Poster>(
      await fetch("/api/posters", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  updatePoster: async (id: string, body: UpdatePosterRequest) =>
    jsonOrThrow<Poster>(
      await fetch(`/api/posters/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  deletePoster: async (id: string) => {
    const res = await fetch(`/api/posters/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  },

  duplicatePoster: async (id: string) =>
    jsonOrThrow<Poster>(
      await fetch(`/api/posters/${id}/duplicate`, {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  setPosterStatus: async (id: string, status: PosterStatus) =>
    jsonOrThrow<Poster>(
      await fetch(`/api/posters/${id}/status`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status }),
        credentials: "same-origin",
      }),
    ),

  // ---- Subscription ----
  getCurrentSubscription: async () =>
    jsonOrThrow<CurrentSubscription>(
      await fetch("/api/subscription/current", { credentials: "same-origin" }),
    ),

  listPlans: async () =>
    jsonOrThrow<Plan[]>(
      await fetch("/api/subscription/plans", { credentials: "same-origin" }),
    ),

  upgradeSubscription: async (tier: SubscriptionTier) =>
    jsonOrThrow<CurrentSubscription>(
      await fetch("/api/subscription/upgrade", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tier }),
        credentials: "same-origin",
      }),
    ),
};

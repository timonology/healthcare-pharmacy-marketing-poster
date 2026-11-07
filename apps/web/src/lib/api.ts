/**
 * Client-side API. All calls go to Next.js BFF route handlers — never
 * directly to the .NET API. The BFF attaches the JWT from an httpOnly cookie,
 * so client code never sees or stores the token.
 */

import type {
  BrandKit,
  DisplayScreen,
  DisplayTokenIssued,
  Playlist as DisplayPlaylist,
  PlaylistItemKind,
  BulkAddToGroupRequest,
  BulkDeleteRequest,
  BulkImportRequest,
  BulkImportResult,
  Campaign,
  AnalyticsOverview,
  CampaignAudiencePreview,
  CampaignAudienceRequest,
  CreateCampaignRequest,
  GeneratePosterRequest,
  CreatePosterRequest,
  CreateVideoRequest,
  RefinePlan,
  AddVideoPicturesRequest,
  ChangeShotsRequest,
  ShotChoice,
  VideoShot,
  RefineRequest,
  AdminAnalytics,
  AdminStats,
  AdminSummary,
  AdminUserFilters,
  AdminUserList,
  AuditFilters,
  AuditList,
  AuditEventFilters,
  AuditEventList,
  UserRole,
  BillingInterval,
  BillingStatusResponse,
  CheckoutSession,
  CreateAdminRequest,
  CurrentSubscription,
  LoginRequest,
  Me,
  OnboardRequest,
  PagedResponse,
  Patient,
  PatientGroup,
  PatientListResponse,
  PharmacySupportOverview,
  RevealedPatientContact,
  AcceptInvitationRequest,
  BulkInviteResult,
  CsvPreview,
  InvitationPreview,
  InvitationStatus,
  InvitedList,
  InviteResult,
  InvitePharmacistRequest,
  GlobalGroup,
  ChildPharmacy,
  PharmacyContext,
  CreateChildPharmacyRequest,
  ManageAsSession,
  AdsAccount,
  CreateAdsCampaignRequest,
  CreatePublishRequest,
  RecurringSeries,
  RecurringSeriesList,
  SeriesPreview,
  SeriesPreviewRequest,
  SeriesRequest,
  DiscoveredAccount,
  GoogleAdsCampaign,
  OAuthStart,
  SocialAvailability,
  TikTokCreatorInfo,
  PublishStatus,
  ScheduledPost,
  ScheduledPostList,
  SocialConnection,
  SocialOAuthProviderKind,
  SupportCampaign,
  SupportCampaignDetail,
  SupportGroup,
  SupportPatientFilters,
  SupportPatientList,
  SupportPoster,
  SupportVideo,
  Plan,
  Poster,
  PosterStatus,
  PosterSummary,
  RegisterRequest,
  SubscriptionTier,
  GeneratedTemplate,
  PosterImage,
  StoredTemplateAsset,
  StockPhoto,
  PhotoOptions,
  PosterPhoto,
  StoredPosterPhoto,
  GeneratedPoster,
  Template,
  TemplateImageAspect,
  UpsertTemplateRequest,
  TemplateCategory,
  TemplateSummary,
  UpdateAdminRequest,
  VideoModelSetting,
  UpdatePosterRequest,
  UpsertBrandKitRequest,
  UpsertPatientGroupRequest,
  UpsertPatientRequest,
  UpsertProfileRequest,
  UserProfile,
  Video,
  NarrationOptions,
  VideoImage,
  PatientImportReport,
  Workspaces,
  AccessRequest,
  AccessRequestForReview,
  UnsubscribePreview,
} from "@acme/shared-types";
import { auditEventQuery } from "@acme/shared-types";
import type { ListSort } from "@/lib/list-window";
import { authedFetch } from "@/lib/session";

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

/**
 * Default fetch options for client-side calls to our own BFF.
 * `no-store` prevents stale Me / subscription responses from causing
 * onboarding-loop redirects after login.
 */
const FETCH_OPTS = { credentials: "same-origin", cache: "no-store" } as const;

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

  /**
   * Checks an activation link and says whose account it opens.
   *
   * Plain fetch, not authedFetch — the person following the link has no
   * session, and authedFetch's 401 handling would bounce them to /login,
   * which is precisely the page they cannot use yet.
   */
  previewInvitation: async (token: string) =>
    jsonOrThrow<InvitationPreview>(
      await fetch(`/api/invitations/preview?token=${encodeURIComponent(token)}`, {
        credentials: "same-origin",
        cache: "no-store",
      }),
    ),

  /** Sets the password and opens the account. */
  acceptInvitation: async (body: AcceptInvitationRequest) =>
    jsonOrThrow<{ activated: boolean }>(
      await fetch("/api/invitations/accept", {
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

  /**
   * Starts a password reset.
   *
   * Never rejects on "no such account" — the endpoint answers identically
   * whether or not the address is registered, so that this cannot be used to
   * discover which addresses have accounts. The caller shows the same message
   * either way.
   */
  /**
   * Uploads a CSV of contacts and returns the per-row report.
   *
   * Sends the file itself rather than parsing it here. The server owns one
   * parser, shared with the invitation importer, and is the only side that can
   * check a row against contacts already saved.
   */
  importPatients: async (file: File): Promise<PatientImportReport> => {
    const form = new FormData();
    form.append("file", file);

    // No Content-Type header: the browser sets it, including the multipart
    // boundary. Setting it by hand omits the boundary and the server sees an
    // empty form.
    const res = await authedFetch("/api/patients/import", { method: "POST", body: form });

    if (!res.ok) {
      const body = await res.json().catch(() => null);
      throw new Error(body?.error ?? "Import failed.");
    }
    return (await res.json()) as PatientImportReport;
  },

  /** The pharmacies the signed-in person may work as. */
  workspaces: async (): Promise<Workspaces> =>
    jsonOrThrow<Workspaces>(await authedFetch("/api/workspaces", FETCH_OPTS)),

  /**
   * Starts working as a pharmacy.
   *
   * A full page load afterwards, deliberately — every cached list, count and
   * store in the app belongs to the pharmacy that was current when it loaded.
   * Reconciling all of that in place is a great deal of machinery to get
   * subtly wrong, and showing one pharmacy's patients under another's name is
   * the exact failure this feature must never have.
   */
  switchWorkspace: async (pharmacyId: string) => {
    const res = await authedFetch("/api/workspaces/switch", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pharmacyId }),
    });
    if (!res.ok) {
      const body = await res.json().catch(() => null);
      throw new Error(body?.error ?? "Could not switch pharmacy.");
    }
    return (await res.json()) as { pharmacyId: string; pharmacyName: string };
  },

  /** What this person has asked for, and how it went. */
  myAccessRequests: async (): Promise<AccessRequest[]> =>
    jsonOrThrow<AccessRequest[]>(
      await authedFetch("/api/workspaces/access-requests", FETCH_OPTS)),

  /**
   * Asks for access to a pharmacy, named by its email address.
   *
   * Resolves whether or not that address has an account — the API answers
   * identically either way, so this cannot be used to discover which
   * pharmacies are registered. Only a complaint about the reason is thrown.
   */
  requestPharmacyAccess: async (pharmacyEmail: string, reason: string) => {
    const res = await authedFetch("/api/workspaces/access-requests", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pharmacyEmail, reason }),
    });
    if (res.status === 400) {
      const body = await res.json().catch(() => null);
      throw new Error(body?.error ?? "Please check the details and try again.");
    }
  },

  /** The review queue. Platform staff only. */
  accessRequestQueue: async (pendingOnly = true): Promise<AccessRequestForReview[]> =>
    jsonOrThrow<AccessRequestForReview[]>(
      await authedFetch(
        `/api/admin/access-requests?pendingOnly=${pendingOnly}&take=100`, FETCH_OPTS)),

  /** Approves or rejects one request. Approval creates the membership. */
  reviewAccessRequest: async (id: string, approve: boolean, note?: string) => {
    const res = await authedFetch(`/api/admin/access-requests/${id}/review`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ approve, note: note ?? null }),
    });
    if (!res.ok) {
      const body = await res.json().catch(() => null);
      throw new Error(body?.error ?? "Could not record that decision.");
    }
  },

  /** Returns to your own pharmacy. */
  leaveWorkspace: async () => {
    await authedFetch("/api/workspaces/switch", { method: "DELETE" });
  },

  /**
   * Starts a reset. Resolves to an error message only when the request was
   * refused for being too frequent — every other outcome looks the same, on
   * purpose (see the route).
   */
  forgotPassword: async (email: string): Promise<{ refused: string | null }> => {
    const res = await fetch("/api/auth/forgot-password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      body: JSON.stringify({ email }),
    });
    if (res.status !== 429) return { refused: null };
    const body = (await res.json().catch(() => null)) as { error?: string } | null;
    return { refused: body?.error ?? "Too many requests. Wait a few minutes and try again." };
  },

  /** Completes a reset. Signs the account out everywhere on success. */
  resetPassword: async (token: string, password: string) => {
    const res = await fetch("/api/auth/reset-password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      body: JSON.stringify({ token, password }),
    });
    if (!res.ok) {
      const body = await res.json().catch(() => null);
      throw new Error(body?.error ?? "Could not reset the password.");
    }
  },

  /** The public unsubscribe page (security review R2-06). */
  unsubscribePreview: async (token: string) =>
    jsonOrThrow<UnsubscribePreview>(
      await fetch(`/api/unsubscribe?token=${encodeURIComponent(token)}`, { cache: "no-store" }),
    ),

  unsubscribe: async (token: string) =>
    jsonOrThrow<UnsubscribePreview>(
      await fetch(`/api/unsubscribe?token=${encodeURIComponent(token)}`, { method: "POST" }),
    ),

  verifyEmail: async (token: string) => {
    const res = await fetch("/api/auth/verify-email", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      body: JSON.stringify({ token }),
    });
    if (!res.ok) {
      const body = await res.json().catch(() => null);
      throw new Error(body?.error ?? "Could not confirm the address.");
    }
  },

  resendVerification: async () => {
    await authedFetch("/api/auth/resend-verification", { method: "POST" });
  },

  logout: async () => {
    await fetch("/api/auth/logout", {
      method: "POST",
      credentials: "same-origin",
    });
  },

  me: async () =>
    jsonOrThrow<UserProfile>(
      await authedFetch("/api/auth/me", FETCH_OPTS),
    ),

  /**
   * "Is anyone signed in?" — returns null instead of throwing, and never
   * triggers session expiry.
   *
   * Used by chrome that renders on both public and private pages. A 401 here
   * is the expected answer for an anonymous visitor; treating it as an expired
   * session sends them to /login, which probes again, which loops.
   */
  probeMe: async (): Promise<UserProfile | null> => {
    const res = await authedFetch("/api/auth/me", FETCH_OPTS, { probe: true });
    if (res.status === 401) return null;
    return jsonOrThrow<UserProfile>(res);
  },

  // ---- Sub-pharmacies ----
  // Scoped to the caller throughout: the API takes the parent from the token,
  // never from a request body.
  /**
   * In-pharmacy display screens and the playlists they loop.
   *
   * Everything here is the pharmacy's own side. The kiosk talks to
   * `/api/display/*` (singular, unauthenticated) and never uses this client —
   * a screen has no session and must not be handed one.
   */
  displays: {
    listScreens: async () =>
      jsonOrThrow<DisplayScreen[]>(
        await authedFetch("/api/displays/screens", FETCH_OPTS),
      ),

    registerScreen: async (body: {
      name: string;
      location?: string;
      addressLine?: string;
    }) =>
      jsonOrThrow<DisplayScreen>(
        await authedFetch("/api/displays/screens", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    updateScreen: async (
      id: string,
      body: { name: string; location?: string; addressLine?: string },
    ) =>
      jsonOrThrow<DisplayScreen>(
        await authedFetch(`/api/displays/screens/${id}`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    assignPlaylist: async (id: string, playlistId: string | null) =>
      jsonOrThrow<DisplayScreen>(
        await authedFetch(`/api/displays/screens/${id}/playlist`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ playlistId }),
          credentials: "same-origin",
        }),
      ),

    deleteScreen: async (id: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/displays/screens/${id}`, {
          method: "DELETE",
          credentials: "same-origin",
        }),
      ),

    refreshPairingCode: async (id: string) =>
      jsonOrThrow<DisplayScreen>(
        await authedFetch(`/api/displays/screens/${id}/pairing-code`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),

    /**
     * Mints a direct link. The token comes back once and is never retrievable
     * again — the server keeps only its hash.
     */
    issueDirectLink: async (id: string) =>
      jsonOrThrow<DisplayTokenIssued>(
        await authedFetch(`/api/displays/screens/${id}/direct-link`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),

    revokeCredential: async (id: string, credentialId: string) =>
      jsonOrThrow<DisplayScreen>(
        await authedFetch(`/api/displays/screens/${id}/credentials/${credentialId}`, {
          method: "DELETE",
          credentials: "same-origin",
        }),
      ),

    listPlaylists: async () =>
      jsonOrThrow<DisplayPlaylist[]>(
        await authedFetch("/api/displays/playlists", FETCH_OPTS),
      ),

    createPlaylist: async (body: {
      name: string;
      items?: { kind: PlaylistItemKind; refId: string; durationSeconds: number }[];
    }) =>
      jsonOrThrow<DisplayPlaylist>(
        await authedFetch("/api/displays/playlists", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    updatePlaylist: async (
      id: string,
      body: {
        name: string;
        items: { kind: PlaylistItemKind; refId: string; durationSeconds: number }[];
      },
    ) =>
      jsonOrThrow<DisplayPlaylist>(
        await authedFetch(`/api/displays/playlists/${id}`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    deletePlaylist: async (id: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/displays/playlists/${id}`, {
          method: "DELETE",
          credentials: "same-origin",
        }),
      ),
  },

  pharmacies: {
    children: async () =>
      jsonOrThrow<ChildPharmacy[]>(
        await authedFetch("/api/pharmacies/children", FETCH_OPTS),
      ),

    /**
     * A branch's records, for the head office that runs it.
     *
     * The read is audited server-side — a head office seeing across its
     * branches without switching into them must not be the quiet way around
     * the trail that switching leaves.
     */
    branchOverview: async (childId: string) =>
      jsonOrThrow<PharmacySupportOverview>(
        await authedFetch(`/api/pharmacies/children/${childId}/overview`, FETCH_OPTS),
      ),

    /** Whether this pharmacy is a branch of another, and of whom. */
    context: async () =>
      jsonOrThrow<PharmacyContext>(
        await authedFetch("/api/pharmacies/context", FETCH_OPTS),
      ),

    createChild: async (body: CreateChildPharmacyRequest) =>
      jsonOrThrow<ChildPharmacy>(
        await authedFetch("/api/pharmacies/children", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    detachChild: async (childId: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/pharmacies/children/${childId}`, {
          method: "DELETE",
          credentials: "same-origin",
        }),
      ),

    /**
     * Starts managing a sub-pharmacy.
     *
     * No token comes back. It is exchanged server-side into an httpOnly
     * cookie, so client code never holds a credential for another account —
     * every subsequent request simply carries the child's identity until the
     * session is ended or lapses.
     */
    startManageAs: async (childId: string) =>
      jsonOrThrow<ManageAsSession>(
        await authedFetch("/api/pharmacies/manage-as", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ childId }),
          credentials: "same-origin",
        }),
      ),

    endManageAs: async () => {
      await fetch("/api/pharmacies/manage-as", {
        method: "DELETE",
        credentials: "same-origin",
      });
    },
  },

  // ---- Brand Kit ----
  getBrandKit: async () => {
    const res = await authedFetch("/api/brand-kit", { credentials: "same-origin" });
    // 204: no kit yet (the route's answer). 404 kept for an older deployment.
    if (res.status === 204 || res.status === 404) return null;
    return jsonOrThrow<BrandKit>(res);
  },

  upsertBrandKit: async (body: UpsertBrandKitRequest) =>
    jsonOrThrow<BrandKit>(
      await authedFetch("/api/brand-kit", {
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
      await authedFetch("/api/brand-kit/logo", {
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
    /** See lib/list-window: `listWindowParams` builds these three. */
    sort?: ListSort;
    from?: string;
    to?: string;
    skip?: number;
    take?: number;
  } = {}) =>
    jsonOrThrow<PagedResponse<TemplateSummary>>(
      await authedFetch(`/api/templates${qs(params)}`, { credentials: "same-origin" }),
    ),

  getTemplate: async (id: string) =>
    jsonOrThrow<Template>(
      await authedFetch(`/api/templates/${id}`, { credentials: "same-origin" }),
    ),

  /**
   * Publish a template to the shared gallery. Staff only — the API enforces it.
   */
  createTemplate: async (body: UpsertTemplateRequest) =>
    jsonOrThrow<Template>(
      await authedFetch("/api/templates", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  updateTemplate: async (id: string, body: UpsertTemplateRequest) =>
    jsonOrThrow<Template>(
      await authedFetch(`/api/templates/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  deleteTemplate: async (id: string) => {
    const res = await authedFetch(`/api/templates/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error("Could not delete that template.");
    return true;
  },

  // ---- Posters ----
  listPosters: async (params: {
    status?: PosterStatus;
    search?: string;
    sort?: ListSort;
    from?: string;
    to?: string;
    skip?: number;
    take?: number;
  } = {}) =>
    jsonOrThrow<PagedResponse<PosterSummary>>(
      await authedFetch(`/api/posters${qs(params)}`, { credentials: "same-origin" }),
    ),

  getPoster: async (id: string) =>
    jsonOrThrow<Poster>(
      await authedFetch(`/api/posters/${id}`, { credentials: "same-origin" }),
    ),

  /**
   * Designs a poster from a brief and saves it.
   *
   * Slow by nature — a model is laying out a page — so callers should show
   * progress rather than a spinner that looks stuck. A 403 here is an
   * allowance refusal with a sentence worth showing verbatim; it is not a
   * sign-in problem.
   */
  /** Which photo sources are set up. AI is only offered when this says so. */
  photoOptions: async () =>
    jsonOrThrow<PhotoOptions>(await authedFetch("/api/posters/photo-options", FETCH_OPTS)),

  /** Stock photos for a pharmacy's AI design. Rate-limited per person. */
  searchPosterStockPhotos: async (query: string) =>
    jsonOrThrow<StockPhoto[]>(
      await authedFetch(
        `/api/posters/stock-photos?query=${encodeURIComponent(query)}`,
        FETCH_OPTS,
      ),
    ),

  /** Uploads a photo for a pharmacy's AI design; refused unless it is a real image. */
  uploadPosterPhoto: async (file: File) => {
    const form = new FormData();
    form.append("file", file);
    return jsonOrThrow<StoredPosterPhoto>(
      await authedFetch("/api/posters/photos", {
        method: "POST",
        body: form,
        credentials: "same-origin",
      }),
    );
  },

  generatePoster: async (body: GeneratePosterRequest) =>
    jsonOrThrow<GeneratedPoster>(
      await authedFetch("/api/posters/generate", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  createPoster: async (body: CreatePosterRequest) =>
    jsonOrThrow<Poster>(
      await authedFetch("/api/posters", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  updatePoster: async (id: string, body: UpdatePosterRequest) =>
    jsonOrThrow<Poster>(
      await authedFetch(`/api/posters/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  deletePoster: async (id: string) => {
    const res = await authedFetch(`/api/posters/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  },

  duplicatePoster: async (id: string) =>
    jsonOrThrow<Poster>(
      await authedFetch(`/api/posters/${id}/duplicate`, {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  setPosterStatus: async (id: string, status: PosterStatus) =>
    jsonOrThrow<Poster>(
      await authedFetch(`/api/posters/${id}/status`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status }),
        credentials: "same-origin",
      }),
    ),

  // ---- Subscription ----
  getCurrentSubscription: async () =>
    jsonOrThrow<CurrentSubscription>(
      await authedFetch("/api/subscription/current", FETCH_OPTS),
    ),

  listPlans: async () =>
    jsonOrThrow<Plan[]>(
      await authedFetch("/api/subscription/plans", { credentials: "same-origin" }),
    ),

  upgradeSubscription: async (tier: SubscriptionTier) =>
    jsonOrThrow<CurrentSubscription>(
      await authedFetch("/api/subscription/upgrade", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tier }),
        credentials: "same-origin",
      }),
    ),

  // ---- Billing (Stripe) ----
  getBillingStatus: async () =>
    jsonOrThrow<BillingStatusResponse>(
      await authedFetch("/api/billing/status", FETCH_OPTS),
    ),

  /**
   * Starts a subscription checkout and returns the hosted Stripe URL. The
   * caller performs a top-level redirect — Stripe Checkout cannot be framed,
   * so `window.location.assign` is required rather than a router push.
   */
  startCheckout: async (tier: SubscriptionTier, interval: BillingInterval) =>
    jsonOrThrow<CheckoutSession>(
      await authedFetch("/api/billing/checkout", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tier, interval }),
        credentials: "same-origin",
      }),
    ),

  buyCredits: async (packKey: string) =>
    jsonOrThrow<CheckoutSession>(
      await authedFetch("/api/billing/credits", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ packKey }),
        credentials: "same-origin",
      }),
    ),

  openBillingPortal: async () =>
    jsonOrThrow<CheckoutSession>(
      await authedFetch("/api/billing/portal", {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  // ---- Admin ----
  // Every call here hits an endpoint the .NET API guards with a role policy
  // plus a per-row scope check. Nothing below is a security boundary.
  admin: {
    stats: async () =>
      jsonOrThrow<AdminStats>(await authedFetch("/api/admin/stats", FETCH_OPTS)),

    /**
     * Design a poster template from a brief.
     *
     * Returns a candidate only — nothing reaches the gallery until it is
     * explicitly published with `api.createTemplate`. Expect this to take
     * 30-120s: it runs a large model and re-runs it if the first design fails
     * the print rules, so callers must show progress rather than a spinner
     * that looks stuck.
     */
    generateTemplate: async (body: {
      prompt: string;
      category?: string;
      palette?: string;
      archetype?: string;
      /**
       * Where the photograph comes from. An upload or a previewed AI image is
       * already stored (`assetUrl`), so the admin sees and can reject it before
       * a whole layout is built around it.
       */
      photo?: PosterPhoto;
    }) =>
      jsonOrThrow<GeneratedTemplate>(
        await authedFetch("/api/admin/templates/generate", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    /**
     * Produce a photograph for a poster and store it.
     *
     * The provider is decided server-side — an image model when one is
     * configured, stock photography otherwise — so the caller just asks for an
     * image. `offset` fetches the next stock candidate for "try another".
     */
    createPosterImage: async (body: {
      description: string;
      aspect?: TemplateImageAspect;
      offset?: number;
      /** "ai": AI or an error, never a stock photo instead. */
      provider?: "ai" | "stock" | "auto";
    }) =>
      jsonOrThrow<PosterImage>(
        await authedFetch("/api/admin/templates/images", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    /**
     * Upload an image for use in templates.
     *
     * Multipart, so no Content-Type header — the browser must set the multipart
     * boundary itself, and providing one here produces a request the server
     * cannot parse.
     */
    /** Stock photos for the admin picker. Thumbnails only. */
    searchStockPhotos: async (query: string) =>
      jsonOrThrow<StockPhoto[]>(
        await authedFetch(
          `/api/admin/templates/stock-photos?query=${encodeURIComponent(query)}`,
          FETCH_OPTS,
        ),
      ),

    uploadTemplateAsset: async (file: File) => {
      const form = new FormData();
      form.append("file", file);
      return jsonOrThrow<StoredTemplateAsset>(
        await authedFetch("/api/admin/templates/assets", {
          method: "POST",
          body: form,
          credentials: "same-origin",
        }),
      );
    },

    /** Platform-wide activity over a rolling window. Days is clamped 7–90. */
    analytics: async (days = 30) =>
      jsonOrThrow<AdminAnalytics>(
        await authedFetch(`/api/admin/analytics?days=${days}`, FETCH_OPTS),
      ),

    /**
     * The audit console. Visibility is applied by the API from the caller's
     * token: an Admin sees their linked pharmacies and their own actions.
     */
    auditEvents: async (filters: AuditEventFilters = {}) =>
      jsonOrThrow<AuditEventList>(
        await authedFetch(`/api/admin/audit-events?${auditEventQuery(filters)}`, FETCH_OPTS),
      ),

    /** The same filters as CSV (max 10,000 rows). A navigation, not a fetch. */
    auditEventsExportUrl: (filters: AuditEventFilters = {}) =>
      `/api/admin/audit-events/export.csv?${auditEventQuery({ ...filters, skip: undefined, take: undefined })}`,

    /** The legacy staff log. SuperAdmin-only; superseded by auditEvents. */
    audit: async (filters: AuditFilters = {}) => {
      const q = new URLSearchParams();
      if (filters.action) q.set("action", filters.action);
      if (filters.search) q.set("search", filters.search);
      if (filters.days) q.set("days", String(filters.days));
      q.set("skip", String(filters.skip ?? 0));
      q.set("take", String(filters.take ?? 50));
      return jsonOrThrow<AuditList>(
        await authedFetch(`/api/admin/audit?${q}`, FETCH_OPTS),
      );
    },

    /**
     * Promotes or demotes a user. SuperAdmin only — the API enforces that,
     * and also refuses to demote the last active SuperAdmin or to strip an
     * Admin who still has pharmacies assigned.
     */
    changeRole: async (userId: string, role: UserRole) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/admin/users/${userId}/role`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ role }),
          credentials: "same-origin",
        }),
      ),

    listUsers: async (filters: AdminUserFilters = {}) => {
      const qs = new URLSearchParams();
      if (filters.search) qs.set("search", filters.search);
      if (filters.role) qs.set("role", filters.role);
      if (filters.isActive !== undefined) qs.set("isActive", String(filters.isActive));
      if (filters.tier) qs.set("tier", filters.tier);
      if (filters.managedBy) qs.set("managedBy", filters.managedBy);
      if (filters.unassignedOnly) qs.set("unassignedOnly", "true");
      if (filters.pharmacyAccounts) qs.set("pharmacyAccounts", "true");
      if (filters.onboarded !== undefined) qs.set("onboarded", String(filters.onboarded));
      if (filters.sort) qs.set("sort", filters.sort);
      if (filters.from) qs.set("from", filters.from);
      if (filters.to) qs.set("to", filters.to);
      qs.set("skip", String(filters.skip ?? 0));
      qs.set("take", String(filters.take ?? 25));
      return jsonOrThrow<AdminUserList>(
        await authedFetch(`/api/admin/users?${qs}`, FETCH_OPTS),
      );
    },

    listAdmins: async () =>
      jsonOrThrow<AdminSummary[]>(await authedFetch("/api/admin/admins", FETCH_OPTS)),

    createAdmin: async (body: CreateAdminRequest) =>
      jsonOrThrow<AdminSummary>(
        await authedFetch("/api/admin/admins", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    updateAdmin: async (id: string, body: UpdateAdminRequest) =>
      jsonOrThrow<AdminSummary>(
        await authedFetch(`/api/admin/admins/${id}`, {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    setUserActive: async (id: string, active: boolean) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/admin/users/${id}/${active ? "reactivate" : "deactivate"}`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),

    assignUser: async (userId: string, adminId: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/admin/users/${userId}/assign`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ userId, adminId }),
          credentials: "same-origin",
        }),
      ),

    unassignUser: async (userId: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/admin/users/${userId}/unassign`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),

    overrideTier: async (userId: string, tier: SubscriptionTier, reason?: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/admin/users/${userId}/tier`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ tier, reason }),
          credentials: "same-origin",
        }),
      ),

    /**
     * Grants or withdraws the ability to run branches.
     *
     * Withdrawing does not detach existing branches — it only stops new ones
     * being created. Audited server-side either way.
     */
    setHeadOffice: async (userId: string, granted: boolean, reason?: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/admin/users/${userId}/head-office`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ granted, reason }),
          credentials: "same-origin",
        }),
      ),

    grantCredits: async (userId: string, credits: number, reason?: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/admin/users/${userId}/credits`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ credits, reason }),
          credentials: "same-origin",
        }),
      ),

    /** General (global) patient groups. SuperAdmin-only. */
    globalGroups: {
      list: async () =>
        jsonOrThrow<GlobalGroup[]>(
          await authedFetch("/api/admin/global-groups", FETCH_OPTS),
        ),

      create: async (body: UpsertPatientGroupRequest) =>
        jsonOrThrow<GlobalGroup>(
          await authedFetch("/api/admin/global-groups", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
            credentials: "same-origin",
          }),
        ),

      update: async (id: string, body: UpsertPatientGroupRequest) =>
        jsonOrThrow<GlobalGroup>(
          await authedFetch(`/api/admin/global-groups/${id}`, {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
            credentials: "same-origin",
          }),
        ),

      /** Deletes the group and removes it from every pharmacy's patients. */
      remove: async (id: string) =>
        jsonOrThrow<boolean>(
          await authedFetch(`/api/admin/global-groups/${id}`, {
            method: "DELETE",
            credentials: "same-origin",
          }),
        ),
    },

    /** Pharmacist invitations. SuperAdmin-only; the API returns 403 otherwise. */
    invitations: {
      list: async (
        filters: { search?: string; status?: InvitationStatus; skip?: number; take?: number } = {},
      ) => {
        const qs = new URLSearchParams();
        if (filters.search) qs.set("search", filters.search);
        if (filters.status) qs.set("status", filters.status);
        qs.set("skip", String(filters.skip ?? 0));
        qs.set("take", String(filters.take ?? 50));
        return jsonOrThrow<InvitedList>(
          await authedFetch(`/api/admin/invitations?${qs}`, FETCH_OPTS),
        );
      },

      invite: async (body: InvitePharmacistRequest) =>
        jsonOrThrow<InviteResult>(
          await authedFetch("/api/admin/invitations", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
            credentials: "same-origin",
          }),
        ),

      inviteMany: async (pharmacists: InvitePharmacistRequest[]) =>
        jsonOrThrow<BulkInviteResult>(
          await authedFetch("/api/admin/invitations/bulk", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ pharmacists }),
            credentials: "same-origin",
          }),
        ),

      /**
       * Parses a CSV without creating anything.
       *
       * FormData, so no Content-Type header — the browser sets it with the
       * multipart boundary, and setting it by hand produces a body the server
       * cannot split.
       */
      previewCsv: async (file: File) => {
        const form = new FormData();
        form.append("file", file);
        return jsonOrThrow<CsvPreview>(
          await authedFetch("/api/admin/invitations/preview-csv", {
            method: "POST",
            body: form,
            credentials: "same-origin",
          }),
        );
      },

      resend: async (userId: string) =>
        jsonOrThrow<InviteResult>(
          await authedFetch(`/api/admin/invitations/${userId}/resend`, {
            method: "POST",
            credentials: "same-origin",
          }),
        ),

      csvTemplateUrl: "/api/admin/invitations/csv-template",
    },

    /**
     * Platform-wide settings. Every call here is SuperAdmin-only and the API
     * returns 403 for an ordinary Admin — the UI hides the page as a courtesy,
     * not as the control.
     */
    settings: {
      videoModel: async () =>
        jsonOrThrow<VideoModelSetting>(
          await authedFetch("/api/admin/settings/video-model", FETCH_OPTS),
        ),

      setVideoModel: async (model: string) =>
        jsonOrThrow<VideoModelSetting>(
          await authedFetch("/api/admin/settings/video-model", {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ model }),
            credentials: "same-origin",
          }),
        ),

      /** Drops the override so the deployed environment value applies again. */
      resetVideoModel: async () =>
        jsonOrThrow<VideoModelSetting>(
          await authedFetch("/api/admin/settings/video-model", {
            method: "DELETE",
            credentials: "same-origin",
          }),
        ),
    },

    /**
     * The support view of one pharmacy — what they have created, for
     * answering a query about it.
     *
     * Read-only apart from `resendCampaign`. Patient contact details arrive
     * masked from the API; `revealPatient` is the only way to the real values
     * and it writes an audit entry before returning them.
     */
    support: {
      overview: async (pharmacyId: string) =>
        jsonOrThrow<PharmacySupportOverview>(
          await authedFetch(`/api/admin/pharmacies/${pharmacyId}/overview`, FETCH_OPTS),
        ),

      patients: async (pharmacyId: string, filters: SupportPatientFilters = {}) => {
        const qs = new URLSearchParams();
        if (filters.search) qs.set("search", filters.search);
        if (filters.groupId) qs.set("groupId", filters.groupId);
        qs.set("skip", String(filters.skip ?? 0));
        qs.set("take", String(filters.take ?? 25));
        return jsonOrThrow<SupportPatientList>(
          await authedFetch(`/api/admin/pharmacies/${pharmacyId}/patients?${qs}`, FETCH_OPTS),
        );
      },

      /**
       * Unmask one patient's email and phone.
       *
       * The reason is required by the API, not just by this signature — it is
       * written verbatim into the audit trail alongside the administrator's
       * identity. Anything shorter than five characters is refused, so "x"
       * will not do.
       */
      revealPatient: async (pharmacyId: string, patientId: string, reason: string) =>
        jsonOrThrow<RevealedPatientContact>(
          await authedFetch(
            `/api/admin/pharmacies/${pharmacyId}/patients/${patientId}/reveal`,
            {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ reason }),
              credentials: "same-origin",
            },
          ),
        ),

      groups: async (pharmacyId: string) =>
        jsonOrThrow<SupportGroup[]>(
          await authedFetch(`/api/admin/pharmacies/${pharmacyId}/groups`, FETCH_OPTS),
        ),

      posters: async (pharmacyId: string) =>
        jsonOrThrow<SupportPoster[]>(
          await authedFetch(`/api/admin/pharmacies/${pharmacyId}/posters`, FETCH_OPTS),
        ),

      videos: async (pharmacyId: string) =>
        jsonOrThrow<SupportVideo[]>(
          await authedFetch(`/api/admin/pharmacies/${pharmacyId}/videos`, FETCH_OPTS),
        ),

      campaigns: async (pharmacyId: string) =>
        jsonOrThrow<SupportCampaign[]>(
          await authedFetch(`/api/admin/pharmacies/${pharmacyId}/campaigns`, FETCH_OPTS),
        ),

      campaign: async (pharmacyId: string, campaignId: string) =>
        jsonOrThrow<SupportCampaignDetail>(
          await authedFetch(
            `/api/admin/pharmacies/${pharmacyId}/campaigns/${campaignId}`,
            FETCH_OPTS,
          ),
        ),

      /**
       * Re-send a campaign that failed. Refused for any other status, and
       * re-checks the pharmacy's monthly recipient allowance first.
       */
      resendCampaign: async (pharmacyId: string, campaignId: string) =>
        jsonOrThrow<SupportCampaignDetail>(
          await authedFetch(
            `/api/admin/pharmacies/${pharmacyId}/campaigns/${campaignId}/resend`,
            { method: "POST", credentials: "same-origin" },
          ),
        ),
    },
  },

  // ---- Social publishing ----
  // Platform credentials never travel through here in either direction. The
  // OAuth flow returns opaque handles; the tokens behind them stay on the
  // server for the ten minutes the pharmacy has to choose an account.
  social: {
    connections: async () =>
      jsonOrThrow<SocialConnection[]>(
        await authedFetch("/api/social/connections", FETCH_OPTS),
      ),

    /** What can be connected here, so buttons that would fail say why instead. */
    availability: async () =>
      jsonOrThrow<SocialAvailability>(
        await authedFetch("/api/social/availability", FETCH_OPTS),
      ),

    /**
     * Starts an OAuth flow. The returned `state` is generated server-side and
     * doubles as the session id the callback redeems — don't invent your own.
     */
    startConnect: async (kind: SocialOAuthProviderKind, redirectUri: string) =>
      jsonOrThrow<OAuthStart>(
        await authedFetch("/api/social/connections/start", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ kind, redirectUri }),
          credentials: "same-origin",
        }),
      ),

    /**
     * Exchanges the callback code and lists what was found. Connects nothing.
     * Single use: the server consumes the state, so call this once per
     * callback (the redirect URI is the one the server recorded at start).
     */
    completeConnect: async (
      kind: SocialOAuthProviderKind,
      code: string,
      state: string,
    ) =>
      jsonOrThrow<DiscoveredAccount[]>(
        await authedFetch("/api/social/connections/callback", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ kind, code, state }),
          credentials: "same-origin",
        }),
      ),

    connect: async (sessionId: string, handles: string[]) =>
      jsonOrThrow<SocialConnection[]>(
        await authedFetch("/api/social/connections", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ sessionId, handles }),
          credentials: "same-origin",
        }),
      ),

    /** TikTok's live creator info for the share screen. Never cache it. */
    tikTokCreator: async (connectionId: string) =>
      jsonOrThrow<TikTokCreatorInfo>(
        await authedFetch(`/api/social/connections/${connectionId}/tiktok-creator`, FETCH_OPTS),
      ),

    disconnect: async (id: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/social/connections/${id}`, {
          method: "DELETE",
          credentials: "same-origin",
        }),
      ),

    /**
     * `view` filters on the server: "upcoming" is anything still to go out,
     * "history" everything finished. Filtering a page client-side instead
     * lets one kind crowd the other off the page.
     */
    posts: async (
      status?: PublishStatus,
      skip = 0,
      take = 50,
      view?: "upcoming" | "history",
    ) => {
      const qs = new URLSearchParams();
      if (status) qs.set("status", status);
      if (view) qs.set("view", view);
      qs.set("skip", String(skip));
      qs.set("take", String(take));
      return jsonOrThrow<ScheduledPostList>(
        await authedFetch(`/api/social/posts?${qs}`, FETCH_OPTS),
      );
    },

    post: async (id: string) =>
      jsonOrThrow<ScheduledPost>(
        await authedFetch(`/api/social/posts/${id}`, FETCH_OPTS),
      ),

    /**
     * Queues a publish. "Now" still goes through the queue — omit
     * `scheduledForUtc` and the worker picks it up on its next poll, which is
     * why the UI should say "queued" rather than implying it is instant.
     */
    createPost: async (body: CreatePublishRequest) =>
      jsonOrThrow<ScheduledPost>(
        await authedFetch("/api/social/posts", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    /** Re-attempts only the targets that have not already succeeded. */
    retryPost: async (id: string) =>
      jsonOrThrow<ScheduledPost>(
        await authedFetch(`/api/social/posts/${id}/retry`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),

    cancelPost: async (id: string) =>
      jsonOrThrow<boolean>(
        await authedFetch(`/api/social/posts/${id}/cancel`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),

    reschedulePost: async (id: string, scheduledForUtc: string | null) =>
      jsonOrThrow<ScheduledPost>(
        await authedFetch(`/api/social/posts/${id}/reschedule`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ scheduledForUtc }),
          credentials: "same-origin",
        }),
      ),

    /* ---- recurring series ---- */

    series: async () =>
      jsonOrThrow<RecurringSeriesList>(await authedFetch("/api/social/series", FETCH_OPTS)),

    seriesOne: async (id: string) =>
      jsonOrThrow<RecurringSeries>(await authedFetch(`/api/social/series/${id}`, FETCH_OPTS)),

    /** The dates a rule would post on. Saves nothing. */
    previewSeries: async (body: SeriesPreviewRequest) =>
      jsonOrThrow<SeriesPreview>(
        await authedFetch("/api/social/series/preview", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    createSeries: async (body: SeriesRequest) =>
      jsonOrThrow<RecurringSeries>(
        await authedFetch("/api/social/series", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    /** Future occurrences only; anything already sent stays as it was. */
    updateSeries: async (id: string, body: SeriesRequest) =>
      jsonOrThrow<RecurringSeries>(
        await authedFetch(`/api/social/series/${id}`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    pauseSeries: async (id: string) =>
      jsonOrThrow<RecurringSeries>(
        await authedFetch(`/api/social/series/${id}/pause`, { method: "POST", credentials: "same-origin" }),
      ),

    resumeSeries: async (id: string) =>
      jsonOrThrow<RecurringSeries>(
        await authedFetch(`/api/social/series/${id}/resume`, { method: "POST", credentials: "same-origin" }),
      ),

    endSeries: async (id: string) =>
      jsonOrThrow<RecurringSeries>(
        await authedFetch(`/api/social/series/${id}/end`, { method: "POST", credentials: "same-origin" }),
      ),
  },

  // ---- Google Ads ----
  ads: {
    campaigns: async () =>
      jsonOrThrow<GoogleAdsCampaign[]>(
        await authedFetch("/api/ads/campaigns", FETCH_OPTS),
      ),

    accounts: async (connectionId: string) =>
      jsonOrThrow<AdsAccount[]>(
        await authedFetch(`/api/ads/connections/${connectionId}/accounts`, FETCH_OPTS),
      ),

    /**
     * Creates a campaign. It is created paused — enabling is a separate act.
     * The Ads account is the connection's own; the server takes it from there.
     */
    createCampaign: async (body: CreateAdsCampaignRequest) =>
      jsonOrThrow<GoogleAdsCampaign>(
        await authedFetch("/api/ads/campaigns", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
          credentials: "same-origin",
        }),
      ),

    setCampaignEnabled: async (id: string, enabled: boolean) =>
      jsonOrThrow<GoogleAdsCampaign>(
        await authedFetch(`/api/ads/campaigns/${id}/${enabled ? "enable" : "pause"}`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),

    syncCampaign: async (id: string) =>
      jsonOrThrow<GoogleAdsCampaign>(
        await authedFetch(`/api/ads/campaigns/${id}/sync`, {
          method: "POST",
          credentials: "same-origin",
        }),
      ),
  },

  // ---- Profile / Me ----
  getMe: async () =>
    jsonOrThrow<Me>(
      await authedFetch("/api/me/full", FETCH_OPTS),
    ),

  updateProfile: async (body: UpsertProfileRequest) =>
    jsonOrThrow<Me>(
      await authedFetch("/api/me/profile", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  onboard: async (body: OnboardRequest) =>
    jsonOrThrow<Me>(
      await authedFetch("/api/me/onboard", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  /**
   * Defers setup until the next sign-in. Changes nothing about the account —
   * `onboardingCompleted` stays false, and the app keeps asking.
   */
  skipOnboarding: async () =>
    jsonOrThrow<{ skipped: boolean }>(
      await authedFetch("/api/me/skip-onboarding", {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  // ---- Campaigns ----
  /** Sorted and ranged by the API on sent date (else created). Omit for newest first, all time. */
  listCampaigns: async (params: { sort?: ListSort; from?: string; to?: string } = {}) =>
    jsonOrThrow<Campaign[]>(
      await authedFetch(`/api/campaigns${qs(params)}`, { credentials: "same-origin" }),
    ),

  createCampaign: async (body: CreateCampaignRequest) =>
    jsonOrThrow<Campaign>(
      await authedFetch("/api/campaigns", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  stopCampaign: async (id: string) =>
    jsonOrThrow<Campaign>(
      await authedFetch(`/api/campaigns/${id}/stop`, {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  retryCampaign: async (id: string) =>
    jsonOrThrow<Campaign>(
      await authedFetch(`/api/campaigns/${id}/retry`, {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  // ---- Sharing ----
  sharePosterByEmail: async (
    id: string,
    body: { recipients: string[]; subject?: string; message?: string },
  ) =>
    jsonOrThrow<{ sent: number; failed: number; note?: string }>(
      await authedFetch(`/api/posters/${id}/share-email`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  posterPdfUrl: (id: string) => `/api/posters/${id}/export.pdf`,

  // ---- Patients ----
  listPatients: async (params: {
    search?: string;
    groupId?: string;
    sort?: ListSort;
    from?: string;
    to?: string;
    skip?: number;
    take?: number;
  } = {}) =>
    jsonOrThrow<PatientListResponse>(
      await authedFetch(`/api/patients${qs(params)}`, { credentials: "same-origin" }),
    ),

  getPatient: async (id: string) =>
    jsonOrThrow<Patient>(
      await authedFetch(`/api/patients/${id}`, { credentials: "same-origin" }),
    ),

  createPatient: async (body: UpsertPatientRequest) =>
    jsonOrThrow<Patient>(
      await authedFetch("/api/patients", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  updatePatient: async (id: string, body: UpsertPatientRequest) =>
    jsonOrThrow<Patient>(
      await authedFetch(`/api/patients/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  deletePatient: async (id: string) => {
    const res = await authedFetch(`/api/patients/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  },

  bulkDeletePatients: async (body: BulkDeleteRequest) => {
    const res = await authedFetch("/api/patients/bulk-delete", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  },

  bulkAddPatientsToGroup: async (body: BulkAddToGroupRequest) =>
    jsonOrThrow<{ added: number }>(
      await authedFetch("/api/patients/bulk-add-to-group", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  bulkImportPatients: async (body: BulkImportRequest) =>
    jsonOrThrow<BulkImportResult>(
      await authedFetch("/api/patients/bulk-import", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  // ---- Patient Groups ----
  listPatientGroups: async () =>
    jsonOrThrow<PatientGroup[]>(
      await authedFetch("/api/patient-groups", { credentials: "same-origin" }),
    ),

  getPatientGroup: async (id: string) =>
    jsonOrThrow<PatientGroup>(
      await authedFetch(`/api/patient-groups/${id}`, { credentials: "same-origin" }),
    ),

  createPatientGroup: async (body: UpsertPatientGroupRequest) =>
    jsonOrThrow<PatientGroup>(
      await authedFetch("/api/patient-groups", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  updatePatientGroup: async (id: string, body: UpsertPatientGroupRequest) =>
    jsonOrThrow<PatientGroup>(
      await authedFetch(`/api/patient-groups/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  deletePatientGroup: async (id: string) => {
    const res = await authedFetch(`/api/patient-groups/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  },

  listPatientGroupMembers: async (id: string) =>
    jsonOrThrow<Patient[]>(
      await authedFetch(`/api/patient-groups/${id}/members`, { credentials: "same-origin" }),
    ),

  campaignAudiencePreview: async (body: CampaignAudienceRequest) =>
    jsonOrThrow<CampaignAudiencePreview>(
      await authedFetch("/api/campaigns/audience-preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  // ---- Analytics ----
  getAnalyticsOverview: async () =>
    jsonOrThrow<AnalyticsOverview>(await authedFetch("/api/analytics/overview", FETCH_OPTS)),

  // ---- Videos ----
  /** Sorted and ranged by the API on created date. Omit for newest first, all time. */
  listVideos: async (params: { sort?: ListSort; from?: string; to?: string } = {}) =>
    jsonOrThrow<Video[]>(
      await authedFetch(`/api/videos${qs(params)}`, FETCH_OPTS),
    ),

  /**
   * Which voices this deployment can narrate with.
   *
   * `null` means ai-service could not be reached, and the caller must offer no
   * voice choice rather than a guessed one — see the BFF route.
   */
  getNarrationOptions: async (): Promise<NarrationOptions | null> =>
    jsonOrThrow<NarrationOptions | null>(
      await authedFetch("/api/videos/narration", { credentials: "same-origin" }),
    ),

  /**
   * Store one of the pharmacy's own pictures for a video being created.
   *
   * Uploaded as soon as it is chosen, so the dialog can show a thumbnail and
   * the pharmacist can remove a wrong one before a render is spent.
   */
  uploadVideoImage: async (file: File): Promise<VideoImage> => {
    const fd = new FormData();
    fd.append("file", file);
    // No Content-Type header: the browser sets it, including the multipart
    // boundary. Setting it by hand omits the boundary and the server sees an
    // empty form.
    return jsonOrThrow<VideoImage>(
      await authedFetch("/api/videos/images", {
        method: "POST",
        body: fd,
        credentials: "same-origin",
      }),
    );
  },

  createVideo: async (body: CreateVideoRequest) =>
    jsonOrThrow<Video>(
      await authedFetch("/api/videos", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        credentials: "same-origin",
      }),
    ),

  retryVideo: async (id: string) =>
    jsonOrThrow<Video>(
      await authedFetch(`/api/videos/${id}/retry`, {
        method: "POST",
        credentials: "same-origin",
      }),
    ),

  /**
   * Asks what a change would do. Runs nothing.
   *
   * Separate from `refineVideo` so the pharmacist sees the plan before agreeing
   * to it — see RefinePlan for why that matters. Calling this also *stores* the
   * plan against the video, which is what the confirmation is then checked
   * against, so the two calls are a pair rather than two independent requests.
   */
  previewRefine: async (id: string, instruction: string) =>
    jsonOrThrow<RefinePlan>(
      await authedFetch(`/api/videos/${id}/refine/preview`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ instruction } satisfies RefineRequest),
        credentials: "same-origin",
      }),
    ),

  /**
   * Runs the change that was previewed.
   *
   * The instruction goes back up and must match the stored plan. That is what
   * makes the confirmation mean something: without it a request could carry one
   * sentence past a plan made for another, and the difference between those two
   * sentences is the difference between a free re-cut and a billable rewrite.
   */
  refineVideo: async (id: string, instruction: string) =>
    jsonOrThrow<Video>(
      await authedFetch(`/api/videos/${id}/refine`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ instruction } satisfies RefineRequest),
        credentials: "same-origin",
      }),
    ),

  /**
   * Add pictures to a finished video and re-cut it with them in.
   *
   * No preview and no instruction to echo back, unlike `refineVideo`. That
   * handshake exists because a model interpreted a sentence and could have
   * interpreted it wrongly; a photograph is not open to interpretation, so
   * there is nothing to show back and agree to.
   */
  /** Every shot of a finished video, for the refine dialog's picker. */
  listVideoShots: async (id: string) =>
    jsonOrThrow<VideoShot[]>(await authedFetch(`/api/videos/${id}/shots`)),

  /** Re-cut with new footage at the shots the pharmacist tapped. Free. */
  changeVideoShots: async (id: string, shots: ShotChoice[]) =>
    jsonOrThrow<Video>(
      await authedFetch(`/api/videos/${id}/shots`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ shots } satisfies ChangeShotsRequest),
      }),
    ),

  addVideoPictures: async (id: string, images: VideoImage[]) =>
    jsonOrThrow<Video>(
      await authedFetch(`/api/videos/${id}/pictures`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ images } satisfies AddVideoPicturesRequest),
        credentials: "same-origin",
      }),
    ),

  deleteVideo: async (id: string) => {
    const res = await authedFetch(`/api/videos/${id}`, {
      method: "DELETE",
      credentials: "same-origin",
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  },
};

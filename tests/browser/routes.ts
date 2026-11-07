/**
 * Every page in the app, who should be able to reach it, and what must be on it.
 *
 * Derived by hand from apps/web/src/app rather than by globbing the filesystem,
 * on purpose. A glob would say a page exists; it would not say who may open it
 * or what makes it "working" — and every bug this suite exists to catch was a
 * page that existed and rendered while being wrong or unreachable.
 *
 * `check-routes.mjs` already fails the build when this list drifts from the
 * filesystem, so the hand-written part cannot silently rot.
 */

export type Role = "guest" | "pharmacy" | "superadmin";

export interface PageSpec {
  /** URL path. `:id` is substituted from a fixture at run time. */
  path: string;
  /** Roles that must be able to open it and see content. */
  allow: Role[];
  /** Roles that must NOT get the page — expected to be redirected away. */
  deny?: Role[];
  /** Text that must appear once loaded. Proves it rendered, not just 200'd. */
  expect?: string;
  /**
   * What "rendered" means for this page.
   *
   * `text` (the default) counts visible characters, which catches a blank
   * shell. That is the wrong question for the poster editor: it is a drawing
   * surface with sixteen characters of chrome ("Layers", "Properties"), so a
   * character count would either fail on a working page or be lowered until it
   * proved nothing. `canvas` asserts the Konva stage actually mounted, which is
   * both stricter and the thing a user would notice.
   */
  rendered?: "text" | "canvas";
  /**
   * Needs a real record to exist. Skipped with a message rather than failed
   * when the fixture could not be created — a skip is honest, a pass is not.
   */
  needsFixture?: "poster" | "template" | "pharmacy" | "branch";
}

export const PAGES: PageSpec[] = [
  // ── public ────────────────────────────────────────────────────────────
  { path: "/",         allow: ["guest", "pharmacy", "superadmin"] },
  { path: "/login",    allow: ["guest"], deny: ["pharmacy"] },
  { path: "/register", allow: ["guest"], deny: ["pharmacy"] },
  { path: "/templates", allow: ["guest", "pharmacy", "superadmin"] },
  { path: "/pricing",  allow: ["guest", "pharmacy"] },

  // ── pharmacy app ──────────────────────────────────────────────────────
  // `deny: guest` on every one of these is the check that the middleware
  // matcher actually covers the route. /pharmacies was in the protected list
  // but missing from the matcher, so its guard never ran.
  { path: "/dashboard",   allow: ["pharmacy"], deny: ["guest"] },
  { path: "/posters",     allow: ["pharmacy"], deny: ["guest"], expect: "Posters" },
  { path: "/videos",      allow: ["pharmacy"], deny: ["guest"], expect: "Videos" },
  { path: "/patients",    allow: ["pharmacy"], deny: ["guest"], expect: "Patients" },
  { path: "/campaigns",   allow: ["pharmacy"], deny: ["guest"], expect: "Campaigns" },
  { path: "/social",      allow: ["pharmacy"], deny: ["guest"] },
  { path: "/analytics",   allow: ["pharmacy"], deny: ["guest"] },
  { path: "/brand-kit",   allow: ["pharmacy"], deny: ["guest"] },
  { path: "/pharmacies",  allow: ["pharmacy"], deny: ["guest"], expect: "Branches" },

  // In-pharmacy screens. Both are ordinary signed-in pages; the kiosk they
  // configure is /display — singular, public, and deliberately absent from
  // this list because it belongs to a television rather than to a person.
  { path: "/displays",  allow: ["pharmacy"], deny: ["guest"], expect: "Screens" },
  { path: "/playlists", allow: ["pharmacy"], deny: ["guest"], expect: "Playlists" },
  // One branch's records, read by the head office that runs it. Fixture is a
  // branch of the signed-in account, so an account with none skips rather than
  // borrowing somebody else's id — which the server would refuse anyway.
  { path: "/pharmacies/:id", allow: ["pharmacy"], deny: ["guest"],
    expect: "Back to branches", needsFixture: "branch" },
  { path: "/profile",     allow: ["pharmacy"], deny: ["guest"] },
  { path: "/canvas",      allow: ["pharmacy"], deny: ["guest"] },

  // ── admin console ─────────────────────────────────────────────────────
  // `deny: pharmacy` throughout: a signed-in pharmacy reaching the admin
  // console is the worst failure this suite can find.
  { path: "/admin",               allow: ["superadmin"], deny: ["guest", "pharmacy"] },
  { path: "/admin/users",         allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/pharmacies",    allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/templates",     allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/templates/new", allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/analytics",     allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/subscriptions", allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/access-requests", allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/audit",         allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/admins",        allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/settings",      allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/global-groups", allow: ["superadmin"], deny: ["pharmacy"] },
  { path: "/admin/invitations",   allow: ["superadmin"], deny: ["pharmacy"] },

  // ── record pages ──────────────────────────────────────────────────────
  { path: "/posters/:id",           allow: ["pharmacy"],   needsFixture: "poster",
    rendered: "canvas" },
  { path: "/admin/pharmacies/:id",  allow: ["superadmin"], needsFixture: "pharmacy" },
];

/**
 * Downloads: links the browser navigates to rather than fetches.
 *
 * These are the ones that broke most quietly. A download that goes through a
 * JSON proxy answers 200 with `null` and a JSON content-type; one with no route
 * answers Next's 404 as an HTML page. Both look fine in an API log, and neither
 * throws anything a page-level check would notice — so the assertion is on the
 * content-type, not on the status.
 */
export const DOWNLOADS = [
  {
    path: "/api/admin/invitations/csv-template",
    role: "superadmin" as Role,
    mustNotBe: ["application/json"],
    expectContains: "csv",
  },
  {
    path: "/api/posters/:id/export.pdf",
    role: "pharmacy" as Role,
    needsFixture: "poster" as const,
    mustNotBe: ["application/json"],
    expectContains: "pdf",
  },
];

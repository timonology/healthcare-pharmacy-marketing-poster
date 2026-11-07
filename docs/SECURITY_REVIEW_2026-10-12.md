# Security review — round 3 (12 October 2026)

Third review, following the first (SEC-xx, September) and the follow-up
(R2-xx, 3 October). Scope: everything built or changed since 3 October —
social publishing and Meta callbacks, Google Ads, video pictures and shot
edits, the poster card endpoint, the SEO routes, diagnostics — plus a
regression check of every earlier fix and a fresh look at dependencies and
deployment.

Not covered: live penetration testing, the real Azure configuration, and
per-CVE applicability testing for the framework advisories below.

## Summary

The application code holds up. The new features follow the patterns the
earlier reviews put in place: every new endpoint checks ownership, every blob
key from a request is checked against the caller's own prefix, signed links are
used where there is no session, provider callbacks verify signatures, and no
earlier fix has regressed.

The two serious items are **the platforms underneath**, not the code:
Next.js 14 has 2026 security advisories with no 14.x fix (R3-01), and .NET 8
stops receiving security fixes on **10 November 2026** — four weeks away
(R3-02). Both need the upgrades planned in `UPGRADE_RUNBOOK_2026-10.md`.

Five code and build items were found and fixed the same day (R3-03 to R3-05,
R3-07, and the diagnostics gap behind the Google Ads support question).

| ID | Severity | Finding | Status |
|---|---|---|---|
| R3-01 | **High** | Next.js 14 is end-of-life and has unpatched 2026 advisories | Done in code (Next 15.5.27, React 19.2.8) — lockfile, build and verify pending |
| R3-02 | **High** (from 10 Nov) | .NET 8 support ends 10 November 2026 | Done in code (.NET 10, packages 10.0.12) — build and verify pending |
| R3-03 | Medium | Local secret files were inside the web image's build context | Fixed |
| R3-04 | Low | Development credentials were published inside the production API image | Fixed |
| R3-05 | Low | Web proxies could be steered to a different API path by a crafted link | Fixed |
| R3-06 | Low | A head office "managing as" a branch can open that branch's billing | Fixed (13 Oct): billing is for the account holder only |
| R3-07 | Low | CSV formula neutralising missed cells starting with a tab | Fixed |
| R3-08 | Info | Some provider error text is shown to pharmacies verbatim | Accepted, noted |
| R3-09 | Info | Carried from R2-10 and SEC-09: Azure network exposure, Key Vault, CSP script-src | CSP: nonce policy shipped report-only. Network and Key Vault: open, design decision needed |

## Findings

### R3-01 — Next.js 14 is end-of-life and has unpatched 2026 advisories (High)

R2-02 flagged Next.js 14 as end-of-life. Since then the framework has shipped
security releases in May, July, August and September 2026, covering
middleware and proxy bypass, cache poisoning, XSS, SSRF and a Windows-only
RCE. Several list affected ranges that include 14.x with fixes only in 15.5.x
and 16.x.

What reduces the exposure here: the app runs on Linux (the August RCE is
Windows-only); it does not use `next/image` or `images.remotePatterns` (the
September SSRF needs them); it uses no `beforeInteractive` scripts; and
middleware only routes the UI — every permission is enforced by the .NET API,
so a middleware bypass reaches a page that then cannot load data. That makes
the known advisories hard to exploit here, but the version will receive no
further fixes.

**Action:** upgrade to Next.js 15.5.x or 16.x (React 19) as set out in
`UPGRADE_RUNBOOK_2026-10.md`. Until then, keep the web app behind the API's
authorisation (as now) and do not add `next/image` remote patterns.

### R3-02 — .NET 8 support ends 10 November 2026 (High from that date)

Microsoft ends security updates for .NET 8 and 9 on 10 November 2026; .NET 10
is the LTS successor. The API image uses the floating `aspnet:8.0` tag, so
rebuilding picks up the last 8.0 patch — at least 8.0.21, which fixes the
Kestrel request-smuggling issue CVE-2025-55315 — but nothing after
10 November.

**Action:** rebuild and redeploy the API now so it runs the latest 8.0.x, then
move to .NET 10 before 10 November (runbook section 3). If that date cannot be
met, schedule the move for the first sprint after it; there is no workaround.

### R3-03 — Local secret files inside the web image's build context (Medium) — Fixed

The web image builds from the repository root with `COPY . .` in its build
stage. `.dockerignore` excluded `.env` but not the local notes file `.test`,
which holds a Stripe test key and webhook secret, an Anthropic API key, and
Google and Meta client secrets. It was also sending the whole API and
ai-service source, including `appsettings.Development.json`, into that stage.
The final image copies only the built output, so the secrets were not in the
running image, but they were in the build stage's layers on every machine and
cache that built it.

**Fixed:** `.dockerignore` now excludes `.test`, every `.env.*` except the
examples, key and certificate files, and `apps/api` and `apps/ai-service`
(the web build needs neither).

**Also do:** move the contents of `.test` into a password manager and delete
the file. Rotate the Anthropic key in it if any image built from this
repository has been pushed with build-stage caching (`--cache-to`).

### R3-04 — Development credentials published in the production API image (Low) — Fixed

`appsettings.Development.json`, which holds the development Meta and Google
OAuth credentials (accepted for development under R2-01), was copied into the
published output and so into every API image. It is never loaded outside
Development, but an image is copied to registries and laptops that the
repository is not.

**Fixed:** the API project now marks that file `CopyToPublishDirectory=Never`.

### R3-05 — Web proxies could be steered to a different API path (Low) — Fixed

Route parameters reach the web app's proxy routes decoded, so a link such as
`/api/posters/..%2F..%2Fadmin%2Fusers/export.png` gave a route the id
`../../admin/users`. `fetch` then normalised the upstream URL to another
endpoint and called it with the signed-in visitor's session. The API still
enforced every permission and the response went only to the victim, so this
granted nothing, but a link should not choose which endpoint a victim calls.

**Fixed:** one check, `isSafeApiPath`, now runs in `apiFetch`, `proxyDownload`
and `proxyImage`. It refuses dot-segments, fragments, backslashes and control
characters, and anything outside `/api/`. The two routes that call the API
directly encode the id and refuse `.`/`..`. Test: `api-path.test.ts`.

### R3-06 — Head office "manage as" can open a branch's billing (Low) — Fixed (13 Oct)

A delegated session (a head office managing a branch) is refused anything
that reshapes the account hierarchy, but it could start a Stripe checkout or
open the billing portal for the branch: change its card, cancel its plan. The
audit trail recorded who did it, but could not undo it.

**Decision (owner, 13 Oct):** billing belongs to the pharmacy's own account
holder. Somebody working as another pharmacy — a head office, a colleague
with a membership, platform support — may see its plan but not change it.
Central billing for groups, if wanted, is a separate feature.

**Fixed:** `[OwnAccountOnly]` (`Acme.WebApi/Delegation.cs`) refuses
`billing/checkout`, `billing/credits`, `billing/portal` and
`subscription/upgrade` with 403 and `code: "own_account_only"`. It runs
before model validation, and the attempt is still audited. `billing/status`
stays readable and carries `canManageBilling: false`; the pricing page
shows the plan with a note instead of the buttons.

"Working as another" means the actor claim is present **and differs from the
subject**. Switching back to your own pharmacy issues a delegated token with
yourself as actor; the earlier "actor claim present" test would have locked
people out of their own billing after an ordinary switch home, and was already
refusing branch creation in that state. `PharmaciesController` now uses the
same rule.

Tests: `OwnAccountOnlyTests` (the rule, plus a reflection sweep requiring every
billing change to carry the attribute), `BillingDelegationTests` (integration:
refused as a branch, plan unchanged, status visible, own account and
switched-home unaffected) and the browser journey "a head office working as a
branch".

### R3-07 — CSV formula neutralising missed a leading tab (Low) — Fixed

Patient exports prefix cells starting with `=`, `+`, `-` or `@` so a
spreadsheet treats them as text. The audit export also covered a leading tab or
carriage return, which some programs read the same way; the patient export did
not. **Fixed** in `Csv.Escape`; test case added to `PatientCsvTests`.

### R3-08 — Provider error text shown verbatim (Info) — Accepted

Google Ads and OAuth failures surface the provider's message to the pharmacy
("Google Ads refused the request: …"). This is deliberate — those messages say
which field is wrong — and they carry no secret. A generic network exception
would show its own text (a host name, a timeout). Noted; no change.

### R3-09 — Carried forward (Info)

- **R2-10 (partly fixed):** the API and ai-service accept traffic from the
  internet rather than only from the web app, and secrets are App Service
  settings rather than Key Vault references. Since R2-03, client addresses
  cannot be spoofed by calling the API directly, which removed the main reason
  this mattered. Still recommended: access restrictions so only the web app
  can reach the API, and Key Vault references.
- **SEC-09:** the web CSP has no `script-src`; adding one needs per-request
  nonces. Best done with the Next.js upgrade (R3-01).

## Regression check of earlier fixes

Each was re-read in the current code; none has regressed.

| Fix | Checked |
|---|---|
| SEC-01 / R2-05 image sources | `CanvasImageSources` runs on poster create/update and template create/update; `ResolveForRender` strips disallowed sources before every render, including the new card endpoint; `SvgRasterizer` still refuses DTDs, entities and external references |
| SEC-05 / R2-08 rate limits and lock | Per-address and per-account keys in `MemoryLoginThrottle`; limits registered |
| SEC-08 / R2-11 docs and diagnostics | Swagger in Development only; ai-service `/docs` behind a flag; `/diagnostics/jwt` Development-only on both services; `/api/diagnostics/config` SuperAdmin-only outside Development (the new `social` block reports set/missing, never values) |
| SEC-09 headers | `SecurityHeadersMiddleware` first in the API pipeline; HSTS outside Development; web frame-ancestors and nosniff unchanged |
| R2-03 client address | `Edge:ClientIpKey` signed-address middleware; Bicep sets the key on both apps |
| R2-04 open redirect | `safe-redirect.ts` used by the sign-in page, with tests |
| R2-06 consent and unsubscribe | Signed unsubscribe tokens; anonymous endpoint reviewed |
| R2-09 session end | `OnTokenValidated` checks the session guard for the subject and, when delegated, the actor |
| R2-12 canvas limits | `CanvasLimits` on every user-supplied canvas |
| R2-13 key separation | `LinkSigningKeys` in use for unsubscribe, poster image and video download links (display tokens are random device tokens, not signed links) |
| R2-14 ImageMagick | URL, HTTP(S), FTP, MVG, MSL and EPHEMERAL coders still disabled |
| R2-16 CI | gitleaks, `pnpm audit`, `dotnet list package --vulnerable`, ai-service audit in `ci.yml` |

## New code reviewed and found sound

- **Meta deauthorize and data-deletion callbacks:** HMAC-SHA256 verified in
  constant time, algorithm pinned, `issued_at` older than a day refused.
- **Social OAuth:** state bound to the starting pharmacy, sessions stored
  server-side with a ten-minute life, redirect URI built by the server, tokens
  encrypted at rest with a versioned key.
- **Google Ads:** every campaign and connection checked against the owner;
  account id cannot be pointed at another customer; budget caps; currency
  checked against the account.
- **Video pictures and shot edits:** every blob key from a request passes
  `VideoImagePolicy.WhyKeyRefused` (owner prefix, no `..`); shot choices are
  checked against the video's own shots.
- **Kiosk video route:** id pattern-checked, signed per video with a time
  window, ranges forwarded, no token or blob key reaches the browser.
- **Poster card endpoint:** owner-only, same-origin, no signed storage URL,
  render concurrency bounded, never writes to the poster.
- **SEO routes:** sitemap and template metadata call only the public template
  endpoints, anonymously (not counted as views); robots.txt keeps crawlers out
  of the app. No new data is exposed.
- **Head office "manage as":** no chaining, child must belong to the caller
  and be able to sign in, 30-minute window, issuance audited — see R3-06 for
  billing.
- **Stripe webhook:** signature and tolerance checked by the Stripe library
  before anything is read.

## To do, in order

1. **This week:** rebuild and redeploy the API image (latest 8.0.x). Move
   `.test` into a password manager and delete it; rotate the Anthropic key if
   build caches were pushed. Decide R3-06.
2. **Before 10 November:** .NET 10 (R3-02).
3. **Next sprint:** Next.js 15.5/16 with React 19 (R3-01), then a CSP
   `script-src` with nonces (SEC-09).
4. **Hardening:** API and ai-service access restrictions and Key Vault
   references (R2-10).

Tests to run for the fixes: `pnpm test:web` (`api-path.test.ts`),
`pnpm test:api` (`PatientCsvTests`), and an API image build to confirm
`appsettings.Development.json` is no longer in `/app`.

## Sources for the platform findings

- Next.js security releases, 2026: <https://nextjs.org/blog/july-2026-security-release>,
  <https://nextjs.org/blog/august-2026-security-release>,
  <https://nextjs.org/blog/september-2026-security-release>,
  <https://vercel.com/changelog/next-js-may-2026-security-release>
- .NET 8 and 9 end of support: <https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/>

## Status update — 12 October 2026 (same day)

- **R3-02:** all API projects target .NET 10; Microsoft packages 10.0.12;
  IdentityModel 8.19.2 (aligned with JwtBearer 10.0.12); Docker images
  `sdk:10.0` / `aspnet:10.0`; CI `10.0.x`; `KnownNetworks` → `KnownIPNetworks`.
  Needs a .NET 10 build with warnings as errors and the test run.
- **R3-01:** Next.js 15.5.27, React 19.3.0; async request APIs and `params`
  migrated (type-checks clean). Needs `pnpm install` (commit the lockfile),
  `pnpm --filter web up "@radix-ui/*"`, build and the browser checks in the
  runbook.
- **SEC-09:** a per-request nonce policy (`script-src 'self' 'nonce-…'
  'strict-dynamic'`, `object-src 'none'`, `base-uri 'self'`) is set by
  middleware on every page, **report-only** by default (`CSP_MODE`).
  Violations are logged by `/api/csp-report`. Switch `CSP_MODE=enforce` once
  a few days of real use produce no reports.
- **R3-06:** fixed 13 Oct — see the finding.
- **R2-10 (network, Key Vault):** not changed. Restricting the API to the web
  app would break what reaches it directly — campaign email images, Stripe
  webhooks, Meta callbacks — so it needs a design choice (route those through
  the web app, or keep the API public and restrict only the ai-service via
  VNet integration). Key Vault references need the deployer to hold a role
  that can assign Key Vault roles. Both are planned as a separate change.

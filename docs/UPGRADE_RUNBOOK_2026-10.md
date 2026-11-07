# Platform upgrades — Next.js and .NET (security review R2-02, R2-07)

Two framework upgrades from the follow-up security review. They could not be
done in the review environment (no package registry, no .NET SDK). This
runbook lists the exact steps so they can be done on a developer machine.

Already done in code:

- The web image runs on Node 22 LTS (`apps/web/Dockerfile`). Node 20 left
  support in April 2026, and Next 14.2 runs on Node 22.
- CI (`.github/workflows/ci.yml`) builds on Node 22 and .NET 8.

---

## 1. Next.js 14 → 15 (R2-02, High)

> **Done in code on 12 October 2026 (security review R3-01).** `next` and
> `eslint-config-next` are 15.5.27 (the September 2026 security release),
> `react`/`react-dom` 19.2.8 (types 19.2), `react-konva` 19.2.7 — **the 19.2 line on purpose**: Next 15.5 runs its own bundled React 19.2 in the browser, and react-konva 19.3 refuses to start on it (`scripts/check-react-versions.mjs` now checks this),
> `next-themes` 0.4.6, `@types/node` 22. The code is already migrated and
> type-checks: `cookies()`/`headers()` are awaited (the cookie and forwarding
> helpers are async), every route handler and server page awaits `params` /
> `searchParams`, the two client pages that took a `params` prop use
> `useParams()`, and the theme provider no longer imports a path next-themes
> 0.4 removed. **What is left, on a machine with the registry:**
>
> ```bash
> pnpm install                                  # updates pnpm-lock.yaml — commit it, CI installs frozen
> pnpm --filter web up "@radix-ui/*"            # React 19 peer ranges
> ```
>
> then the **Verify** list below, plus:
>
> - `pnpm test:web` — three component tests (`PhotoFramer`, `ShotPicker`,
>   `KioskVideo`) mount React into the hand-written DOM in `src/test/dom.ts`.
>   `act` now comes from `react`; if React 19's renderer touches a DOM method
>   that fake does not have, add it there.
> - Open any page and check the response has
>   `Content-Security-Policy-Report-Only` with a `'nonce-…'`, the page's
>   `<script>` tags carry the same nonce, and the browser console shows no
>   CSP reports. Before setting `CSP_MODE=enforce`, confirm the enforced
>   response carries **both** the nonce policy and `frame-ancestors`. `lucide-react` stays on 0.439 (works with
> React 19; pnpm warns about its peer range) — moving to 1.x renames icons and
> is a separate change. Plan Next 16 (Active LTS) next; the async APIs it
> requires are already in place.

Next 14 reached end of life on 26 October 2025. Security releases since then
(May and July 2026) ship only for 15.x and 16.x. Go to the latest **15.5.x**
first. It is the smaller step: it supports React 19 on the App Router and keeps
the `next.config.mjs` options this app uses. Plan 16 as a separate change.

```bash
cd apps/web
pnpm dlx @next/codemod@latest upgrade 15      # bumps next, react, react-dom, eslint-config-next
pnpm add react-konva@^19 konva@^9             # react-konva 18 only supports React 18
pnpm add next-themes@^0.4                     # React 19 peer
pnpm install
```

### What changes in this codebase

| Area | Next 15 change | What to do here |
|---|---|---|
| `cookies()` / `headers()` | Now async (sync access still works in 15 with a warning) | Three files call them: `lib/server/cookies.ts`, `lib/server/forwarding.ts` and `app/layout.tsx`. Make the helpers `async` and `await` them (`apiFetch` and `proxyDownload` are already async, so their callers need no change). |
| Route handler `params` | A Promise | 12 handlers already `await params` (`display/slides/[posterId]` shows the pattern). About ten still read it synchronously, among them `posters/[id]/*`, `patients/[id]`, `assets/templates/[fileName]`, `ads/[...path]` and `displays/[...path]`. The codemod rewrites most; `pnpm tsc` finds the rest. |
| `fetch` caching | No longer cached by default | No change: every server fetch already sets `cache: "no-store"`. |
| React 19 | `forwardRef` still works; some types change | Run `pnpm tsc` and fix what it reports. The editor (react-konva) is the part to test by hand. |
| ESLint | `eslint-config-next` 15 needs flat config | Already on ESLint 9. Check `pnpm lint`. |

### Verify

```bash
pnpm --filter web exec tsc --noEmit
pnpm test:web
node scripts/check-contract.mjs && node scripts/check-routes.mjs && node scripts/check-proxies.mjs
pnpm --filter web build
pnpm test:browser                              # against a local stack
```

Test by hand: sign in and out, the idle sign-out warning, the poster editor
(add, move and resize shapes, image upload, export), the kiosk at `/display`,
the unsubscribe page, and an admin "Design with AI" run.

---

## 2. .NET 8 → .NET 10 (R2-07, Medium; .NET 8 support ends 10 November 2026)

> **Done in code on 12 October 2026 (security review R3-02).** All six
> projects target `net10.0`; `Microsoft.Extensions.*`,
> `Microsoft.AspNetCore.Authentication.JwtBearer` and
> `Microsoft.AspNetCore.Mvc.Testing` are 10.0.12; `System.IdentityModel.Tokens.Jwt`
> and `Microsoft.IdentityModel.Tokens` are 8.19.2, the version JwtBearer 10.0.12
> depends on (keep all `Microsoft.IdentityModel.*` on one version — mixed versions
> fail at run time, not at build); the Dockerfile uses `sdk:10.0` / `aspnet:10.0`
> (Ubuntu 24.04 based — the font packages install the same way); CI uses
> `10.0.x`; `KnownNetworks` became `KnownIPNetworks`. What is left is the
> **Verify** step below on a machine with the .NET 10 SDK: build with warnings as
> errors, fix anything the newer analysers report, run the tests and export a
> poster by hand.
>
> **Package audit.** The .NET 10 SDK audits *transitive* packages and, with
> warnings as errors, fails the restore on any with an advisory. It found three
> the .NET 8 SDK never reported: SharpCompress 0.30.1 and Snappier 1.0.0
> (from MongoDB.Driver 2.x — now **MongoDB.Driver 3.12.0**; the code uses no
> API that 3.0 removed, no Guid fields and no LINQ2) and Newtonsoft.Json 12.0.3
> (from Stripe.net 45 — now pinned to **13.0.3**). `apps/api/global.json` pins
> the SDK to 10.0.100 or later.

.NET 10 is the current LTS. The change is mostly mechanical.

1. In all six projects, set `<TargetFramework>net10.0</TargetFramework>`
   (`apps/api/src/*/*.csproj`, `apps/api/tests/*/*.csproj`).
2. Bump the `Microsoft.*` package references from 8.0.x to the matching
   10.0.x: `Microsoft.AspNetCore.Authentication.JwtBearer`,
   `Microsoft.Extensions.*`. Then update `System.IdentityModel.Tokens.Jwt` /
   `Microsoft.IdentityModel.Tokens` to the version JwtBearer 10 depends on.
3. `apps/api/Dockerfile`: `dotnet/sdk:8.0` → `dotnet/sdk:10.0` and
   `dotnet/aspnet:8.0` → `dotnet/aspnet:10.0`. `USER $APP_UID` is unchanged.
4. `.github/workflows/ci.yml`: `dotnet-version: 10.0.x`.
5. Build with warnings as errors (already on) and fix what the new analysers
   report.

### Known spots to check

- **Forwarded headers.** .NET 10 renames `KnownNetworks` to
  `KnownIPNetworks`. `Program.cs` clears both lists; use the new name if the
  old one is marked obsolete.
- **JWT.** `OnTokenValidated` (the R2-09 session check) uses
  `context.SecurityToken.ValidFrom`, which works with both JWT handlers.
- **QuestPDF / SkiaSharp / Svg.Skia.** Run the export tests
  (`SvgLogoRenderingTests`, the PDF tests) and export a real poster by hand.

### Verify

```bash
pnpm test:api && pnpm test:api:integration
pnpm test:prove -- SEC                       # the security mutation proofs
./scripts/e2e/run.sh                          # against a local stack
```

# Production settings — gap analysis

Compared against every configuration key the code actually reads. Ordered by
how much damage the gap does, not by section.

---

## 0. Security review, 3 October 2026 — what changed at deploy

The fixes for `docs/SECURITY_REVIEW_2026-10-03.pdf` change how the services
start and what they accept. Check these before the first deploy that carries
them.

| Setting / behaviour | What to do |
|---|---|
| **`Jwt__Secret` is now checked at startup** | Outside Development the API **refuses to start** if the secret is missing, under 32 bytes, or still a placeholder (`replace…`, `change_me`, `example`…). Bicep sets it from `jwtSecret`; confirm the App Service value is a real random string (`openssl rand -base64 48`). The ai-service `JWT_SECRET` must be the same value. |
| Rate limits | Shipped defaults: sign-in/sign-up/reset/verify/invitation 10 a minute per address; forgot-password and resend-verification 5 per 15 minutes; screen pairing 10 a minute; share-by-email 10 an hour per person; stock search 20 a minute per person. Override any of them with `RateLimits__AuthPerMinute`, `RateLimits__AccountEmailPer15Minutes`, `RateLimits__DisplayClaimPerMinute`, `RateLimits__ShareEmailPerHour`, `RateLimits__StockSearchPerMinute`. **On the QA environment, raise `RateLimits__AuthPerMinute` (e.g. 600) before running the browser suite** — it signs in many times a minute from one address. |
| Sign-in lock | 8 wrong passwords lock that account for 15 minutes (per API instance). The message tells the person to wait or reset. |
| Password rule | New passwords (sign-up, reset, invitation) need 12+ characters and refuse common ones. Existing passwords keep working. |
| ai-service `APP_ENV` | Leave **unset** in production: `/docs` and `/redoc` are then off. Compose sets `development` locally. |
| Logo uploads | Stored by what the bytes are. SVGs are drawn to PNG at upload; an SVG that links outside itself is refused. |
| Poster images | A poster may only use our own assets, the brand logo/QR routes, inline `data:` images, or `https://images.unsplash.com`. Anything else is refused at save and blanked at render. |
| Containers | All three images run as non-root. The ai-service entrypoint hands an old root-owned media volume to its `app` user on first start. |
| Platform hardening (R2-07, R2-10) | Web image on Node 22. App Services: FTPS off, TLS 1.2 minimum, FTP publishing credentials off. Still manual: restrict Cosmos DB to the App Service outbound IPs (`az cosmosdb update --ip-range-filter …`), switch registry pulls to managed identity and disable the ACR admin user, and move secrets to Key Vault references. The Next.js 15 and .NET 10 upgrades are in `docs/UPGRADE_RUNBOOK_2026-10.md`. |
| Local compose | Ports are bound to `127.0.0.1`. To reach the stack from a TV or phone, run with `BIND_ADDR=0.0.0.0`. |
| Meta and Google OAuth secrets (R2-01) | **Done 3 October 2026:** rotated, and production reads them from `Social__Meta__AppSecret` / `Social__Google__ClientSecret`. The old values stay in `appsettings.Development.json` as development credentials only; that file is never loaded outside Development. |
| **`CLIENT_IP_SIGNING_KEY` (R2-03)** | New. The same long random value goes on the web app (`CLIENT_IP_SIGNING_KEY`) and the API (`Edge__ClientIpKey`); Bicep passes `clientIpSigningKey` to both. Without it the API logs a warning and per-address rate limits can be dodged by calling the API directly. `openssl rand -base64 48`. **Set it on both apps at once** (if only the API has it, every user shares the web app's address and the sign-in limit is shared too). **Pass it on every Bicep deploy**: Bicep replaces all app settings, so leaving it out clears it. Keep it in `test.env` / the production parameters. |
| **Password reset emails not arriving (14 Oct)** | Reported from production while campaign email worked. Three causes, each now visible: **(1)** the web app and API signing keys above differ or one is missing, so every user shares one address and the forgot-password limit (5 per 15 minutes) is used up by anyone — `pnpm smoke` now FAILs this ("users are told apart"), and the API logs every refusal as `Rate limit account-email refused … from <address>`; **(2)** the account was invited or imported and never activated — it used to get nothing and now gets its activation link; **(3)** the provider refused the email — recorded on the token, and the smoke's "password reset email" step reports it. The forgot-password page now says "too many requests" instead of "check your inbox" when refused. |
| **API running as Development (15 Oct smoke)** | The production smoke signed in with the repository's published smoke password, which the API only seeds in Development, and campaigns failed with "Mailjet credentials missing" once `appsettings.Development.json` stopped shipping (R3-04) — production had been sending with the *development* Mailjet keys from that file. Set `ASPNETCORE_ENVIRONMENT=Production` on the API, and put every secret in App Service settings: `Messaging__Mailjet__ApiKey`, `Messaging__Mailjet__SecretKey`, `Edge__ClientIpKey` (and `CLIENT_IP_SIGNING_KEY` on the web app), `Jwt__Secret`. Then change the smoke account's password: it is the published one, and the seeder never updates an existing account. `pnpm smoke` now FAILs "api runs as Production" when this recurs. |
| **Sender after switching to Production (16 Oct)** | Smoke passed, nothing arrived: `appsettings.Production.json` named `no-reply@pharmacyposter.app`, unverified in Mailjet, which accepts and then holds such mail. Removed; the sender is `info@sonarinformatics.com` (appsettings.json) unless `Messaging__Mailjet__FromAddress` is set — set it explicitly, to a sender verified on the account `Messaging__Mailjet__ApiKey` belongs to. The API logs `Mailjet sender … is verified` / `is NOT verified` at startup. |
| **Social token encryption key (21 Oct)** | "Social accounts are not set up on this server yet" after the switch to Production: the only key ever used was the one in `appsettings.Development.json`, which production no longer reads. Existing Facebook/Instagram/Google connections are encrypted with it (version 1), so production must hold **that same key**: `Social__Encryption__ActiveVersion=1`, `Social__Encryption__Keys__0__Version=1`, `Social__Encryption__Keys__0__Key=<the value in appsettings.Development.json>`. A new random key would make every pharmacy reconnect. To rotate later, add `Keys__1` (version 2, `openssl rand -base64 32`) and set `ActiveVersion=2`, keeping version 1 so old tokens still decrypt. `pnpm smoke` now FAILs when the key is missing. The only other value production was taking from that file was the relaxed local rate limits, which it should not have. |
| **Readiness token (22 Oct)** | Set one long random value (`openssl rand -hex 24`) as `Readiness__Token` on the API and `READINESS_TOKEN` on the ai-service (Railway), and run `pnpm smoke` with `SMOKE_READINESS_TOKEN` set to it. Every dependency is then checked from inside each service — database, storage, Mailjet sender, Stripe prices, Meta and Google credentials, that stored social connections open, Anthropic, Pexels, ElevenLabs credit, Replicate — with free reads only. Without it those endpoints are 404. Bicep takes it as `readinessToken`. |
| `BUILD_ID` (all three images) | `scripts/deploy.sh` stamps the SHA tag into the API (`/health`), web app (`/api/health`) and ai-service (`/health`). `pnpm smoke` FAILs when the API and web app are on different builds — a half-finished deploy. Deploy with `pnpm ship:<env>`, not by building images by hand. |
| `LINK_SIGNING_SECRET` (R2-13) | Optional but recommended: `Signing__LinkSecret` on the API. It signs emailed poster-image and video links separately from the JWT secret, so `Jwt__Secret` can then be rotated without breaking links already sent. Set it once and keep it stable. Links sent before this change keep working until they expire. |
| Access tokens (R2-09) | 20 minutes (was 60), matching the web app's 20-minute idle sign-out. Deactivating an account or resetting its password now ends its sessions at once: on the instance that made the change immediately, and on others within 30 seconds. |
| Poster size (R2-12) | Saves over about 1.9 MB or 500 items are refused with a plain message, to fit the Cosmos DB 2 MB document limit. A database without that limit can raise it with `Posters__MaxCanvasBytes` / `Posters__MaxShapes`. The editor now compresses large pictures before adding them. |
| Campaign unsubscribe (R2-06) | Every campaign email carries an unsubscribe link and one-click `List-Unsubscribe` headers. `Public__WebBaseUrl` and `Public__ApiBaseUrl` must be the public addresses, or the links point at localhost. |
| `SITE_URL` (web) | The public web address, used for canonical links, `sitemap.xml`, `robots.txt`, link previews and the home page's structured data. Bicep sets it to `https://${webHostname}`; once the custom domain is live, set `webHostname` to it (e.g. `marketing25.co.uk`) or search engines are told the `azurewebsites.net` address is the real one. Check `/robots.txt` and `/sitemap.xml` name the right host after deploying. |
| **LinkedIn (30 Oct)** | Live in production for profiles. `Social__LinkedIn__ClientId` / `ClientSecret` from the LinkedIn app's Auth tab, which needs the **Sign In with LinkedIn using OpenID Connect** and **Share on LinkedIn** products and the redirect URL `https://marketing25.co.uk/social/callback/linkedin`. Bicep: `linkedInClientId`, `linkedInClientSecret`, `linkedInPagesEnabled` (false until LinkedIn approves Community Management), `linkedInProfilesEnabled`, `linkedInApiVersion` (`202610`). The smoke FAILs a redirect LinkedIn reports as unregistered, and readiness checks the credentials. See `SOCIAL_SETUP.md` §3c. |
| **YouTube (28 Oct)** | Uses the Google OAuth client. Needs the YouTube Data API v3 enabled, `https://marketing25.co.uk/social/callback/youtube` registered, the two YouTube scopes on the consent screen and test users until Google verifies the app. `Social__Google__YouTubeEnabled=false` hides it. While testing, `Social__Google__YouTubePrivacy=unlisted`. See `SOCIAL_SETUP.md` §3b. |
| **Google Ads: developer tokens sunset (4 Nov)** | Google moved Ads API access to the Google Cloud project on 10 Sep 2026; developer tokens are ignored. Leave `Social__Google__AdsDeveloperToken` empty. Enable the **Google Ads API** in the OAuth client's project, verify the project's brand, and request **Basic access** on the project's Google Ads API page in Cloud Console. Until then real accounts answer `CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION`. See `SOCIAL_SETUP.md` §3. |
| **YouTube Data API v3 (4 Nov)** | "YouTube Data API v3 has not been used in project … or it is disabled": enable it in the **same project as the OAuth client on Azure** (the error names the project number). |
| **TikTok (3 Nov)** | Built; sandbox until TikTok audits the app. `Social__TikTok__ClientKey` / `ClientSecret` (Bicep `tikTokClientKey`, `tikTokClientSecret`), redirect `https://marketing25.co.uk/social/callback/tiktok` in Login Kit, Direct Post on, the test account added as a sandbox target user and set to private. See `SOCIAL_SETUP.md` §3d. |
| **X (6 Nov)** | Built. `Social__X__ClientId` / `ClientSecret` — the **OAuth 2.0** Client ID and Secret, not the API key (Bicep `xClientId`, `xClientSecret`). App type *Web App*, permissions *Read and write*, callback `https://marketing25.co.uk/social/callback/x`. **Pay-per-use:** buy credits in the X Developer Console or every post fails with the credits message. See `SOCIAL_SETUP.md` §3e. |
| **`Edge__ClientIpKey` and readiness token still unset (smoke 29 Oct)** | Both still WARN on the production smoke. Set them — see the `CLIENT_IP_SIGNING_KEY` and readiness rows above. |
| Mailjet keys (SEC-03) | **Done 3 October 2026:** rotated, and production reads them from `Messaging__Mailjet__ApiKey` / `SecretKey`. `appsettings.json` (which production also loads) now has none; the old development keys live only in `appsettings.Development.json`. Production cannot fall back to them, and a missing setting fails loudly ("Mailjet credentials are missing"). |

---

## 1. Critical — fix before anything else

### `ASPNETCORE_ENVIRONMENT` is `Development` on production

This one setting turns on five things that should never be live:

| What | Consequence |
|---|---|
| `DevUserSeeder` runs | **12 fake pharmacies and 2 fake admins are being created in your production Atlas database**, all with the password `TestPass123!` at `@example.test`. It runs on every boot and is enabled by default (`DevData:SeedUsers` defaults to true). |
| Swagger is served | Your full API surface is published at `/swagger` to anyone who asks. |
| `RequireHttpsMetadata = false` | JWT bearer validation stops insisting on HTTPS for metadata retrieval. |
| `/api/diagnostics/jwt` answers | Returns a fingerprint of your signing secret. It cannot be reversed, but it lets someone *confirm* a guessed secret. |
| `DevEnvFile` reads a repo `.env` | Config precedence differs from what you tested. |

**Fix**

```
ASPNETCORE_ENVIRONMENT = Production
```

**Then clean up what has already been seeded.** In Atlas, on the `users`
collection:

```js
db.users.deleteMany({ email: /@example\.test$/ })
```

Check the count first with `countDocuments` — and confirm no real account has
ever used that domain. Also change `SuperAdmin__Password`, which is currently
the shipped default `ChangeMe-...`.

### Mailjet credentials are committed to git — resolved 3 October 2026

The production keys were rotated and are set as App Service settings
(`Messaging__Mailjet__ApiKey`, `Messaging__Mailjet__SecretKey`).
`appsettings.json` carries no credentials. The previous keys are kept for
local testing in `appsettings.Development.json`, which only Development loads.
The API logs a warning if a hosted App Service ever runs as Development.

---

## 2. Deployed but broken — features that are live and cannot work

### AI poster generation

`AiService:BaseUrl` is empty in `appsettings.json` and has no env var, so every
generation fails with "AI template generation is not configured."

```
AiService__BaseUrl        = https://<your-ai-service>.azurewebsites.net
AiService__TimeoutSeconds = 210
```

The ai-service itself needs its own app settings — see section 5.

### Social publishing and Google Ads

None of it is configured, so:

- the publish worker logs one error at startup and stands down
- no pharmacy can connect a Facebook, Instagram, YouTube, LinkedIn, TikTok, X or Google account

```
Social__Encryption__ActiveVersion   = 1
Social__Encryption__Keys__0__Version = 1
Social__Encryption__Keys__0__Key     = <openssl rand -base64 32>
Social__Meta__AppId                  = <meta app id>
Social__Meta__AppSecret              = <meta app secret>
Social__Meta__GraphVersion           = v26.0
Social__Google__ClientId             = <google client id>
Social__Google__ClientSecret         = <google client secret>
Social__LinkedIn__ClientId           = <linkedin client id>
Social__LinkedIn__ClientSecret       = <linkedin client secret>
Social__LinkedIn__PagesEnabled       = false
Social__TikTok__ClientKey            = <tiktok client key>
Social__TikTok__ClientSecret         = <tiktok client secret>
Social__X__ClientId                  = <x oauth 2.0 client id>
Social__X__ClientSecret              = <x oauth 2.0 client secret>
Social__Worker__Enabled              = true
```

**The encryption key is not optional and cannot be regenerated later.** Lose it
and every pharmacy has to reconnect every account. Store a copy somewhere
durable — Key Vault, or your password manager.

If you deploy with `infra/azure/main.bicep`, pass these as the `social*`,
`meta*`, `google*`, `linkedIn*`, `tikTok*` and `x*` parameters instead of setting them in the portal: the
template's app settings are the complete list, so a redeploy removes anything
added by hand. `Public__WebBaseUrl` must be the web app's public address — the
API builds the OAuth redirect from it.

After changing `Social__Meta__GraphVersion`, publish once to a test Page and a
test Instagram account before relying on it.

Register Meta's **Deauthorize callback** and **Data deletion request URL**
(`https://<api-host>/api/social/meta/deauthorize` and `…/data-deletion`) and the
privacy policy URL — see `SOCIAL_SETUP.md` §2. Facebook Login apps do not pass
App Review without them.

Optionally, switch on **Require App Secret** in the Meta app's advanced
settings: every call now carries `appsecret_proof`, so a leaked Page token is
useless without the app secret.

Register these redirect URIs with each platform, exactly (no `www`, no
trailing slash):

```
https://marketing25.co.uk/social/callback/meta       (Meta → Facebook Login)
https://marketing25.co.uk/social/callback/google     (Google OAuth client)
https://marketing25.co.uk/social/callback/youtube    (Google OAuth client)
https://marketing25.co.uk/social/callback/linkedin   (LinkedIn app → Auth)
https://marketing25.co.uk/social/callback/tiktok     (TikTok app → Login Kit)
https://marketing25.co.uk/social/callback/x          (X app → User authentication settings)
```

### Stripe billing

`appsettings.json` has `Stripe:Enabled = true` but there is no secret key. That
combination is worse than disabled: checkout is offered, then fails against the
Stripe API rather than returning the clean "billing is not configured" message.

Either set the keys, or set `Stripe__Enabled = false` until you do.

```
Stripe__Enabled                   = true
Stripe__SecretKey                 = sk_live_...
Stripe__WebhookSecret             = whsec_...
Stripe__Prices__StarterMonthly    = price_...
Stripe__Prices__StarterYearly     = price_...
Stripe__Prices__ProMonthly        = price_...
Stripe__Prices__ProYearly         = price_...
Stripe__SuccessUrl                = https://sonar-marketing-auth.azurewebsites.net/billing/success
Stripe__CancelUrl                 = https://sonar-marketing-auth.azurewebsites.net/pricing
Stripe__PortalReturnUrl           = https://sonar-marketing-auth.azurewebsites.net/dashboard
```

---

## 2b. Build steps that are easy to skip

### `pnpm install` before building the web image

The root `package.json` now carries `pnpm.overrides` pinning three Radix
packages to a single version each. Without a fresh install the lockfile does not
carry them, two copies of the same module ship, and the app reaches a state
where a page renders perfectly and **cannot be clicked until it is reloaded** —
no error, no log line, no failed request. `scripts/check-radix-layers.mjs` fails
the build while duplicates are on disk, so the signal is there; it is the sort
of failure that gets waved past as "a dependency thing".

`apps/web/Dockerfile` runs `pnpm install --frozen-lockfile || pnpm install`, so
an image builds either way and the overrides still apply — but the fallback
silently hides lockfile drift, which is worth revisiting on its own.

### Rebuild the ai-service when Python changes

Obvious, and it was still got wrong twice — because the boot marker could not
show it. Unset `BUILD_ID` used to fall back to the mtime of `main.py` alone, so
a rebuild that added modules and rewrote the worker printed a five-day-old
marker. It now reads the newest mtime anywhere under `app/` plus the file count,
so the marker moves when the code does. Check it in the boot banner:

```
SONAR AI-SERVICE BOOT MARKER  build=local-20260922-043811-35f
```

A deployed build reports the git short SHA instead, which is also the image tag.

## 3. Worth setting

```
Messaging__Mailjet__FromAddress = <verified sender in Mailjet>
Messaging__Mailjet__FromName    = Sonar Marketing25
AiService__AnthropicModel       = claude-haiku-4-5-20251001
```

### Measure `Messaging__Mailjet__MaxRequestBytes` per environment

Leave it unset and you get 1,000,000 — deliberately conservative.

**Do not set it to Mailjet's documented 15 MB**, but do not treat the default as
a measured ceiling either — an earlier version of this page did, and was wrong.

Campaign sends were failing with `Broken pipe` partway through the upload. Large
requests failed and small ones did not, so a limit was inferred. Then a request
of **1,008,893 bytes failed where one of 1,008,621 had succeeded** minutes
earlier. The actual fault was a pooled connection being reused after the server
had closed it, now fixed by a 15-second `PooledConnectionIdleTimeout` and a
single retry. Size correlated only because a longer write is likelier to be the
one holding a dead socket.

So the budget is a sensible default — fewer bytes, less exposure — rather than a
number to tune precisely. If you do want a figure for a given environment:

```bash
./scripts/mailjet-probe.sh you@example.com
```

Roughly a minute, sends nothing (SandboxMode), and prints the size at which
requests stop being accepted. Set the value a comfortable margin below it.

Too low costs extra HTTP calls, a few seconds on a large campaign. Too high
costs a campaign that reports every recipient as failed and gives no usable
reason. Err low.

`AiService__AnthropicModel` is the fallback a super admin sees under **Settings
→ Video generation model** when no override is set. Without it the screen shows
"built-in default", which is correct but less informative.

Leave `Sonar__UseMock = true` until you have real Sonar credentials — the
default is already mock, so nothing to do.

---

## 4. Already correct

`Mongo__*`, `Jwt__*`, `AzureBlob__*`, `Auth__RefreshTokenDays`,
`Public__ApiBaseUrl`, `Cors__Origins__0`, `SuperAdmin__Email`,
`WEBSITES_PORT`, `WEBSITES_ENABLE_APP_SERVICE_STORAGE`, and the Docker registry
settings.

`Public__ApiBaseUrl` is worth a second look even though it is on that list. It
has to be an address the **public internet** can reach, not one this server can
reach: a campaign's poster and its video links are fetched from a patient's
side, and Gmail fetches images from Google's servers rather than from the
reader's browser. Set to a loopback, private or container address, the send
detects it and embeds the poster in every recipient's copy instead — the message
still arrives and still looks right, but the request size now grows with the
audience, which is what makes a large campaign time out mid-upload. It logs a
warning naming the address whenever it does this; a clean campaign log is the
check.

One thing to verify rather than change: the **JWT secret must be byte-identical
between the API and the ai-service**, or every forwarded call fails with a 401
that reads like a login bug. Both expose a fingerprint endpoint — compare them
after deploying.

---

## 5. The ai-service needs its own settings

These are separate app settings on the ai-service, not the API:

```
JWT_SECRET            = <same value as the API's Jwt__Secret>
JWT_ISSUER            = acme-api
JWT_AUDIENCE          = acme-clients
MONGO_CONNECTION_STRING = <same Atlas connection string>
MONGO_DATABASE        = acme
ANTHROPIC_API_KEY     = <key>
ANTHROPIC_MODEL       = claude-haiku-4-5-20251001
TEMPLATE_DESIGN_MODEL = claude-sonnet-5
PEXELS_API_KEY        = <key>
AZURE_BLOB_CONNECTION_STRING = <same as the API>
AZURE_BLOB_CONTAINER  = canvas-assets
TAVILY_API_KEY        = <key, for video research>
VIDEO_WORKER_ENABLED  = true
ELEVENLABS_API_KEY    = <key>          # required here — see below
```

**`ELEVENLABS_API_KEY` is not optional on a hosted deployment.** Without it the
pipeline narrates with Edge-TTS, which is an unofficial route into Microsoft's
consumer speech service and is **refused from datacentre address ranges** — so
every render dies at `Voicing… 45%` while the identical code works on a laptop.
There is deliberately no fallback in the other direction either: when the key is
set, a narration failure fails the render carrying ElevenLabs' own reason.

`pnpm smoke` checks this before it spends a render — a hosted deployment
reporting `edge` is a FAIL with the fix named.

Optional: `REPLICATE_API_TOKEN` for AI imagery (falls back to stock photography),
`ELEVENLABS_DEFAULT_VOICE_ID` to pin a voice (Rachel if unset).

---

## 6. In-pharmacy display screens

No new configuration keys. What this feature adds is a set of things a proxy or
CDN in front of the web app can quietly break, all of which look like a
different problem when they do.

**`ETag` and `If-None-Match` must survive the round trip.** A screen polls
`/api/display/content` every thirty seconds for years, and that is only
affordable because an unchanged playlist answers `304` with no body. Anything
that strips either header turns every poll into a full download of every poster.
It keeps working, so the only symptom is a bandwidth bill nobody can explain.

**`X-Display-Token` must survive too.** A request that arrives without it gets
`400`; a request with a bad one gets `401`. The device treats `401` as "this
credential is finished", erases it and returns to the pairing box — so a proxy
that strips the header and turned that into a `401` would unpair every screen
in the estate at once. The `400`/`401` split exists precisely for this, but only
the API side can enforce it.

**HTTPS is not optional here.** The direct-link credential travels in the URL
fragment and the paired credential lives in the device's local storage. Both are
bearer tokens for that screen.

**Do not add a shared cache in front of `/api/display/content`.** The BFF route
sets `Cache-Control: private, no-store` for this reason: one screen's playlist
served to the next screen that asks would be a cross-tenant leak created by
infrastructure rather than by code.

**Before launch, decide about downgrades.** `MaxDisplayScreens` is checked when
a screen is registered and not afterwards, so an account that drops to Free
keeps its screens running. That was chosen over blanking a pharmacy's shop
window on a billing event, but it is a commercial decision that has not been
confirmed.

---

## Order to do it in

1. `ASPNETCORE_ENVIRONMENT = Production`, redeploy, then delete the seeded
   `@example.test` accounts and change the super admin password
2. Rotate the Mailjet keys and move them to env vars
3. `AiService__BaseUrl`, plus the ai-service settings — restores AI generation
4. Stripe, or turn it off explicitly
5. Social and Ads credentials (Meta, Google, LinkedIn, TikTok, X), and register
   the five redirect URIs
6. Compare the JWT fingerprints across both services
7. `ELEVENLABS_API_KEY` on the ai-service, then **restart it** — settings are
   read once per process, so a key added to a running container does nothing
8. **Commit the working tree**, then deploy. `scripts/deploy.sh` builds from the
   files on disk and tags with `git rev-parse --short HEAD`: deploying
   uncommitted work produces an image whose build id names a commit that does
   not contain it. It now appends `-dirty-<timestamp>` and warns, but the fix is
   to commit
9. `pnpm install`, so the Radix `pnpm.overrides` reach the lockfile, then check
   `node scripts/check-radix-layers.mjs` passes. Skipping this ships a page that
   renders and cannot be clicked — see section 2b
10. `pnpm smoke` against the deployment. Exit 0 before you tell anyone the
   release is out. Read the warnings too: a video much longer than 15 seconds
   and a picture the render did not use are both reported there, and neither
   blocks the exit code


## Removing the seeded demo accounts

`DevUserSeeder` creates 12 demo pharmacies and 2 demo admins, all at
`@example.test`, all sharing the password `TestPass123!`. It is guarded twice —
it returns unless the environment is Development, and again if
`DevData:SeedUsers` is false. So their presence on production means one thing:
**the deployment is running with `ASPNETCORE_ENVIRONMENT=Development`.**

### Order of operations

Config first. Removing the accounts while the environment is still Development
fixes today and not tomorrow.

1. `DevData__SeedUsers=false` — stops the seeder outright, smallest possible
   change, do it now
2. `./scripts/demo-accounts.sh` — report what is actually there
3. `./scripts/demo-accounts.sh --apply` — deactivate the empty ones
4. `ASPNETCORE_ENVIRONMENT=Production` — the real fix, and it also closes
   Swagger and `/api/diagnostics/*`

```
API_URL=https://your-api \
SUPERADMIN_EMAIL=... SUPERADMIN_PASSWORD=... \
  ./scripts/demo-accounts.sh
```

Read-only until `--apply`. There is no default `API_URL`: a destructive script
that fills in its own target is one typo from the wrong environment.

### Deactivate, not delete

- There is no delete endpoint for a user, deliberately — a pharmacy row is
  referenced by patients, posters and campaigns.
- The seeder is idempotent **by email**, so a deactivated row is left alone on
  restart. A hard delete would simply be recreated on the next boot for as long
  as the environment stays Development.
- Going through the API puts every change in the audit log against the
  SuperAdmin who made it. A `mongosh` session leaves no trace.

### What it refuses to touch

Two of these accounts may not be test data any more — they share a published
password, so anyone could have signed in and done real work.

- an account **holding records** (patients, posters, campaigns, videos) is
  reported and skipped: deactivating it would take a working pharmacy offline
- a demo admin **still managing real pharmacies** is skipped, because
  deactivating it leaves those pharmacies without an account manager

Both need a human decision — reassign, export, or confirm the records are junk —
and then `--force`.

The email suffix is checked again immediately before each call, so a bug in the
listing cannot widen the blast radius. Verified against a stub API: a real
pharmacy in the same list is never touched, and `evil@example.test.attacker.com`
is refused (suffix match, not substring).

## Regression gate

- [ ] `pnpm test` is green.
- [ ] `pnpm test:prove` reports **0 missed, 0 stale anchors**. A missed bug means its test passes with the bug back in place.

# Social publishing & Google Ads — setup

*Last updated 3 November 2026. Covers Facebook, Instagram, YouTube, LinkedIn, TikTok, Google Ads and recurring posts.*

What has to exist outside this repo before any of it works, and in what order.

---

## 0. Where the credentials go

**Not in `appsettings.Development.json`.** That file is committed to git. An app
secret in git is a rotation job, not a typo.

Two safe places, depending on how you run the API:

### Running `dotnet run` directly

Use user secrets — stored outside the repo, already wired up
(`UserSecretsId` is in the csproj):

```bash
cd apps/api/src/Acme.WebApi

dotnet user-secrets set "Social:Meta:AppId"        "<meta app id>"
dotnet user-secrets set "Social:Meta:AppSecret"    "<meta app secret>"
dotnet user-secrets set "Social:Google:ClientId"     "<google client id>"
dotnet user-secrets set "Social:Google:ClientSecret" "<google client secret>"
dotnet user-secrets set "Social:LinkedIn:ClientId"     "<linkedin client id>"
dotnet user-secrets set "Social:LinkedIn:ClientSecret" "<linkedin client secret>"

# Optional, only if campaigns run under a manager account. Digits, no dashes.
dotnet user-secrets set "Social:Google:AdsLoginCustomerId" "1234567890"

dotnet user-secrets list          # check
```

### Running under docker-compose

Repo-root `.env`, which is gitignored. Keys are in `.env.example`:

```
SOCIAL_ENCRYPTION_KEY=
META_APP_ID=
META_APP_SECRET=
GOOGLE_CLIENT_ID=
GOOGLE_CLIENT_SECRET=
GOOGLE_ADS_LOGIN_CUSTOMER_ID=
LINKEDIN_CLIENT_ID=
LINKEDIN_CLIENT_SECRET=
LINKEDIN_PAGES_ENABLED=false
```

`docker-compose.yml` maps these onto `Social__*` for the API container.
Restart with `docker compose up -d --force-recreate api` after editing.

### Production

Environment variables, same `__` form:
`Social__Meta__AppSecret`, `Social__Google__ClientSecret`, and so on.

### Redirect URIs — must match exactly

Register these on the platform side. A mismatch is the most common failure and
both platforms report it precisely, which is why the callback page shows their
message verbatim.

| Platform | Redirect URI |
|---|---|
| Meta → Facebook Login → Valid OAuth Redirect URIs | `http://localhost:9090/social/callback/meta` |
| Google Cloud → Credentials → Authorised redirect URIs | `http://localhost:9090/social/callback/google` |
| Google Cloud → Credentials → Authorised redirect URIs (YouTube) | `http://localhost:9090/social/callback/youtube` |
| LinkedIn → your app → Auth → Authorized redirect URLs | `http://localhost:9090/social/callback/linkedin` |
| TikTok → your app → Login Kit → Redirect URI | `https://<your https address>/social/callback/tiktok` (TikTok accepts https only) |
| X → your app → User authentication settings → Callback URI | `http://localhost:9090/social/callback/x` |

Swap `http://localhost:9090` for your real origin in production. The path is
generated from the provider name in lowercase: `/social/callback/meta`,
`/social/callback/google`, `/social/callback/youtube`,
`/social/callback/linkedin`, `/social/callback/tiktok` or `/social/callback/x`. Production uses `https://marketing25.co.uk/…`
(no `www`, no trailing slash).

**The API decides the redirect URI, not the browser.** It is built from
`Public__WebBaseUrl` (falling back to `Cors__Origins__0`, then
`http://localhost:3000`). If the app is opened on a different address, Connect
is refused with a message naming the address to use — register exactly that
one with each platform.

### OAuth sessions

Start records the flow (who, which provider, which redirect) in the
`oauth_sessions` collection, keyed by a hash of `state`. The callback is
accepted only for a state issued to the same pharmacy for the same provider,
once. The accounts it finds are held there encrypted with the credential key
for ten minutes, until the pharmacy chooses, and deleted on use; a TTL index
removes abandoned flows. Several API instances need no sticky sessions.

---

## 1. Credential encryption (required, do this first)

The API refuses to run the publish worker without it, and no account can be
connected.

```bash
openssl rand -base64 32
```

```jsonc
"Social": {
  "Encryption": {
    "ActiveVersion": 1,
    "Keys": [ { "Version": 1, "Key": "<the base64 string>" } ]
  }
}
```

In production set it as an environment variable instead:

```
Social__Encryption__Keys__0__Version=1
Social__Encryption__Keys__0__Key=<base64>
Social__Encryption__ActiveVersion=1
```

`appsettings.Development.json` already has a generated key. **It is for local
development only** — it is in git, so treat anything encrypted with it as
public.

### Rotating

Add the new key, leave the old one in place, point `ActiveVersion` at the new
one. New writes use it; existing connections stay readable through their own
recorded version. Remove the old key only once nothing references it — deleting
a key that records still use does not fail at startup, it fails when a pharmacy's
post tries to publish.

---

## 2. Meta app — Facebook Pages & Instagram

1. Create an app at developers.facebook.com, type **Business**.
2. Add the **Facebook Login** product. Under its settings add the redirect URI:
   `https://your-domain/social/callback/meta`
3. Request these permissions in App Review:
   - `pages_show_list`, `pages_read_engagement`, `pages_manage_posts`
   - `instagram_basic`, `instagram_content_publish`
4. Register the two platform callbacks, on the **API's** public address (Meta
   calls them directly, not through the web app). App Review requires them:
   - Facebook Login → Settings → **Deauthorize callback URL**:
     `https://<api-host>/api/social/meta/deauthorize`
   - App settings → Basic → **Data deletion request URL**:
     `https://<api-host>/api/social/meta/data-deletion`
   - App settings → Basic → **Privacy policy URL**: `https://<web-host>/privacy`

   Both verify Meta's `signed_request` with the app secret; anything else gets
   a 400. Deauthorize revokes the connections that person granted; data
   deletion removes them outright and answers with a link to
   `/privacy/data-deletion?code=…`. Connections made before 6 October 2026 do
   not record who granted them, so these callbacks cannot find them; they stop
   at their next publish with Meta's "token invalid", and reconnecting records
   the grantor.
5. Optional but recommended: **Require App Secret** (App settings → Advanced).
   Every call already sends `appsecret_proof`.
6. Before App Review, test with a Page owned through **Business Manager**. If
   it does not appear in the account picker, the app also needs
   `business_management` — add it to `MetaOAuthProvider.Scopes` and to the
   review request.
7. Config:

```jsonc
"Social": { "Meta": { "AppId": "...", "AppSecret": "...", "GraphVersion": "v26.0" } }
```

**Before review is granted**, only users with a role on the app can connect.
Add testers under App Roles to try it.

**Instagram requires** a Business or Creator account *linked to a Facebook
Page*. A personal Instagram account will not appear in the connect dialog, and
this is the single most common "it doesn't work" report.

---

## 3. Google — Business Profile & Ads

Shared OAuth client:

1. Google Cloud Console → APIs & Services → Credentials → OAuth client ID (Web).
2. Redirect URI: `https://your-domain/social/callback/google`
3. Enable the **Google Ads API** and, separately, apply for **Business Profile
   API** access — that one is a manual allowlist and is usually the slowest step.

```jsonc
"Social": {
  "Google": {
    "ClientId": "...",
    "ClientSecret": "...",
    "AdsApiVersion": "v25",       // Google supports each version about 12 months
    "AdsValidateOnly": false      // true: Google checks every creation, creates nothing
  }
}
```

No manager id is configured. When the signed-in Google user reaches accounts
through a manager (MCC) — the usual test setup, and how agencies work — the
connect step lists the client accounts under it and stores the manager on each
connection, so every call sends the right `login-customer-id`.

### Google Ads access: the Cloud project, not a developer token

**Google sunset developer tokens on 10 September 2026.** Google Ads API access
now belongs to the **Google Cloud project** that owns the OAuth client — the
same project as YouTube. A token sent in a call is ignored, and Google has said
a future API version will reject calls that carry one, so this server sends
`developer-token` only if `Social__Google__AdsDeveloperToken` is set (legacy)
and warns at startup when it is. Leave it empty.

1. **Enable the Google Ads API** in that Cloud project (APIs & Services →
   Library → Google Ads API → Enable).
2. Open the project's **Google Ads API → Overview** page in Cloud Console:
   it shows the project's access level and is where access is requested.
   The old API Center page in manager accounts is being retired.
3. A new project has **Test Account Access** — calls to real ad accounts fail
   with `CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION` (v25), which pharmacies
   see as "only works with test accounts so far". It is not treated as a
   pharmacy sign-in problem: the connection is not marked Reconnect.
4. For real pharmacies request **Basic access** (15,000 operations a day)
   from that page. **Brand verification** of the Cloud project (OAuth consent
   screen → Branding → verify) is now mandatory for new Basic requests, and
   once it is done Google reviews Basic requests automatically, in minutes.
   (Explorer access, 2,880 operations a day on production accounts, may be
   granted along the way.)
5. Google's change notices for the API go to the project's **Owner/Editor**
   emails (IAM). Make sure someone reads them.

### "Google Ads is not available on this server yet"

The API shows this when it cannot see `Social__Google__ClientId` and
`Social__Google__ClientSecret` (with the encryption key set), or
`Social__Google__AdsEnabled` is false. No developer token is needed. To find
which:

- **The startup log** says it in one line: `Google Ads is NOT available:
  missing …` (or `Google Ads is available`).
- **`GET /api/diagnostics/config`** (anyone in Development, SuperAdmin
  elsewhere) has a `social` block: each setting `set`/`missing`,
  `googleAdsMissing`, and `settingsUnderWrongName` for values found under a
  near-miss key. No values are shown.

The usual causes:

1. **A slightly different name** (`Social__Google__ClientId`, double
   underscores) on the **API** app, not the web app.
2. **docker-compose: the container was restarted, not recreated.**
   `docker compose restart` keeps the old environment. Run
   `docker compose up -d --force-recreate api` after changing `.env`.
3. **Azure: a deployment replaced the portal settings.** `main.bicep` sets the
   API's whole app-settings list, so pass `googleClientId` and
   `googleClientSecret` as parameters, not just in the portal.

### What a campaign is, at Google

Created in one all-or-nothing request, **always paused**, declared as
containing no EU political advertising (Google requires the declaration).

| | Search | Performance Max |
|---|---|---|
| Bidding | Maximise clicks — works without conversion tracking | Maximise conversions — the only kind Google allows; needs a conversion action in the account |
| Budget | Daily (up to 1,000), or a total over the campaign dates (up to 20,000; needs an end date) — always in the **account's own currency**, which the form shows and the server checks | Same |
| Areas | Town / postcode-district names, looked up in the UK with Google's own location search; people *in* the area only | Same names |
| Copy | 3–15 headlines, 2–5 descriptions, 1–20 search terms (phrase match) | 3–15 headlines, 2–5 descriptions, a long headline, the business name |
| Images | None | The poster fitted into 1.91:1 and 1:1 (never cropped), and the Brand kit logo on white |
| Not yet | — | Video (needs the video on YouTube first). Demand Gen is not offered. |

### Test-account checklist — run before real money

Test accounts never serve ads and cannot spend, so all of this is safe.

1. Set `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` and
   **`GOOGLE_ADS_VALIDATE_ONLY=true`** (no developer token), and enable the
   Google Ads API in the client's Cloud project. Register
   `http://localhost:9090/social/callback/google` on the OAuth client. Rebuild
   the API.
2. Social & Ads → Connected accounts → **Google Ads** → sign in with the Google
   user that has access to your **test manager** account. The picker should
   list the test client accounts under it. Connect one.
3. Google Ads tab → **New campaign**, Search, with an area like `Leeds` and a
   search term. Expect: *"Checked by Google and valid — nothing was created"*
   (no campaign is listed afterwards — a check leaves no record). Test
   accounts are often in US dollars; the budget label shows the account's
   currency.
   Any other answer is Google's own message about what to change.
4. Add a logo in Brand kit, then try Performance Max the same way (its
   headline and description text assets are created for real even here —
   Google needs them first; unlinked, they show nowhere). If Google
   says there is no conversion action, create one in the test account
   (Goals → Conversions) — Performance Max cannot be created without.
5. Set `GOOGLE_ADS_VALIDATE_ONLY=false`, rebuild, create a Search campaign for
   real. In the Google Ads UI for the test account check: status Paused,
   budget, the area (Locations), "presence" targeting, the ad's headlines and
   descriptions, phrase-match keywords.
6. **Start** it from the campaign card (confirm the budget), then **Pause** —
   both should show in the Google Ads UI within a minute. **Refresh** reads
   metrics; on a test account they stay at zero.
7. Disconnect the Google account: our copy of the token is deleted and, if no
   other connection uses the same sign-in, the grant is revoked at Google
   (myaccount.google.com/permissions no longer lists the app).

Before production, request **Basic access** for the Cloud project (above —
brand verification first), and keep `GOOGLE_ADS_VALIDATE_ONLY=true` on any
staging server.

---

## 3b. YouTube

Videos go to a pharmacy's YouTube channel as Shorts. Posters are never offered
for YouTube (it takes video only). It uses the **same Google OAuth client** as
Google Ads (`Social__Google__ClientId` / `ClientSecret`), with its own consent
asking only for YouTube — a pharmacy that connects Ads is not asked for its
channel, and the reverse.

**One-time setup in Google Cloud Console** (the project that owns the OAuth client):

1. **APIs & Services → Library → YouTube Data API v3 → Enable.**
2. **Credentials → your OAuth client → Authorised redirect URIs:** add
   `https://marketing25.co.uk/social/callback/youtube` (and
   `http://localhost:3000/social/callback/youtube` for local testing). It must
   match `Public__WebBaseUrl` exactly, as for Meta.
3. **OAuth consent screen → Data access / Scopes:** add
   `https://www.googleapis.com/auth/youtube.upload` and
   `https://www.googleapis.com/auth/youtube.readonly`.
4. **While the app is in Testing:** add your own Google account (the one that
   owns the test channel) under **Test users**. Only test users can connect
   until the app is verified.

Settings (all optional):

| Setting | Default | |
|---|---|---|
| `Social__Google__YouTubeEnabled` | `true` | `false` hides YouTube entirely. |
| `Social__Google__YouTubePrivacy` | `public` | `public`, `unlisted` or `private`. Use `unlisted` or `private` while testing. |
| `Social__Google__YouTubeCategoryId` | `22` | People & Blogs. `26` is Howto & Style. |

**How a post works.** The worker exchanges the stored refresh token for an
access token, downloads the finished video from our storage, opens a
resumable upload with the title, description and privacy, then sends the file.
The caption's first line becomes the title (100 characters max, YouTube
requires one); the whole caption becomes the description, with `#Shorts`
added. Not made for kids is declared on every upload.

| Situation | Result |
|---|---|
| Sign-in refused (`invalid_grant`, 401) | Failed; the connection is marked **Reconnect**. |
| Network or server fault before the file is sent | Retried automatically — nothing exists on YouTube yet. |
| Daily quota used up (`quotaExceeded`) | Failed with "retry tomorrow". Not retried every few minutes: it resets at midnight US Pacific. |
| Timeout, dropped connection or 5xx **while the file is sent** | **Uncertain** — it may be on the channel. Check before pressing Retry. |
| A poster sent to YouTube | Refused when composing; skipped if it ever reaches the worker. |

**Before real pharmacies can use it** — Google's process, not code:

- **Uploads are forced Private** for projects Google has not audited for the
  YouTube API, whatever privacy is asked for. The upload still succeeds; the
  API log notes "YouTube made video … private instead of public". Apply for the
  YouTube API audit (YouTube API Services compliance audit) from the Cloud
  Console quotas page to lift it.
- **App verification**: `youtube.upload` needs Google's OAuth app
  verification. Until then only test users can connect, behind an
  "unverified app" warning.
- **Quota** is per project, shared by every pharmacy. Check the current cost of
  `videos.insert` and your daily allowance on the YouTube Data API v3 quotas
  page, and request more if needed.

**Disconnecting** deletes our copy of the credential but does not revoke it at
Google: Google revokes a whole sign-in at once, which could also cut off the
same person's Google Ads connection. For the same reason, disconnecting
**Google Ads** leaves the Google grant in place while the pharmacy has a
YouTube channel connected. To withdraw access fully, remove the app at
myaccount.google.com/permissions.

**Testing checklist** (your own channel, app in Testing):

1. Set `Social__Google__YouTubePrivacy=unlisted` (or `private`).
2. Social & Ads → Connected accounts → **YouTube → Connect**, sign in as a test user, pick the channel.
3. On a Ready video: **Share to social** → tick the YouTube channel → caption
   with a short first line → **Publish now**.
4. Within a minute the post shows **Published** with a `youtu.be/…` link. Open it.
5. Try a recurring series with the YouTube channel on a video.
6. Disconnect and reconnect the channel: the same connection comes back.

## 3c. LinkedIn

Posters and videos go to the signed-in person's **LinkedIn profile** straight
away, and — once LinkedIn approves the app for the Community Management API —
to the **company Pages** they are an admin or content admin of. One consent
covers the profile and its Pages; the pharmacy ticks which to connect.

**One-time setup at developer.linkedin.com → My apps → your app:**

1. **Products:** add **Sign In with LinkedIn using OpenID Connect** and **Share
   on LinkedIn** (both self-serve, approved at once). These give `openid
   profile w_member_social` — profile posting.
2. **Auth → Authorized redirect URLs:** add
   `https://marketing25.co.uk/social/callback/linkedin` (and
   `http://localhost:3000/social/callback/linkedin` for local testing). It must
   match `Public__WebBaseUrl` exactly.
3. **Settings → verify the app** with your company Page (a Page super admin
   approves the link).
4. **Auth → Client ID / Primary Client Secret** → `Social__LinkedIn__ClientId` /
   `Social__LinkedIn__ClientSecret` (App Service settings in production; Bicep
   parameters `linkedInClientId` / `linkedInClientSecret`, or a redeploy wipes
   them). Never commit them.
5. **For Pages:** request **Community Management API** (Products tab).
   LinkedIn reviews it — legal company name, address, website, privacy
   policy, a use case, and later a screen recording for the Standard tier. If
   it insists on an app of its own, create one, request it there, and switch
   the credentials to that app with `Social__LinkedIn__ProfilesEnabled=false`.
   When approved, set `Social__LinkedIn__PagesEnabled=true`.

Settings:

| Setting | Default | |
|---|---|---|
| `Social__LinkedIn__ClientId` / `ClientSecret` | — | Required. |
| `Social__LinkedIn__ProfilesEnabled` | `true` | Offer the person's own profile (asks for `openid profile w_member_social`). |
| `Social__LinkedIn__PagesEnabled` | `false` | Offer company Pages (adds `w_organization_social rw_organization_admin`). **Only after approval** — before it, LinkedIn refuses the whole consent with `unauthorized_scope_error`, and the smoke test FAILs saying so. |
| `Social__LinkedIn__ApiVersion` | `202610` | The `LinkedIn-Version` header. Each version is served about a year; bump deliberately and post once to a test profile. |

**The 60-day sign-in.** LinkedIn access tokens last 60 days and most apps get
no refresh token, so the connection stores when it expires. From two weeks
before, Social & Ads shows "The sign-in expires on …" with a **Reconnect**
button; after it, the connection shows Reconnect and posts to it fail with
"needs to be reconnected" (the recurring-post email says so too). If LinkedIn
grants the app refresh tokens, the refresh token (a year) is stored instead and
used to mint an access token on every call — nothing to configure.

**How a post works.** The worker uploads the poster (`/rest/images`) or the
video in the parts LinkedIn asks for (`/rest/videos`, then finalize), then
creates the post (`/rest/posts`, public, main feed) as `urn:li:person:…` or
`urn:li:organization:…`. The caption is escaped for LinkedIn's "little" text
format — `( ) [ ] { } @ < > * _ ~ | \` would otherwise be read as markup — and
`#hashtags` are kept. 3,000 characters max.

| Situation | Result |
|---|---|
| Sign-in refused (401) or expired refresh token | Failed; the connection is marked **Reconnect**. |
| Permission refused (403) — e.g. no longer a Page admin | Failed; marked **Reconnect**, saying why. |
| Network or server fault while uploading | Retried automatically — an asset is not a post. |
| Video still processing when the post is created | Asked again for about 80 seconds, then retried later. Nothing is posted meanwhile. |
| Rate limited (429) | Retried automatically. |
| Timeout, dropped connection or 5xx **creating the post** | **Uncertain** — it may be on the feed. Check before pressing Retry. |
| Anything else LinkedIn refuses (422 etc.) | Failed with LinkedIn's own message. |

**Daily check:** a profile is checked by reading `/v2/userinfo`; a Page by
confirming the person still holds an admin or content-admin role on it. A Page
role removed shows as Reconnect the next day.

**Disconnecting** deletes our copy. The access token is revoked at LinkedIn
only when no other connection (profile or Page, in any pharmacy) still uses
the same sign-in, and none of the same person's LinkedIn connections remain.

**Reconnecting** renews every account ticked on the callback page; all are
ticked by default, so a profile and its Pages (one shared 60-day sign-in) are
renewed together. To withdraw fully: LinkedIn → Settings → Data privacy →
Permitted services.

**Testing checklist** (profile posting needs no approval):

1. Set the two credentials and redeploy; readiness `linkedin` should say
   "app credentials accepted".
2. Social & Ads → **LinkedIn → Connect**, sign in, tick your profile.
3. On a poster: **Share to social** → tick LinkedIn → **Publish now**. It shows
   **Published** with a `linkedin.com/feed/update/…` link. LinkedIn has no
   private post: it is public — delete it afterwards.
4. Same with a Ready video (takes longer: LinkedIn processes it first).
5. A recurring series to LinkedIn.
6. `SMOKE_LINKEDIN_PUBLISH=1 pnpm smoke` (or `=video`) posts the smoke poster for real.

## 3d. TikTok

Videos only, through the Content Posting API's **Direct Post**. Each post
carries the audience and settings the pharmacy chose for it on the share
screen — TikTok's Content Sharing Guidelines require that, and it is why
TikTok cannot be part of a recurring post.

**One-time setup at developers.tiktok.com → your app:**

1. **Products:** **Login Kit** and **Content Posting API** with **Direct Post**
   switched on.
2. **Scopes:** `user.info.basic` and `video.publish`.
3. **Login Kit → Redirect URI:** `https://marketing25.co.uk/social/callback/tiktok`.
   TikTok accepts https only — test locally through an https tunnel, or on
   the deployed site.
4. **Sandbox:** add the TikTok account you test with as a **target user**.
   Only target users can sign in while the app is in sandbox.
5. **Set that TikTok account to private** in the TikTok app. Until TikTok
   audits the app, every post is private ("Only me") and posting to a public
   account is refused (`unaudited_client_can_only_post_to_private_accounts`).
   Unaudited apps can have at most 5 accounts posting a day.
6. **Credentials:** `Social__TikTok__ClientKey` / `Social__TikTok__ClientSecret`
   (Bicep: `tikTokClientKey`, `tikTokClientSecret`; compose: `TIKTOK_CLIENT_KEY`,
   `TIKTOK_CLIENT_SECRET`). `Social__TikTok__Enabled=false` hides TikTok.

**The share screen** (built to TikTok's guidelines, which its audit checks):
the account's nickname; **Who can see this post** from the options TikTok
returns for that account, with nothing pre-selected; **Comment / Duet /
Stitch** off by default and greyed when the creator switched them off in
TikTok; **Disclose commercial content** with **Your brand** ("Promotional
content") and **Branded content** ("Paid partnership", which cannot be
private); and the consent line — Music Usage Confirmation, plus the Branded
Content Policy for branded content. The settings are read live from TikTok
every time (`GET /api/social/connections/{id}/tiktok-creator`). One TikTok
account per post.

**The credential.** TikTok access tokens last 24 hours, refresh tokens a year,
and a refresh may return a *new* refresh token, after which only the new one
works. The stored credential holds both tokens and the access token's expiry;
whoever refreshes it — a publish, the daily check, or the share screen — saves
the rotated credential straight away (`RenewCredentialAsync`, a targeted
update that never touches a revoked connection). The access token is reused
while valid, so refreshes are about daily.

**How a post works.** Creator info is read again and the chosen audience must
still be offered; then `video/init` (FILE_UPLOAD), the file in TikTok's chunks
(one chunk up to 64 MB, otherwise 10 MB chunks with the remainder in the last),
then the post's status is followed until `PUBLISH_COMPLETE` or `FAILED`.

| Situation | Result |
|---|---|
| Refresh refused, `access_token_invalid`, `scope_not_authorized` | Failed; marked **Reconnect**. |
| Audience no longer offered for the account | Failed before anything is sent: choose again. |
| `spam_risk_too_many_posts`, `reached_active_user_cap`, banned, unaudited + public account | Failed with the reason; not retried. |
| Rate limit, network or server fault before the last chunk | Retried automatically — nothing is posted until the upload completes. |
| Timeout or 5xx on the **last chunk**, or still processing after about 2½ minutes | **Uncertain** — TikTok may still publish it. Check before Retry. |
| `FAILED` from TikTok | Failed with TikTok's `fail_reason`. |

Private posts have no public post id, so they are recorded under their
publish id with no link.

**Disconnecting** revokes the sign-in at TikTok (TikTok's revoke ends the
person's whole authorisation of the app) — unless the same TikTok user is
still connected in another pharmacy.

**Before real pharmacies can use it:** apply for the **Content Posting API
audit** with a demo video of the share screen and a post. Until then: posts
private, private accounts only, 5 posting accounts a day.

**Testing checklist (sandbox):**

1. Credentials set; readiness `tiktok` says "app credentials accepted".
2. Social & Ads → **TikTok → Connect**, sign in as the target user.
3. On a Ready video, **Share to social** → tick TikTok. The TikTok box shows
   your nickname and the audiences; choose **Only me**, leave the switches.
4. **Publish now**. After processing it shows **Published** (no link for a
   private post); the video is in the TikTok app under your private videos.
5. Try **Branded content** with **Only me**: the screen refuses it.
6. `SMOKE_TIKTOK_PUBLISH=1 pnpm smoke` posts the smoke video as Only me.

## 3e. X (formerly Twitter)

Posters and videos, to the X account the person signs in with. Not in
recurring posts: X refuses a post whose text repeats one already on the
account, and a series repeats its caption every time.

**Costs money per post.** Since February 2026 the X API is pay-per-use for
new developers: there is no free tier and no monthly plan to buy. Each post
and each media upload is charged against credits bought in the X Developer
Console. Out of credits, X answers **402** and the post fails with
*"Marketing25's X API credits are used up…"* (not retried, and the
connection is **not** marked for reconnecting). Keep credits topped up, and
watch usage in the console — the daily health check deliberately makes no
billed calls.

**One-time setup at console.x.com:**

1. Create an app. **User authentication settings → Set up**:
   - **App permissions:** *Read and write*.
   - **Type of App:** *Web App, Automated App or Bot* (a confidential client
     — it gets a client secret).
   - **Callback URI:** `https://marketing25.co.uk/social/callback/x` (and the
     localhost one for development).
   - **Website URL:** `https://marketing25.co.uk`; terms and privacy links.
2. **Keys and tokens → OAuth 2.0 Client ID and Client Secret.** Not the
   *API Key / Secret* (that is OAuth 1.0a, unused here).
3. **Credentials:** `Social__X__ClientId` / `Social__X__ClientSecret` (Bicep:
   `xClientId`, `xClientSecret`; compose: `X_CLIENT_ID`, `X_CLIENT_SECRET`).
   `Social__X__Enabled=false` hides X.
4. **Buy credits** in the console's billing page.
5. Upload the Marketing25 mark (`apps/web/public/brand/marketing25-mark-512.png`)
   as the app icon — it is what people see on X's consent page.

**Sign-in.** OAuth 2.0 authorization code with PKCE (S256). Scopes:
`tweet.read tweet.write users.read media.write offline.access` — X requires
the two read scopes alongside posting, `media.write` is for the picture or
video, and `offline.access` gives the refresh token scheduled posts need. The
PKCE verifier is derived from the client secret and the flow's state (HMAC),
so nothing extra is stored between consent and callback. One account per
sign-in.

**The credential.** Access tokens last 2 hours, and **every** refresh returns
a new refresh token (the old one stops working). Stored like TikTok's — both
tokens and the expiry in one encrypted blob — and whoever refreshes (a
publish or the daily check) saves the rotated credential at once
(`RenewCredentialAsync`). The daily check is just that refresh: X accepting
it proves the grant is alive, at no credit cost.

**The 280 rule.** X allows 280 per post, counted its own way: most letters
1, CJK and emoji 2, **every link 23** (even `pharmacy.co.uk`). The share
dialog shows an *X: n / 280* counter when X is ticked and blocks Publish over
it; the API refuses it too (`XText`, mirrored in `apps/web/src/lib/x-text.ts`). Premium accounts can post more,
but nothing tells us which accounts are Premium.

**How a post works.** Our file is fetched (for a poster, the same 1080×1350
JPEG Instagram gets — X refuses images over 5 MB), uploaded with
`POST /2/media/upload/initialize` → `/{id}/append` (4 MB segments) →
`/{id}/finalize`, a video followed with `STATUS` until X has processed it,
then `POST /2/tweets` with the text and the media id. The link is
`https://x.com/i/web/status/{id}`.

| Situation | Result |
|---|---|
| Refresh refused, 401, upload 403 (an old grant without `media.write`) | Failed; marked **Reconnect**. |
| 402 / out of credits | Failed with the credits message; not retried, connection untouched. |
| 403 `client-not-enrolled` (the app is not in a Project with API access) | Failed with an administrator message; connection untouched. |
| Refresh refused because a publish or the daily check had just rotated the token | Retried (publish) or left for tomorrow (daily check) — not marked Reconnect. |
| Duplicate text (403 "duplicate content") | Failed: change the caption. |
| Rate limit, network or server fault during the upload, or still processing after 2½ minutes | Retried automatically — nothing is posted before the last step. |
| Timeout, dropped connection or 5xx on `POST /2/tweets`, or success with no id | **Uncertain** — X may have posted it. Check before Retry. |
| Video processing `failed` | Failed with X's reason. |

**Worth knowing:**

- X's sign-in code lasts about 30 seconds. "Value passed for the
  authorization code was invalid" after a slow callback means: connect again.
- Our videos are 1080×1920 H.264/AAC. X's docs still list 1280×1024 as the
  largest upload for accounts without Premium, though it accepts 1080p in
  practice and plays it back at 720p. If a video fails with "X could not
  process the video", that is the first thing to check — post one with
  `SMOKE_X_PUBLISH=video` after setting X up.

**Disconnecting** revokes the refresh token at X, which ends the grant —
unless the same X user is still connected in another pharmacy.

**Testing checklist:**

1. Credentials set; readiness `x` says "app credentials accepted" (it
   redeems a fake code with the client id and secret — free).
2. Social & Ads → **X → Connect**, sign in, **Authorize app**. The account
   shows as *Name (@handle)*.
3. **Share to social** on a poster → tick X → the *X: n / 280* line appears.
   **Publish now** → Published with an `x.com/i/web/status/…` link.
4. A caption over 280 (or with several links) → the counter goes red and
   Publish is disabled.
5. `SMOKE_X_PUBLISH=1 pnpm smoke` posts the smoke poster (`=video` for the
   video). Public, and uses credits — delete it afterwards.

## 4. Worker

```jsonc
"Social": { "Worker": { "Enabled": true, "PollSeconds": 15, "LeaseSeconds": 300 } }
```

`LeaseSeconds` must comfortably exceed the slowest single account — an
Instagram video is a create-poll-publish sequence that can take two minutes.
The worker renews the lease before each account, and every write it makes is
conditional on still holding it.

Running several API instances is fine and expected — the claim is a single
atomic `findAndModify`, so exactly one wins each post.

### Daily health check

`Social:Worker:HealthChecks` (default `true`, and off whenever the worker is
off). Once an hour each instance claims connections not checked in 24 hours —
atomically, so instances share the work — and reads each account's own id with
its stored token. Nothing is posted. Facebook, Instagram, YouTube, LinkedIn and TikTok
(profile: `/v2/userinfo`; Page: the person still holds an admin role) are
checked. A rejected token (Meta code 190, Google `invalid_grant`, LinkedIn 401
or a lost Page role) marks the connection **Reconnect**; an outage or rate
limit changes nothing and it is looked at again tomorrow. Results are written
with a targeted update that can never revive a connection revoked meanwhile.

### What happens when something goes wrong

| Situation | Result |
|---|---|
| Rate limit, network failure before sending, Instagram still ingesting | Retried automatically after 2, then 10 minutes; three attempts in all. The post shows "Trying again at …". |
| Content refused, account disconnected, token revoked | Failed. Token problems mark the connection "needs reconnecting". |
| Timeout, server error or missing id **on the call that publishes** | **Uncertain** — it may be live. Never retried automatically; the pharmacy checks the account, and Retry asks them to confirm. |
| Worker stopped or crashed mid-publish | The post is reclaimed when its lease lapses. Accounts already done stay done; the one in flight becomes Uncertain; the rest carry on. |
| Pharmacy presses Retry | Only accounts that did not publish are tried, with a fresh attempt count. |

### Recurring posts

A pharmacy sets one published poster or ready video, the accounts it goes to
(Facebook Page, Instagram, and YouTube for videos) and a schedule — daily, weekly on chosen
days, every N days, or specific dates — at a local time in the pharmacy's time
zone. It posts until they pause or end it. Social & Ads → **Recurring**, or
**Make recurring** on a poster or video.

- **Nothing new publishes.** A second hosted service (`SeriesWorker`, on and
  off with `Social:Worker:Enabled`) checks every 20 seconds for series that
  are due and turns each due occurrence into an ordinary scheduled post, which
  the publish worker above claims and sends with its usual lease, retries and
  Uncertain handling. One-off publishing is unchanged.
- **No double posts.** An occurrence's id is the series id plus its instant
  (`ser_{series}_{yyyyMMddTHHmmZ}`). Two instances making the same one write
  the same `_id`, and Mongo keeps one. A retry reuses it.
- **No bursts.** If the server was down, only the latest missed occurrence is
  posted, and only if it is under two hours late; the rest are skipped and
  counted. An occurrence queued but never picked up is cancelled once it is two
  hours late.
- **Checked every time.** Before each occurrence: the account is active and not
  on Free, the poster is still published (or the video still ready), and the
  pharmacy is within its plan's active-series limit (the oldest series keep
  running after a downgrade). Any failure stops that post, pauses the series
  with the reason, and emails the pharmacy once.
- **A broken account fails one post.** An account that needs reconnecting is
  still on each occurrence, is marked failed for it, and the other accounts
  still get the post. The series carries on. An account that has been
  disconnected and removed is left off each post instead (it could only fail,
  every time); if none of the series' accounts are left, it pauses once and
  says so. On edit, an account already on the series may stay while it needs
  reconnecting; a newly added one must work.
- **Limits.** Free: none. Starter: 2 active. Pro: 20 active. Enforced by the
  API on create and on resume.
- **Clock changes.** Times are kept as local wall-clock times and converted
  per occurrence, so 09:00 stays 09:00 in March and October. A time the clocks
  skip (01:30 on the spring change) goes out an hour later; a time that happens
  twice goes out once, at the first.
- **Emails to the pharmacy**, through the normal Mailjet path: *Posted*,
  *Needs you* / *Reconnect needed*, and *Paused*. Each outcome is claimed
  atomically before it is sent, so it is emailed once. Each series has its own
  setting — every post and problems, problems only, or off — separate from
  patient campaign unsubscribes.

Collections: `recurring_series`, plus `seriesId` / `occurrenceAtUtc` on
`scheduled_posts` (indexes created at startup).

### Instagram images

Instagram accepts only JPEG, and nothing taller than 4:5. A poster is an A4 PNG,
so for Instagram targets the worker builds a 1080×1350 JPEG — the poster fitted
whole and centred, the sides filled with its own edge colour — and stores it
beside the PNG as `…-ig.jpg`. Videos go as they are (Reels).

---

## Healthcare advertising — read before enabling a campaign

The pharmacy is the advertiser of record, not us, but the rules bite quickly:

- **Google Ads restricts prescription drug terms.** Advertising prescription
  medicines generally requires certification through LegitScript, and
  uncertified campaigns are disapproved rather than merely underperforming.
- **Personalised advertising based on health conditions is prohibited.** Do not
  build audiences or ad copy around a diagnosis.
- **Meta restricts health and pharmaceutical claims**, and rejects "before and
  after" imagery and copy implying a personal health attribute.
- **UK**: prescription-only medicines cannot be advertised to the public at all
  (Human Medicines Regulations 2012). Services — flu jabs, blood pressure
  checks, travel clinics — are fine; the medicines themselves are not.

Nothing in this codebase enforces any of that. The publish dialog and the
campaign wizard carry the warning; the responsibility sits with the pharmacy.

---

## Current state

| Piece | Status |
|---|---|
| Credential encryption + rotation | Done, tested |
| Poster rasterisation (canvas → PNG) | Done — see below |
| Facebook Page publishing | Implemented |
| Instagram publishing | Implemented |
| Scheduling, retry, partial failure | Done — automatic retry with backoff, uncertain outcomes held for a person |
| Publish worker with atomic lease | Done — lease renewal, crash recovery, conditional writes |
| Instagram poster format (4:5 JPEG) | Done |
| OAuth state bound server-side, multi-instance safe | Done |
| Connections / publish / ads API | Done |
| Google OAuth (consent, token exchange, refresh token) | Implemented — Ads and YouTube. No developer token needed since Google's 10 Sep 2026 change; Ads needs the Cloud project's Basic access for real accounts |
| Google Business Profile publishing | Adapter registered, returns "not available yet"; its OAuth scope is not requested until it exists |
| Meta deauthorize / data-deletion callbacks | Done — signed_request verified |
| Plan and account re-checked when a post is due | Done — deactivated, Free, or unpublished poster stops it |
| Daily connection health check | Done — Facebook, Instagram, YouTube and LinkedIn; a rejected token shows "Reconnect" the same day |
| YouTube publishing (Shorts, videos only) | Done — resumable upload; forced private until Google audits the project (§3b) |
| LinkedIn profile publishing (posters and videos) | **Live in production, 30 Oct 2026** (§3c) |
| LinkedIn company Page publishing | Built; off until LinkedIn approves Community Management (`Social__LinkedIn__PagesEnabled`) |
| LinkedIn 60-day sign-in expiry | Done — reminder from 14 days before, Reconnect renews profile and Pages together |
| Recurring posts (series) | Done — Facebook, Instagram, YouTube, LinkedIn; Starter 2 / Pro 20 active; emails |
| TikTok (videos, Direct Post) | Built 3 Nov 2026 (§3d). Sandbox until TikTok audits the app: posts private, private accounts only |
| Instagram "View" links | Done — permalink read after publishing, best effort |
| Google Ads campaign creation screen | Done — creates paused |
| Google Ads API client (REST, v25) | Implemented — Search and Performance Max; run the test-account checklist before real use |
| `/social` UI — accounts, posts, recurring, ads | Done |
| Publish dialog on posters and videos | Done |

The two stubs are registered rather than omitted so the failure is a readable
message instead of "no publisher for this platform" from inside the worker.

### How a poster becomes an image

Nothing in the product stored a rendered poster. The canvas JSON is the source
of truth and every view is regenerated from it — grid previews are SVG drawn in
the browser, PDFs are drawn per request. `Poster.ThumbnailBlobKey` existed but
nothing ever populated it, so the first version of social publishing failed on
every poster with a message about the poster not existing.

Posters are now rasterised on demand:

```
canvas JSON → QuestPdfExporter.RenderPngAsync → blob: posters/{ownerId}/{posterId}-{hash}.png
```

- **Same renderer as the PDF.** `QuestPdfExporter` implements both
  `IPosterPdfExporter` and `ICanvasRasterizer`. Same parse, same image prefetch,
  same z-ordered bands — only the output stage differs — so a poster's PNG and
  its PDF are the same picture. Two implementations would have drifted.
- **Cached by content hash.** The blob key contains a SHA-256 of the canvas
  *and* the watermark flag. Editing a poster changes the key, so a stale image
  can never be served; re-publishing an unchanged one costs a single existence
  check. The watermark is in the hash because an upgrading pharmacy must stop
  serving the watermarked render they just paid to remove.
- **1240px wide**, the canvas's own width for A4 at 150dpi and comfortably above
  Instagram's 1080px feed, so platforms downscale rather than upscale.
- **`GET /api/posters/{id}/export.png`** exposes the same render, so download
  and publish share one cache and one watermark decision.

Bitmaps are composited separately from the vector layers rather than embedded in
the SVG, because SkiaSharp's SVG parser silently drops
`<image href="data:…">` — the same constraint the PDF path already works around.

### Google Ads status

The client (`GoogleAdsRestClient`) talks to the Google Ads API over REST:
listing accounts (through managers), creating Search and Performance Max
campaigns, starting and pausing them, and reading lifetime metrics. Without
the Google OAuth client (or with `Social__Google__AdsEnabled=false`) the server
uses a placeholder and the Google card stays disabled. The request shapes are pinned by `GoogleAdsRestClientTests`; whether
Google accepts them is what the test-account checklist above proves.

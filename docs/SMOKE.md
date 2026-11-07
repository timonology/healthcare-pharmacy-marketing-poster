# Smoke test

One cheap call through every paid door, against a deployment that is actually
running.

```bash
pnpm smoke
```

Exit code 0 means shippable. Anything else, read the report.

## Why this exists

Every suite in the repo passed while production was broken. Not through
negligence — through scope. Each of these was invisible to the tests *by
construction*:

| What broke | Why no test could catch it |
|---|---|
| A container ran five days stale and 404'd an endpoint that had shipped days earlier | Tests run against **source**. Production runs an **image**. A stale image passes 100% of everything. |
| Anthropic started rejecting `temperature`; Tavily changed its return shape | The Python suite mocks both providers — correctly, since nobody wants a suite that costs money and flakes. The consequence is that *what a provider sends* is untested. |
| `$push` onto an explicitly-null field failed, freezing every render | The .NET API and the Python worker share one document and are tested **separately**. Nothing exercised both against the same database. |
| A raw `TypeError` was rendered onto a pharmacist's screen | No test reads what the customer reads. |

This script closes all four, because it talks HTTP to the running web, API and
ai-service and reads what a customer would read.

## What it does

| Step | Checks |
|---|---|
| Reachability | `api` and `ai-service` `/health`, the web tier answers, and its `/api/health`. Reports the **build id** each service claims, and **fails when the API and web app are on different builds** — a half-finished deploy where each service is healthy and the seam between them is not. The ai-service on a different build warns (it is often skipped on purpose). A hosted deployment with no build ids warns: ship with `scripts/deploy.sh`, which now stamps all three images. |
| API runs as Production | Hosted only. `/api/diagnostics/jwt` answers only in Development, so a 200 means a deployed API is running as Development — leaning on development credentials that are no longer shipped. FAIL. A campaign that fails for missing email credentials is also a FAIL now, not a provider warning. |
| ai-service routes | Reads `/openapi.json` and asserts the endpoints the API depends on are actually served, including the photo-mode routes (`image-options`, `stock-photos`, `stock-photo`). This is the stale-container check. |
| ai-service is this checkout | For a local build (`build=local-…`), compares the image's stamp with `apps/ai-service/app` on disk: same `.py` file count, and no change more than 15 hours newer than the build. Catches an image whose routes all exist but whose behaviour is old — the 5 October run where a 29 September image returned pre-house-style posters. Fix: `docker compose up -d --build ai-service`. Runs only when `AI_URL` is this machine (a remote build without `BUILD_ID` also says `local-…`); `SMOKE_SKIP_BUILD_CHECK=1` turns it off. A checkout or branch switch touches file times, so it can ask for a rebuild the code did not strictly need — a rebuild is the cheap side of that mistake. |
| Sign in | Once against the API for a bearer token, and once through the **web app's own BFF** so the session cookie and proxy are exercised — the half an API-only smoke never touches. |
| Readiness (from inside) | Needs `SMOKE_READINESS_TOKEN` (the API's `Readiness__Token`, the ai-service's `READINESS_TOKEN`). The **API** checks, each with a free read and a 10s limit: database ping and account count; storage write/read/delete of a probe blob; Mailjet keys and that the sender is verified; Stripe key and every configured price (and warns on a test key in Production); Meta app id/secret (Meta issues an app token) and that the site's domain is in the app's **App Domains**; Google client (an invalid-code exchange tells a bad client from a good one; WARNs if a legacy Ads developer token is still set); the LinkedIn and TikTok clients the same way (and which of LinkedIn's profiles and Pages are offered); that stored social connections **open with the configured key**; public addresses; the ai-service reachable from the API; the client-address key. The **ai-service** checks Anthropic, Pexels, ElevenLabs (including characters left), Replicate, Tavily (its free usage endpoint: key valid and research credits left — never a search), its database and storage. Also FAILs when the address the API builds links on is not the site under test. Without the token: SKIP locally, WARN hosted. |
| Social connections | For every provider the Social page offers, **starts a real connection through the web app** from `${WEB}/social/callback/<kind>`, exactly as the Connect button does, and checks the provider would send people back there. A refusal ("Connections must be started from …" — the 22 October production failure after the move to marketing25.co.uk) FAILs with the setting to change. For Google it also opens the consent page, which reports `redirect_uri_mismatch` or a bad client before sign-in. Meta checks redirects only after sign-in and offers no API for its exact redirect list, so readiness checks App Domains and the report names the address that must be in Facebook Login → Valid OAuth Redirect URIs — after any domain change, connect one Facebook page by hand (QA script 10a.1). Leaves one short-lived OAuth session row, which expires. |
| Account email | Added after password reset emails failed to arrive on production while campaign email worked. **Users are told apart:** through the web app, asks the API whether it can tell this browser from others. If the web app's `CLIENT_IP_SIGNING_KEY` and the API's `Edge__ClientIpKey` differ (or the web app has none), every user shares one address, and the per-address limits — five reset requests in fifteen minutes — apply to everybody at once: a FAIL. **Password reset email:** requests a reset for the smoke account through the web app's forgot-password route, exactly as the page does, then asks the API as that account what happened (`/api/auth/account-email/latest`). FAILs if the request was refused as too frequent, if nothing reached the account, if the provider did not accept the email, or (hosted) if the API is in email mock mode or the link points anywhere but the web address. Sends one real email to `SMOKE_EMAIL`; `SMOKE_RESET_EMAIL=0` skips it. |
| Plan | Reads the subscription so the report says which tier it ran as and what allowance was left. |
| Posters | Creates one **from a published template**, publishes it, fetches `export.pdf` and `export.png`, and checks the **magic bytes** and the **size** — a 200 containing an HTML error page has happened before, and so has a correctly-signed blank page. Then the **My Posters card** (`/card`): a real JPEG that is not empty, and a `304` when asked again with its ETag. An empty card fails the run — published posters once showed a blank "?" card while every export passed. |
| Campaign | Sends **one** email to `SMOKE_EMAIL_TO`, then polls until `Sent` or `Failed`. |
| AI poster design | One design ("walk-in flu clinic, bold", stock photo), if the plan has allowance left, then checks it is in the house style: A4, a headline of 60px+, exactly one booking QR, no unfilled `{{token}}`, no ✓/★ glyphs, no translucent rects, and the photo in our own storage, never hot-linked. Any of those is a FAIL. A photo note (e.g. no stock match, so photo-free) is a WARN. A `403` is a SKIP — being refused correctly is the feature working. |
| Narration | Asks which voices this deployment offers. **FAILs a hosted deployment that reports `edge`** — Edge-TTS is refused from datacentre networks, so every render there would die at the voice stage. Costs 200ms and saves three minutes plus two model calls. |
| Attached picture | Uploads a real 64×64 PNG (generated, not a committed fixture) and attaches it to the render below. Then pushes one just over the 8MB cap and checks it is refused **with a message naming a size** — the cap is enforced by `[RequestSizeLimit]`, which rejects before the action runs, so a bare 413 tells the pharmacist nothing. The good upload goes first on purpose: if it worked, the connection is fine, so whatever happens to the oversized one is the cap doing its job rather than the network. |
| Video | Starts one short render and polls to `Ready` or `Failed`, up to **five minutes** — a real local render measures ~3m30s, of which 91s is ffmpeg. Downloads the result, checks it is not suspiciously small, and **reads its actual duration out of the MP4** (against the duration the API says was ordered, not a constant in the script). |
| Picture actually used | On a `Ready` video, checks `diagnostic` says nothing about images. A picture that cannot be read gives up its beat to stock footage and the render carries on — deliberately, so one bad JPEG never costs a video and the allowance it spent. That tolerance is what makes a dropped picture invisible: Ready status, playable MP4, empty note. The PNG above is built to decode, so anything here means the feature is broken rather than the file. |
| Video refine | Changes the video just made, the way a pharmacist does. Lists its shots (a tile per shot, each with a still, one loaded **through the web app** as a browser would). A shot not in the video must be refused. A typed request naming no shot ("replace the shot with medicine…") must be answered with *tap the shot*, not guessed. Taps the last stock shot, asks for "a person stepping on a bathroom scale", and requires `Ready` **with no "not made" note**. Then checks the tapped shot's footage changed and the others did not, the MP4 is the same length (the voice was reused), and the allowance did not move. Each is a FAIL: every one reached a pharmacist before this step existed. The exception is another shot changing *alongside* the tapped one, which is the designed re-download fallback, so it warns. |
| YouTube | **Connect check** (in Social & Ads, like Meta and Google): the sign-in asks for `youtube.upload` and not Ads, and Google's consent page accepts the YouTube callback and scopes. `invalid_scope` FAILs with "add youtube.upload and youtube.readonly on the consent screen". **Real upload** (opt-in): if the smoke account has a YouTube channel connected and `SMOKE_YOUTUBE_PUBLISH=1`, queues the finished smoke video to it through the normal publish path and waits up to five minutes for `Published` and a `youtu.be` link. It is a real upload that uses YouTube quota: set `Social__Google__YouTubePrivacy=unlisted` on the API first, and delete it in YouTube Studio afterwards. Without a channel, or without the flag, it SKIPs and says which. |
| LinkedIn | **Connect check** (in Social & Ads): the sign-in asks for a posting permission (`w_member_social`, or `w_organization_social` with Pages on), and LinkedIn's sign-in page is opened without signing in. `unauthorized_scope_error` coming straight back FAILs — usually `Social__LinkedIn__PagesEnabled=true` before LinkedIn approved Community Management, or a missing product on the app. A redirect LinkedIn reports as not registered FAILs with the URL to add (the 29 October production run). Some problems LinkedIn only reports after sign-in, so after setting up an app connect once by hand (QA script 10a.3). **Sign-in expiry**: with a LinkedIn account connected to the smoke account, WARNs within 14 days of its 60-day sign-in running out, FAILs once it has. **Real post** (opt-in): `SMOKE_LINKEDIN_PUBLISH=1` posts the smoke poster (`=video` the smoke video) through the normal publish path and waits up to five minutes for `Published` and a link. LinkedIn posts are public — delete it afterwards. Prefers a connected Page over a profile. |
| TikTok | **Connect check**: the sign-in carries a client key and asks for `video.publish`. TikTok's page is drawn by script, so a bad client key or unregistered redirect shows only in a browser — connect once by hand after setup (QA script 10a.12). **Creator info**: with a TikTok account connected to the smoke account, reads its live settings through the API, as the share screen does. **Real post** (opt-in): `SMOKE_TIKTOK_PUBLISH=1` posts the smoke video as *Only me* with comments, Duet and Stitch off, and waits up to six minutes for `Published` (TikTok processes it first). Delete it in the TikTok app afterwards. |
| X | **Connect check**: the sign-in carries a client id, a PKCE challenge (S256) and asks for `tweet.write`, `media.write` and `offline.access`. X's page is drawn by script, so a wrong client id or unregistered callback shows only in a browser — connect once by hand after setup (QA script 10a.18); readiness `x` checks the client id and secret. **Real post** (opt-in): `SMOKE_X_PUBLISH=1` posts the smoke poster (`=video` the smoke video) with a time-stamped caption, since X refuses duplicates. Public and uses API credits — delete it on X afterwards. |
| Display screens | Registers one and mints a pairing code. Then, if the Video step finished a video, puts it on that screen: builds a playlist with it, pairs **through the web app** as a device does, and checks the kiosk content lists the video with a signed `/api/display/video/…` link (and never the device token); that a kiosk **not** declaring video is sent none; and that the link answers `Range: bytes=0-1023` with **206** and a strong `ETag`. Ignoring the range is a WARN — Chrome plays it, Safari does not. No ETag is a WARN — it plays, but screens re-download every video on every loop. `SMOKE_DISPLAY_VIDEO=0` skips this part. |
| Cleanup | Deletes what it created — **unless something failed**, in which case it leaves everything in place so the evidence survives. `SMOKE_KEEP=1` always keeps. |

Social publishing and Google Ads are deliberately not covered.

The publish step is not ceremony. A new poster is a **draft**, and
`PosterUsage.WhyCannotSend` refuses to put a draft in front of patients — so the
smoke walks the path a pharmacist walks, publish then send. Its first real run
skipped it and the campaign step failed with *"is still a draft"*, which reads
like a broken deployment and was the product working exactly as designed. The
stub enforces the same rule, so dropping the publish step fails the self-test.

The template matters for the same reason. The first green run exported a **1 KB
PDF** — a valid, correctly-signed, entirely blank page, because the poster had
been created empty. It passed, and it proved almost nothing: a renderer that had
stopped drawing would have passed identically. Building from a template puts a
real design through fonts, images and layout, and gives the size floor something
to mean. Under it the step WARNs rather than FAILs — the floor is judgement, and
judgement should not block a release by itself.

**Length is checked, not inferred from size.** The clip is exactly as long as
its narration, and the narration is written by a model against a word budget —
a soft constraint. A prompt carrying "140-160 words for a 60s Short" against a
15-second request produced 27-second videos for weeks, and the only symptom
anybody noticed was "videos take a while". Size cannot show that: it moves with
bitrate and content. So the runner parses the `mvhd` box of the downloaded MP4
and reports the real duration, warning above 1.5× the ordered length.

Every call is also on a clock (`SMOKE_REQUEST_TIMEOUT_MS`, two minutes). Node's
fetch would otherwise wait around five, and a script sitting silently on one
request for five minutes looks hung — you reach for ctrl-C, and the report you
were waiting for never prints. A door that doesn't answer is the finding; it
should arrive as a FAIL with a sentence. The slow steps announce what they are
waiting for, for the same reason.

## PASS, WARN, FAIL

The one judgement call in the script, and the reason it is worth reading:

- **PASS** — it worked.
- **WARN** — it failed, *with a message written for a person*. A provider having
  a bad minute is not a reason to hold a release. Warnings do not fail the run.
- **FAIL** — one of:
  - a route the API needs is missing from the running image;
  - a render is **still going and has stopped moving**. A job that can neither
    finish nor fail is the worst state there is: the customer watches a progress
    bar for ever. That was the exact symptom of the `usageAttempts` bug, where
    the failure path could not even record the failure. *Still going but still
    reporting new stages* is a WARN, not a FAIL — the first version conflated
    them and reported a render that finished 28 seconds past the clock as
    "cannot finish or fail", which is how a real signal gets ignored;
  - a `note` or error contains a **traceback or exception type**. That means an
    exception reached the customer unhandled, which is a bug in this build
    whatever the provider was doing.

That last rule is the important one. "The video failed" is not a release
decision; "the video failed and showed the pharmacist
`expected string or bytes-like object, got 'list'`" is.

### A flaky connection is not a failed deployment

A request that dies on the network — `ECONNRESET`, a connect timeout, a
download cut off mid-file (`terminated`) — is tried once more after three
seconds: any GET, and any request whose connection never opened. A POST that
was cut off after it was sent is not repeated, since it may have done its
work. Requests that only passed on the second try are listed in one dim line
above the failures; if that line appears often, the connection from the
machine running the smoke is the problem. Self-test scenario
`download-cut-once`.

### Two messages per failure

A failed render now carries both, and the report prints both:

```
WARN  video render
      Video failed, with a human message: Something went wrong while making this video…
      Last stage reached: Voicing… 45%. Video id 84101b27….
      Reason: NonRetriablePipelineError: ElevenLabs could not narrate this
              ← ElevenLabsError: ElevenLabs returned 401: Invalid API key
```

`note` is the pharmacist's sentence and is what the app displays. `diagnostic`
is the exception type and message — no stack frames, no file paths — stored so
that diagnosing a production failure does not require access to the host's log
stream. It came from a real round trip: the smoke reported a failed render on
Railway, and the reason ("Edge-TTS is not served to datacentre networks") was
three clicks away in a log nobody running the smoke necessarily has.

**Classification still reads `note` only.** The diagnostic is technical by
definition, so judging it with the traceback rule would turn every provider
outage into a FAIL and make the one signal that matters meaningless.

## Is the smoke test still working?

```bash
pnpm smoke:selftest
```

A smoke test that has quietly stopped noticing things is worse than none,
because its green line gets read as assurance. This repo has been bitten by that
shape twice — a source scanner that matched nothing and passed, and a tamper
test whose "tampered" token was byte-identical one run in sixteen.

So the runner is exercised against a fake deployment (`stub-server.mjs`) that
can be told to break in each way that matters, and the self-test asserts the
exit code for each:

| Scenario | Expected |
|---|---|
| `healthy` | exit 0 — a working deployment passes |
| `video-stuck` | exit 1 — a render that can neither finish nor fail |
| `video-long` | exit 0 — a 27s render for a 15s product warns, does not block |
| `video-slow` | exit 0 — slow but still reporting; warns, and is **not** deleted |
| `video-crash` | exit 1 — a traceback on a customer's screen |
| `video-outage` | exit 0 — a human failure message warns, does not block |
| `image-dropped` | exit 1 — Ready, playable, and the attached picture silently missing |
| `image-mute` | exit 0 — an oversized upload refused with a bare 413 and no reason |
| `route-missing` | exit 1 — a route missing from the running image |
| `narration-edge` | exit 1 — a hosted deployment with no ElevenLabs key |
| `narration-missing` | exit 1 — the API cannot reach ai-service for the voice list |
| `image-ignored` | exit 1 — a render that never looked at the attached pictures |
| `refine-not-made` | exit 1 — a shot change comes back Ready but unchanged |
| `refine-guesses` | exit 1 — a typed request naming no shot is carried out anyway |
| `refine-no-timeline` | exit 1 — a fresh video's tiles have no stills at all: the rendered timeline never reached the worker |
| `refine-no-shots` | exit 1 — a finished video has no shots on record (an old ai-service image) |
| `refine-wrong-shot` | exit 1 — the change lands on a shot nobody tapped |
| `refine-revoiced` | exit 1 — a shot change re-records the narration |
| `refine-charged` | exit 1 — a shot change uses a video from the allowance |
| `refine-recovery-miss` | exit 0 — another shot re-fetched differently beside the tapped one warns, does not block |
| `screen-video-leak` | exit 1 — a video is sent to a kiosk that never said it could play one |
| `screen-video-broken` | exit 1 — a screen's signed video link will not open |
| `screen-video-norange` | exit 0 — the video link ignores Range; warns (plays in Chrome, not Safari) |
| `screen-video-noetag` | exit 0 — the video link has no validator; warns (screens re-download every loop) |

A few minutes, and it touches nothing real: no API, no model spend, no email.
Run it after editing `run.mjs`.

## The account it runs as

The smoke signs in as **its own account**, not the SuperAdmin. The API seeds it
on startup (`SmokeAccountSeeder`) from the same `SMOKE_EMAIL` / `SMOKE_PASSWORD`
the runner reads, so there is one credential rather than two that drift.

| | |
|---|---|
| Email | `smoke@sonar.local` |
| Role | **PharmacyUser** — not an admin |
| Plan | **Pro** (`SMOKE_ACCOUNT_TIER`) |
| Profile | "Smoke Test Pharmacy", onboarding complete |

Three reasons it is not the SuperAdmin:

- every run left posters, campaigns and videos on the one account an operator
  has to be able to trust;
- staff sit on the **Free** plan, so the video and AI-design steps reported *no
  allowance* and skipped — the paid half of the test, the half that costs money
  and the half that keeps breaking, was never running;
- least privilege. This credential lives in a `.env` and will end up in CI. What
  a leak costs should be one pharmacy account full of test data.

**The seeder never touches an account that already exists.** Changing
`SMOKE_PASSWORD` here does *not* change it on the server — same rule as
`SuperAdminSeeder`, and for the same reason: a seeder that reset the password on
every boot would be a standing backdoor that silently reverts a rotated
credential.

Outside Development it refuses the password published in this repository **by
value**, not just by length — that password is nineteen mixed characters and
would sail through any complexity rule while being the first thing an attacker
tries. `SmokeAccountPolicy` holds that rule as a pure function, and
`SmokeAccountPolicyTests` pins it.

If sign-in fails on a fresh database, the account has not been seeded yet:

```bash
docker compose up -d api     # runs the startup tasks
```

## Environment

Settings live in the repo-root **`.env`**, in the `# ----- Smoke test -----`
block (`.env.example` has the same block with comments). `pnpm smoke` reads that
file itself — there is no `dotenv` dependency and no `--env-file` flag to
remember.

**The real environment always wins over the file**, so one run can be pointed
elsewhere without editing anything:

```bash
API_URL=https://sonar-marketing-api-test.azurewebsites.net pnpm smoke
```

That order matters: a developer's `.env` points at their laptop, and a smoke run
that silently ignored the URL you typed would report a green result about the
wrong machine. The report header prints the file it read and how many values it
took from it, so "which deployment did that actually test?" always has an answer
on screen.

| Variable | Default |
|---|---|
| `API_URL` | `http://localhost:$API_HOST_PORT`, else `:5050` |
| `AI_URL` | `http://localhost:$AI_HOST_PORT`, else `:8000` |
| `WEB_URL` | `http://localhost:$WEB_HOST_PORT`, else `:3000` |
| `SMOKE_EMAIL` / `SMOKE_PASSWORD` | the seeded smoke account; read by **both** the runner and the API's seeder |
| `SMOKE_ACCOUNT_ENABLED` | `true` — set false to stop the API seeding it |
| `SMOKE_ACCOUNT_TIER` | `Pro` |
| `SMOKE_EMAIL_TO` | `timothy.babalola009@gmail.com` |
| `SMOKE_VIDEO_TIMEOUT_MS` | `300000` |
| `SMOKE_VIDEO_SECONDS` | `15` — the length the product sells, for the over-length check |
| `SMOKE_CAMPAIGN_TIMEOUT_MS` | `90000` |
| `SMOKE_REQUEST_TIMEOUT_MS` | `120000`, per HTTP call |
| `SMOKE_KEEP=1` | keep what it created (`pnpm smoke:keep`) |
| `SMOKE_YOUTUBE_PUBLISH=1` | upload the smoke video to the smoke account's connected YouTube channel (off by default) |
| `SMOKE_LINKEDIN_PUBLISH=1` / `=video` | post the smoke poster (or video) to the smoke account's connected LinkedIn profile or Page (off by default; public) |
| `SMOKE_TIKTOK_PUBLISH=1` | post the smoke video to the smoke account's connected TikTok account as Only me (off by default) |
| `SMOKE_X_PUBLISH=1` / `=video` | post the smoke poster (or video) to the smoke account's connected X account (off by default; public, uses X API credits) |
| `SMOKE_ENV_FILE` | read a different env file |
| `SMOKE_NO_ENV_FILE=1` | ignore `.env` entirely (the self-test sets this) |
| `SMOKE_SKIP_BUILD_CHECK=1` | skip "ai-service is this checkout" (local runs only) |
| `SMOKE_HOSTED=1` | treat the target as a real deployment; inferred from the URL otherwise |

**Mind the web port.** `docker compose` publishes the web app on
`WEB_HOST_PORT`, which defaults to **9090**; `pnpm dev:web` and the Playwright
config use **3000**. The two have always disagreed, so the URL defaults follow
the same `*_HOST_PORT` variables compose does rather than repeating a literal —
otherwise a compose stack fails the BFF step with "connection refused" and sends
you looking for a bug in the web app.

The variable names match `scripts/e2e/run.sh` and the Playwright config, but
note that *those* still read only the process environment — `.env` is loaded by
the smoke runner alone.

The file parser is deliberately small: `KEY=value`, `#` comments, optional
quotes, optional `export`. No interpolation, no multi-line values. Anything more
than that, pass on the command line.

Against a deployed environment:

```bash
API_URL=https://sonar-marketing-api.azurewebsites.net \
AI_URL=https://sonar-marketing-ai.azurewebsites.net \
WEB_URL=https://sonar-marketing-web.azurewebsites.net \
SMOKE_EMAIL=... SMOKE_PASSWORD=... \
pnpm smoke
```

## What it costs

One model call for the video script, one for the poster design, one Tavily
search, one email. Pennies. It is safe to run before every release and that is
the point — a smoke test nobody runs because it is slow or expensive is a smoke
test that does not exist.

It writes real records. Run it against dev or the test environment by
preference; against production, it creates and then deletes one poster, one
video and one screen, and sends one email.

## Where it sits

`pnpm test` proves the code is correct. **`pnpm smoke` proves the deployment is
alive.** They fail for different reasons and neither replaces the other — the
whole reason this file exists is that a green `pnpm test` told us nothing about
a container running five-day-old code.

Run it after `pnpm ship:*`, before telling anyone the release is out.

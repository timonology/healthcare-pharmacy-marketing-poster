# Project for Sonar Informatics. Healthcare Marketing Poster

Kindly ignore my Acme abbreviation :)

A SaaS for community pharmacies to design, share, and send professional marketing posters — **and now generate short-form marketing videos** with a Claude-powered agent. Pharmacists pick a template, drop in their brand kit and details, edit on a drag-and-drop canvas, then export to PDF or send to patients by email / SMS. On the video side they type a topic and the agent researches → scripts → narrates → edits a 15-second 9:16 clip ready for Reels / TikTok / YouTube Shorts. Posters and videos publish straight to Facebook, Instagram, LinkedIn, YouTube and TikTok — once, or on a recurring schedule — and can run as local Google Ads campaigns. All gated by a tiered subscription model.

This document describes the architecture, domain model, and the local + production setups.

## Stack

| Layer | Technology |
|-------|------------|
| Web | Next.js 14 (App Router), TypeScript, Tailwind, shadcn/ui, Zustand, React-Konva, next-themes |
| API | .NET 10 Web API (Controllers) with Clean Architecture |
| AI Service | Python 3.11, FastAPI, **LangGraph video pipeline**, LangChain (Claude via Anthropic + Azure OpenAI for canvas) |
| Video pipeline | ElevenLabs (voice — required when hosted) / Edge-TTS (free, local only), Pexels (stock B-roll), Replicate Flux Schnell (AI imagery, Pro), MoviePy 1.0.3 + FFmpeg for compositing |
| Database | Azure Cosmos DB (MongoDB API) - local Mongo container in dev |
| Auth | JWT issued + validated by the .NET API, rotating refresh tokens |
| Storage | Azure Blob Storage (Azurite locally) for brand logos and rendered videos |
| PDF export | QuestPDF (Community license) via canvas-to-SVG pipeline |
| Email | Mailjet HTTP API or SMTP via System.Net.Mail, with mock provider for dev |
| SMS | Pluggable `ISmsSender` (mock provider for dev, Twilio/Azure Communication ready) |
| External | `ISonarApiClient` calls the Sonar Backend to enrich profiles with FCode + ODS data; falls back to a deterministic mock when the upstream isn't configured |
| Orchestration | Docker Compose (dev), Azure App Service for Containers (prod) via `infra/azure/main.bicep` + `scripts/deploy.sh` |

## Repository Layout

```
.
├── apps/
│   ├── web/                              # Next.js 14 frontend
│   │   └── src/
│   │       ├── app/                      # Routes (App Router)
│   │       │   ├── (auth)/               # login, register
│   │       │   ├── onboarding/           # 3-step pharmacy onboarding
│   │       │   ├── dashboard/            # Welcome, stats, quick actions
│   │       │   ├── profile/              # Pharmacy profile editor
│   │       │   ├── campaigns/            # Email + SMS campaigns
│   │       │   ├── templates/, posters/  # Template library + my posters
│   │       │   ├── posters/[id]/         # Konva editor
│   │       │   ├── videos/               # Marketing video list + Create dialog
│   │       │   ├── pricing/              # Subscription plans
│   │       │   └── api/                  # BFF route handlers (incl. /api/videos/*)
│   │       ├── components/
│   │       │   ├── editor/               # Konva canvas + sidebars + dialogs
│   │       │   ├── landing/              # Marketing site sections
│   │       │   ├── site/                 # Global SiteHeader
│   │       │   └── ui/                   # shadcn primitives
│   │       ├── lib/                      # api client, canvas helpers
│   │       └── store/                    # Zustand stores
│   ├── api/                              # .NET 10 Web API
│   │   ├── src/
│   │   │   ├── Acme.Domain/              # Entities, value objects, errors
│   │   │   ├── Acme.Application/         # Use cases, DTOs, interfaces
│   │   │   ├── Acme.Infrastructure/      # Mongo, JWT, Blob, PDF, HTTP clients
│   │   │   └── Acme.WebApi/              # Controllers, middleware, DI
│   │   ├── tests/
│   │   └── Acme.sln
│   └── ai-service/                       # Python FastAPI + LangGraph
│       ├── app/
│       │   ├── main.py                   # FastAPI + lifespan that starts the video worker
│       │   ├── config.py                 # pydantic-settings for all env vars
│       │   ├── routers/canvas.py         # existing canvas AI endpoints
│       │   └── video_agent/              # NEW: video generation pipeline
│       │       ├── context.py            # UserContext (topic, tone, voice, style)
│       │       ├── state.py              # AgentState typed dict
│       │       ├── graph.py              # LangGraph wiring (5 stages)
│       │       ├── nodes.py              # researcher · scriptwriter · voice · visuals · editor
│       │       ├── ai_imagery.py         # Replicate Flux Schnell (Pro-tier AI visuals)
│       │       ├── tts_policy.py         # Which narrator, and why (no Edge fallback when keyed)
│       │       ├── voices.py             # The voice catalogue the app offers
│       │       ├── elevenlabs_tts.py     # ElevenLabs voice (required when hosted)
│       │       ├── storage.py            # Azure Blob adapter (auto-rewrites Azurite URL)
│       │       ├── worker.py             # Motor poller + progress writes
│       │       └── README.md             # Pipeline design + trigger flow
│       ├── Dockerfile                    # python:3.11-slim + ffmpeg + imagemagick + fonts
│       ├── README.md                     # Env-var reference + how-to-run
│       └── pyproject.toml
├── packages/
│   └── shared-types/                     # Source-of-truth TypeScript contracts
├── infra/
│   └── azure/
│       ├── main.bicep                    # Provisions all 3 App Services + Cosmos + Blob + ACR
│       └── main.parameters.json
├── scripts/
│   └── deploy.sh                         # Build + push + restart api/web/ai-service
├── docker-compose.yml
├── .env.example
├── AZURE.md                              # Full Azure deployment guide
├── RAILWAY.md                            # Railway alternative
└── README.md
```

## Architecture

```
        Browser  ─https─▶  web (Next.js)
                                │
                                ├─ client-side fetch /api/* (BFF route handlers)
                                │                              │
                                │                              ▼
                                └─ server-side fetch ───────▶ api (.NET) ─────▶ MongoDB ◀────┐
                                                              │                              │
                                                              ├─▶ Azure Blob (logos, MP4s) ◀─│
                                                              ├─▶ Sonar API (mock-friendly)  │
                                                              ├─▶ Mailjet / SMTP (mockable)  │
                                                              └─▶ SMS provider (mockable)    │
                                                                                             │
                                                              ai-service (Python) ───────────┘
                                                              │
                                                              │  polls Mongo for Pending videos
                                                              │  every 4s and runs LangGraph:
                                                              │
                                                              ├─▶ Tavily (research)
                                                              ├─▶ Claude (script)
                                                              ├─▶ ElevenLabs (voice; Edge-TTS locally)
                                                              ├─▶ Pexels (stock B-roll)
                                                              ├─▶ Replicate Flux (AI imagery, Pro)
                                                              └─▶ FFmpeg + MoviePy (compositing)
```

Three containerised services, all sharing the same Mongo + Blob. The .NET API stays authoritative for the `Video` entity (tier gating, ownership, monthly quotas, signed URL generation). The Python ai-service is the render worker — it never touches HTTP requests from the browser; it just polls the `videos` collection and mutates rows Pending → Rendering → Ready. The web app polls `/api/videos` every 2s while any row is in-flight and shows a progress bar reading the `note` field.

The web container never lets the browser talk to the API directly. Every authenticated request goes through a Next.js route handler that reads the JWT from an `httpOnly` cookie and forwards to the API with a `Bearer` header. This keeps the JWT out of `document.cookie` / `localStorage` and means a single CORS origin (the web domain) covers everything.

### Clean Architecture layers

The .NET solution follows the classic four-project Clean Architecture split:

- **`Acme.Domain`** — entities + value objects + domain exceptions. No outward dependencies. Contains: `User` (with embedded `PharmacyProfile`), `RefreshToken`, `BrandKit`, `Template`, `Poster`, `Campaign`, `SubscriptionTier`.
- **`Acme.Application`** — use cases, DTOs, repository / external service *interfaces*. Depends on Domain only. Contains: `AuthService`, `ProfileService`, `BrandKitService`, `TemplateService`, `PosterService`, `SubscriptionService`, `CampaignService`. Also defines `IUserRepository`, `IPosterRepository`, `ICampaignRepository`, `IBrandKitRepository`, `ITemplateRepository`, `IRefreshTokenRepository`, `ISonarApiClient`, `IEmailSender`, `ISmsSender`, `IPosterPdfExporter`, `IBlobStorageService`, `IBrandAssetService`, `IPasswordHasher`, `IJwtTokenService`, `IRefreshTokenGenerator`.
- **`Acme.Infrastructure`** — concrete implementations. Mongo repositories, BCrypt password hasher, JWT issuer, Azure Blob client, QuestPDF exporter, SMTP sender, mock SMS sender, HttpClient-based Sonar client. Also hosts `MongoIndexInitializer` (a startup task) and `TemplateSeeder` (idempotent system-template seeder).
- **`Acme.WebApi`** — controllers (`AuthController`, `MeController`, `BrandKitController`, `TemplatesController`, `PostersController`, `PosterExportController`, `CampaignsController`, `SubscriptionController`), exception middleware, DI composition, JWT bearer auth, CORS, JSON enum config.

`Result<T>` (in `Acme.Application.Common`) is the use-case return type. Kinds: `Success | NotFound | Conflict | Invalid | Unauthorized | Forbidden`. Controllers map kinds to HTTP statuses (e.g., `Forbidden` → 403 when a user hits a plan limit).

## Features

### Auth + onboarding

1. **Register** with email + password + display name. Backend hashes with BCrypt, issues access + refresh tokens, stores the refresh hash.
2. **Multi-step onboarding** (`/onboarding`):
   - Step 1 — Pharmacy name, postal address, description.
   - Step 2 — Contact name + mobile number.
   - Step 3 — Plan picker (Free / Starter / Pro, Free default).
3. On submit, the API calls **Sonar Backend** for FCode enrichment (mocked deterministically in dev; real HTTPS call when `Sonar:BaseUrl` is set), stores the result on the profile, sets the tier, marks onboarding complete.
4. **Subsequent logins** land on `/dashboard`. Anyone with `profile.onboardingCompleted=false` is bounced to `/onboarding`.

JWTs are short-lived (15 min) access tokens plus rotating 30-day refresh tokens. Refresh-token reuse detection: presenting a revoked token revokes all of that user's refresh tokens.

### Subscription tiers

| | Free | Starter | Pro |
|---|---|---|---|
| Price | £0 | £7 / month | £25 / month |
| Posters | 5 | 30 | Unlimited |
| **Marketing videos** | **0 / month** | **5 / month** | **30 / month** |
| **AI text-to-video imagery** | No | No | **Yes** |
| AI poster designs | 3 / month | 50 / month | Unlimited |
| Campaign recipients | 0 | 500 / month | 5,000 / month |
| Patient contacts | 50 | 500 | Unlimited |
| **In-pharmacy display screens** | **0** | **1** | **Unlimited** |
| Social publishing | No | Yes | Yes |
| Active recurring social posts | 0 | 2 | 20 |
| Watermark on exports | Yes | No | No |
| Analytics dashboard | No | No | Yes |

Limits are enforced in `Acme.Application` services before mutations:

- `PosterService.CreateAsync` / `DuplicateAsync` → checks `Plans.For(user.Tier).MaxPosters` against `repo.CountAsync(...)` and returns `Forbidden` if exceeded.
- `CampaignService.CreateAndSendAsync` → checks `MaxCampaignRecipientsPerMonth` against `repo.CountRecipientsThisMonthAsync(...)`.
- `DisplayService.RegisterScreenAsync` → checks `MaxDisplayScreens` against the screens already registered. **Checked at registration only** — an account that downgrades keeps the screens it has, deliberately, rather than blanking a shop window over a billing change.

- `PosterAiService` → checks `MaxAiGenerationsPerMonth` (`AiAllowance`) before a pharmacy's "Design with AI" call; one generation is one AI design.
- `VideoService` → checks `MaxVideosPerMonth` (then top-up credits) and `AiVideoEnabled`; `PatientLimits` → `MaxPatients`; `AnalyticsService` → `AnalyticsDashboard`.

The plan cards' wording is written from these limits by `PlanCopy.Highlights`
(Plans.cs) — only enforced limits get a line. The static marketing teaser and
onboarding read the same lines from `packages/shared-types/src/plan-highlights.ts`;
`PlanCopyTests` fails if the two differ.

Plans are a static catalogue (`Acme.Application.Subscriptions.Plans`) — no database state. Upgrading just flips `User.Tier` and persists.

### Brand kit

`/brand-kit` lets the pharmacist set the logo (Azure Blob upload), colour palette, the pharmacy block (name, address, contact, license, phone), and a regulatory footer. The footer is auto-applied to every poster. SAS URLs for logos are short-lived (15 minutes); the editor refetches when expired.

### Editor

`/posters/[id]` opens a Konva-based editor:

- Drag / resize / rotate with a Transformer.
- Tools: select, rectangle, circle, text, line, image upload.
- Live-update during drag, history committed on settle.
- Undo / redo (`⌘Z`, `⇧⌘Z`) with a 100-deep history.
- Layers panel (z-order, reorder, delete, duplicate).
- Properties panel with per-shape forms.
- Text editing via overlay `<textarea>`.
- Wheel-zoom anchored at the cursor; pan via select tool drag.
- **Apply pharmacy info** button (top bar): replaces `{{pharmacy_name}}` / `{{address}}` / `{{contact_name}}` / `{{phone}}` tokens in text shapes, or appends a footer line.
- **PDF** button: hits `/api/posters/{id}/export.pdf` (QuestPDF render via SVG, watermark when applicable).
- **PNG** button: `Stage.toDataURL("image/png")`.
- **Share** button: opens dialog, emails the PDF as attachment to a list of recipients.

Auto-save: every mutation flips `saveStatus` to `dirty`, then a 1.5 s debounce PUTs `/api/posters/{id}`. `AbortController` cancels in-flight saves when more edits arrive.

### Templates

Six curated system templates seeded at startup (idempotently — stable `sys-*` ids, upsert by id):

| Id | Category | Style |
|---|---|---|
| `sys-flu-vaccination` | Vaccination | Bold cyan, photo, bullet list |
| `sys-vitamin-d` | Awareness | Warm amber, vitamin photo |
| `sys-loyalty-rewards` | Promotion | Dark indigo, oversized callout |
| `sys-hand-hygiene` | Safety | Purple, numbered step cards |
| `sys-allergy-season` | Seasonal | Pink palette, three product cards |
| `sys-welcome` | General | Slate header, three service cards |

`/templates` shows them in a filterable grid (category, paper size, search). Each card previews the canvas as SVG (the same `CanvasPreview` component the dashboard uses) — no real thumbnails needed.

### Campaigns

`/campaigns` lists the user's campaigns and offers a "New campaign" modal. Channels:

- **Email** — renders the poster to PNG and draws it **in the body** as an inline `cid:` attachment via `IEmailSender` (Mailjet or SMTP in prod, mock that logs to console in dev). It is not a PDF attachment: those went unopened, and a base64 data URL would trip Gmail's ~102KB clipping and cut the message off. The sender name and pharmacy block are resolved from the brand kit.
- **SMS** — sends a short message via `ISmsSender` (mock by default; the contract is Twilio / Azure Communication Services ready — swap the impl).

Recipient validation is channel-aware: emails go through a strict regex, SMS strips non-digit / non-`+` characters and rejects under 7 characters. Limits are enforced before send. Stop button is enabled while a campaign is `Draft` or `Sending`.

**The poster is re-encoded for email.** The cached render is a 1240px PNG at
quality 100 — right for print, a shop-window screen and Instagram, and about ten
times what an email needs for something displayed at 600px. `IImageTranscoder`
re-encodes it to JPEG at 1200px at the call site in `CampaignService`
(1.15 MB → 174 KB in practice), so the shared render and its cache key are
untouched and the kiosk, social publishing and PDF export are unaffected. If the
re-encode fails or comes out larger it returns null and the PNG goes out
unchanged — a campaign that loses its poster to a failed optimisation is worse
than one carrying a large poster.

**Batching is size-aware, not count-aware.** Mailjet Send v3.1 carries
`InlinedAttachments` *inside each personalisation*, so an inline poster is
uploaded once per recipient. `MailjetEmailSender` chunks recipients against a
byte budget rather than a fixed count. See
`Acme.Tests/Messaging/MailjetBatchingTests.cs`.

**Connections are not reused after sitting idle.** `SocketsHttpHandler` is
configured with a 15-second `PooledConnectionIdleTimeout`, and
`MailjetEmailSender` retries once when the connection dies mid-upload.

This is the fix for a long-running `Broken pipe` fault. It presented as a size
limit — big requests failed, small ones did not — and a request-size budget was
added on that reading. Then **1,008,893 bytes failed where 1,008,621 had
succeeded**. Two hundred and seventy-two bytes apart, opposite outcomes: not a
threshold. The timings gave it away, a successful megabyte taking ~2.5s against
20–26s for a failure — a stall and a reset, not a slow upload. Writing to a
pooled keep-alive the server had already dropped is what that is. Size only
correlated because a longer write is likelier to be holding the dead socket.

The retry is safe because the body never finished uploading, so nothing was
accepted; `HttpCompletionOption.ResponseHeadersRead` keeps the retry boundary
there rather than after Mailjet has replied.

**`Messaging:Mailjet:MaxRequestBytes` (default 1,000,000) is a sensible
default, not a measurement.** Kept because fewer bytes means a shorter write and
less exposure, and Mailjet's own 15 MB limit is real — but do not treat it as a
discovered ceiling.

**Requests declare their length.** `PostAsJsonAsync` is deliberately not used:
`JsonContent` cannot report its length, so `HttpClient` falls back to
`Transfer-Encoding: chunked` and serialises straight onto the socket. When the
far end rejects a request and closes without draining it, the failure surfaces
as `IOException: Broken pipe` thrown from inside `JsonTypeInfo.SerializeAsync`
— naming the JSON serialiser, the one component that is certainly not at fault,
and losing whatever status the server meant to return. `BuildRequest` serialises
to a byte array, sets `Content-Length`, and sets `Expect: 100-continue` so a
refusal arrives before megabytes of poster are uploaded.

`POST /api/admin/diagnostics/email/test` takes an `inlineKb` to reproduce a send
at a chosen payload size, and `scripts/mailjet-probe.sh` does the same with
`curl` — no .NET in the path — which is what distinguishes "our code", "the
network" and "the credentials" when a send fails.

### Social publishing, recurring posts and Google Ads

`/social` (Starter and Pro) connects accounts and publishes posters and videos.
Setup, every setting and the failure behaviour are in `docs/SOCIAL_SETUP.md`.

| Platform | Posters | Videos | Status |
|---|---|---|---|
| Facebook Page, Instagram Business | Yes | Yes | Live (Meta app in development mode until App Review) |
| YouTube channel | No | Shorts | Built; uploads private until Google audits the project |
| LinkedIn profile | Yes | Yes | Live since 30 October 2026 |
| LinkedIn company Page | Yes | Yes | Built; off until LinkedIn approves Community Management |
| Google Ads | Campaign from a poster | — | Built; needs the Cloud project's Basic access (developer tokens were sunset 10 Sep 2026) |
| TikTok account | No | Yes | Built; sandbox until TikTok audits the app (posts private) |
| X account | Yes | Yes | Built 6 November 2026; pay-per-use API credits, 280-character posts |
| Google Business Profile | — | — | Not built |

- **Publishing** goes through one queue (`scheduled_posts`) and `PublishWorker`:
  an atomic lease per post, automatic retries only when nothing can have been
  published, and **Uncertain** (never retried) when it might have been.
- **Recurring posts** (`recurring_series`, `SeriesWorker`) create one
  occurrence per due time in the pharmacy's time zone, DST-safe, skip anything
  more than two hours late, and email posted / failed / paused.
- **Connections** are encrypted (AES-256-GCM), checked daily, and marked
  **Reconnect** when a platform stops accepting them. LinkedIn's 60-day
  sign-in is reminded from 14 days before.

### Brand mark

The Marketing25 logo is a pharmacy cross whose right arm is a play arrow, on a
teal tile (`#11798A`) with a light-teal spark (`#7FE3D6`). It is drawn in one
place for the app — `apps/web/src/components/brand/LogoMark.tsx` — and the same
shape lives in `apps/web/src/app/icon.svg` (browser tab), `app/apple-icon.png`,
`public/brand/` (SVG plus 120/192/512/1024 px PNGs for app stores and the
Google, Meta, LinkedIn, TikTok and X consoles), the link-preview image and the
quick-start PDF (`scripts/build-user-guide-pdf.py`). Change them together.

### Lists: sort and date

The templates gallery, My Posters, Videos, Campaigns, Patients and the admin
users, pharmacies and audit tables share one toolbar — `sort=newest|oldest`,
`from=`, `to=` in the URL — filtered and sorted in Mongo (`ListWindow`).

### In-pharmacy display screens

`/displays` and `/playlists` manage screens; `/display` **is the kiosk the
device itself opens**. Note the singular — the two differ by one character and
only `/display` is public.

- **`DisplayScreen`** — one physical panel: name, location, optional address
  drawn on the screen, and its credentials.
- **`Playlist`** — an ordered set of published poster ids with a dwell time
  each (3–300s, max 50). First-class rather than hanging off a screen, so one
  edit reaches a counter display and a waiting-room display, or every branch a
  head office works as.

Only **published** posters can be assigned — a screen in a shop window is
publication, so it reuses the campaign rule.

**No auth cookie on the kiosk.** A device holds a random token whose SHA-256 is
what's stored; the raw value is never persisted. It is scoped to one screen's
playlist — not the poster library and not the account — because a kiosk hangs
where strangers walk past and can be photographed. Two credential kinds: a
six-character pairing code (four hours, single use, alphabet excludes `O/0`,
`I/1/L`, `S/5`, `Z/2`, `B/8`, `U/V`), and a direct link carrying its token in
the URL **fragment** so it never reaches an access log.

**Polling, not sockets.** `GET /api/display/content` every ~30s with
`If-None-Match`; unchanged answers `304` with no body. A missing token is `400`
and a bad one `401` — the device treats `401` as "credential finished" and
erases it, so a proxy stripping a header must not cost a working screen its
pairing. Slides are the same cached PNGs the campaign emails use.

Full setup guide, including the hardware side: `docs/DISPLAY_SCREENS.md`.

### Marketing videos (agentic AI pipeline)

`/videos` is a topic-first video generator. The pharmacist types a topic (e.g. *"why book your flu jab this autumn"*), optionally adds creative guidance or their own script, and the Python ai-service runs a 5-stage LangGraph pipeline to produce a 15-second 9:16 marketing video ready for Reels / TikTok / YouTube Shorts.

**Pipeline stages** (implemented in `apps/ai-service/app/video_agent/nodes.py`, ported from the Yami Social project):

1. **Researcher** — Tavily search (4 queries) → Claude distills 7 sharp, pharmacy-relevant facts with sources. Quality gate: ≥5 facts, avg score ≥0.55; retries up to 2× if the bar isn't met.
2. **Scriptwriter** — Claude turns facts into strict JSON: title, hook, script, segments (`{start, end, text, visual_keyword}`), description, tags, CTA. Prompts are pharmacy-tuned — no scaremongering, no dosing advice, always frames prescription decisions as "ask your pharmacist".
3. **Voice** — **ElevenLabs whenever `ELEVENLABS_API_KEY` is set, with no fallback**; Edge-TTS only when it is not. The rule lives in `video_agent/tts_policy.py` and exists because Edge-TTS is refused from datacentre networks: every hosted render died at the voice stage while the same code worked locally, and setting the key appeared to change nothing, because the provider used to be chosen from the *voice id*. Text is sanitised (zero-width chars, most emoji, control chars) before synthesis; the Edge path still walks 4 Microsoft voices on transient failure.
4. **Visuals** — Pexels stock video aligned to script segments (default), or Replicate Flux Schnell one image per beat (Pro-tier, cost-capped at 8 images / $0.05 per job).
5. **Editor** — MoviePy fits/crops each clip to 1080×1920, adds text overlays per segment, fadein/out crossfades, watermark for free tier (kept for parity — free is currently blocked by quota), then encodes H.264 with `preset=ultrafast crf=26`.

Three input modes exposed in the Create dialog:

- **AI script** — agent researches + writes from topic. Fastest.
- **Guided** — agent writes, but treats a Description you provide as required creative direction.
- **My script** — you paste the exact narration; the agent only extracts metadata and skips research + scripting entirely.

**Trigger flow** — the .NET API is authoritative for the `Video` entity. `POST /api/videos { topic, ... }` tier-gates, quota-checks, and inserts a `videos` document with `status: Pending`. The ai-service worker polls that collection every ~4s, atomically claims one row via `find_one_and_update`, runs the pipeline, and mutates the row's `note` field with `Stage… N%` throughout. On completion it uploads the MP4 + thumbnail to `videos/{ownerId}/{videoId}.{mp4,jpg}` in the same Blob container the API reads from, and flips the row to `Ready`. The web UI polls `/api/videos` every 2s while any row is in-flight and renders a progress bar reading the percentage out of `note`.

**Retry** — the video card has a retry button when a render fails. It resets `Status = Pending` (no re-charge against monthly quota) and the worker picks the row up again on its next poll.

**Environment** — the ai-service refuses to start the worker if `ANTHROPIC_API_KEY`, `TAVILY_API_KEY`, or `PEXELS_API_KEY` is missing (logged clearly at boot). `REPLICATE_API_TOKEN` is optional (blank disables AI imagery). **`ELEVENLABS_API_KEY` is optional only on a developer machine** — blank means Edge-TTS, which is refused from datacentre networks, so a hosted deployment without it fails every render at the voice stage. See `apps/ai-service/README.md` for the full env-var table with signup links + per-video cost estimates.

### Sonar Backend integration

`ISonarApiClient.LookupAsync(name, address, ct)` returns `SonarPharmacyLookupResult?` with `FCode`, `OdsCode`, `RegulatoryStatus`. The real implementation `POST /api/pharmacies/lookup` to `Sonar:BaseUrl` with an `X-Api-Key` header. Errors are logged and the call returns `null` — onboarding still completes.

Dev mode (`Sonar:UseMock=true` in appsettings) generates a deterministic `F-XXXXX` code from the pharmacy name hash so the rest of the stack exercises the FCode-display code paths end-to-end without the real upstream.

Wired via `AddHttpClient<ISonarApiClient, SonarApiClient>` (`IHttpClientFactory`) so timeouts, retries, and lifetime management are framework-managed.

### PDF export

`IPosterPdfExporter` is implemented by `QuestPdfExporter`. It walks the canvas JSON, builds an SVG string (rects with corner radius, circles, lines, text with soft wrapping, watermark), and hands it to QuestPDF via `page.Content().Svg(svg)`. QuestPDF deprecated direct SkiaSharp access in 2024.3; SVG is now the supported integration path. Result is byte[] returned to the controller, which streams it as `application/pdf`.

## Domain model overview

```
User
├─ Email (value object, validated + normalized)
├─ DisplayName
├─ PasswordHash (value object, BCrypt)
├─ Tier (SubscriptionTier enum)
└─ Profile (PharmacyProfile value object)
    ├─ PharmacyName, Address, Description
    ├─ ContactName, ContactPhone
    ├─ SonarFCode (nullable)
    └─ OnboardingCompleted

BrandKit (per user)
├─ Colors (Primary, Secondary, Accent)
├─ Pharmacy (Name, License, Phone, Address)
├─ LogoBlobKey (nullable)
└─ RegulatoryFooter

Template (system / shared)
├─ Name, Description, Category, Tags
├─ ThumbnailBlobKey (nullable)
├─ CanvasJson (string; structured via shared-types CanvasDocument)
└─ IsPublished

Poster (per user)
├─ OwnerId
├─ Name, SourceTemplateId (nullable)
├─ ThumbnailBlobKey (nullable)
├─ CanvasJson
└─ Status (Draft / Published / Archived)

Campaign (per user)
├─ OwnerId, PosterId
├─ Name
├─ Channel (Email | Sms)
├─ Recipients (deduped, validated)
├─ Status (Draft / Sending / Sent / Stopped / Failed)
├─ SentCount, SentAtUtc
└─ Note (failure reason if applicable)

Video (per user)
├─ OwnerId
├─ Name, Topic, StyleTone
├─ Source (AiPipeline)
├─ Status (Pending / Rendering / Ready / Failed)
├─ ScriptSource (Ai / Guided / Custom)
├─ VisualStyle (Realistic / AiImagery)
├─ Description, CustomScript, VoiceId (optional)
├─ Aspect ("9:16"), DurationSeconds (15)
├─ BlobKey, ThumbnailBlobKey (set on Ready)
└─ Note (progress string like "Rendering… 42%", or failure reason)

RefreshToken (per session)
├─ UserId
├─ TokenHash (SHA-256 of raw token; raw never stored)
├─ ExpiresAtUtc, RevokedAtUtc
└─ ReplacedByTokenHash
```

## Shared TypeScript types

`packages/shared-types/src/` is the source of truth for all over-the-wire shapes. It's transpiled by Next.js (`transpilePackages: ["@acme/shared-types"]`) and mirrored as records in `Acme.Application` DTOs. Exports:

- `auth.ts` — `UserProfile`, `AuthResponse`, `RegisterRequest`, `LoginRequest`, `RefreshRequest`, `JwtClaims`
- `brand-kit.ts` — `BrandKit`, `BrandColors`, `PharmacyDetails`, `UpsertBrandKitRequest`
- `campaign.ts` — `Campaign`, `CampaignChannel`, `CampaignStatus`, `CreateCampaignRequest`
- `canvas.ts` — `CanvasDocument`, `Shape` (discriminated union: rect, circle, line, text, image, group), `Vector2`
- `poster.ts` — `Poster`, `PosterSummary`, `PosterStatus`, `CreatePosterRequest`, `UpdatePosterRequest`
- `profile.ts` — `Me`, `PharmacyProfile`, `UpsertProfileRequest`, `OnboardRequest`
- `subscription.ts` — `Plan` (incl. `maxVideosPerMonth`, `aiVideoEnabled`), `Usage` (incl. `videosThisMonth`), `CurrentSubscription`, `SubscriptionTier`, `UpgradeRequest`, `isUnlimited`
- `template.ts` — `Template`, `TemplateSummary`, `TemplateCategory`, `UpsertTemplateRequest`, `PagedResponse`
- `video.ts` — `Video`, `VideoStatus`, `VideoScriptSource`, `VideoVisualStyle`, `CreateVideoRequest`

Enums travel as strings (`"Free" | "Starter" | "Pro"`, `"Email" | "Sms"`, etc.). The .NET API uses `JsonStringEnumConverter` globally so request + response shapes match.

## Local Setup

### Prerequisites

- Node.js 20+
- pnpm 9+ (`npm i -g pnpm`)
- .NET 10 SDK
- Python 3.11+ (optional — only for the AI service)
- Docker Desktop

### 1. Clone & install

```bash
git clone <repo>
cd <repo>
cp .env.example .env
pnpm install
```

### 2. Boot infrastructure (Mongo + Azurite)

```bash
docker compose up -d mongo azurite
```

Mongo (port 27017) is your local Cosmos-DB-MongoDB-API stand-in. Azurite (port 10000) is the Azure Blob emulator used for brand logos and rendered videos.

### 3. Run each service

In separate terminals:

```bash
# .NET API → http://localhost:5050
cd apps/api/src/Acme.WebApi
dotnet run

# Next.js web → http://localhost:3000
cd apps/web
pnpm dev

# Python AI service → http://localhost:8000
# Handles both the canvas AI endpoints AND the video generation worker.
# Recommended path: run in Docker so ffmpeg + imagemagick + fonts are
# available (the video pipeline needs all three).
docker compose up -d --build ai-service
docker compose logs -f ai-service    # look for "✅ Video worker started"
```

Or run everything via Docker (recommended for the video feature):

```bash
docker compose up --build
```

The ai-service image is ~1.5-2 GB (ffmpeg + moviepy + fonts) so the first build takes a few minutes; subsequent rebuilds reuse cached layers.

### 4. Verify

- Web app: <http://localhost:3000>
- API health: <http://localhost:5050/health>
- API Swagger: <http://localhost:5050/swagger>
- AI service health: <http://localhost:8000/health>
- AI service Swagger: <http://localhost:8000/docs>
- Video worker startup log (should show `✅ Video worker started (poll=4.0s)`):

  ```bash
  docker compose logs ai-service | grep -i worker
  ```

> macOS users: the API runs on **5050** because Apple's AirPlay Receiver listens on 5000 by default. If you'd rather use 5000, disable AirPlay Receiver in System Settings → AirDrop & Handoff, then change the launch profile.

## Authentication Flow

1. Client `POST /api/auth/register` or `/api/auth/login` against the .NET API (via the BFF route handler).
2. .NET validates credentials, issues an HS256 JWT (15-min access token) **and** a 30-day rotating refresh token (raw value sent once; SHA-256 hash stored).
3. The BFF stores both as `httpOnly` cookies on the web origin:
   - `acme_access` — `path=/`, sent on every request.
   - `acme_refresh` — `path=/api/auth`, only sent to refresh endpoints.
4. Subsequent BFF calls attach `Authorization: Bearer <access>` from the cookie.
5. On 401, the BFF can `POST /api/auth/refresh`; the API issues a new access + refresh pair, marks the old refresh revoked, and detects reuse (presenting a revoked refresh wipes the user's entire refresh-token set).
6. The Python AI service validates the same JWT with the shared signing key.

Cookie security is controlled by `COOKIE_SECURE` env var on the web service: `false` for plain-HTTP localhost, `true` in production (HTTPS only).

## Configuration

`appsettings.json` keys (override via env vars using the `Section__Property` convention):

| Section | Keys |
|---|---|
| `Mongo` | `ConnectionString`, `Database` |
| `Jwt` | `Secret` (≥32 chars), `Issuer`, `Audience`, `ExpiryMinutes` |
| `Auth` | `RefreshTokenDays` |
| `AzureBlob` | `ConnectionString`, `Container` |
| `Sonar` | `BaseUrl`, `ApiKey`, `TimeoutSeconds`, `UseMock` |
| `Messaging` | `UseMock`, `Provider` (`smtp` or `mailjet`), `Smtp.{...}`, `Mailjet.{ApiKey,SecretKey,FromAddress,FromName,BaseUrl,SandboxMode,MaxRequestBytes}`, `Sms.{Provider,AccountSid,AuthToken,SenderId}` |
| `Cors` | `Origins.0`, `Origins.1`, … |
| `Social` | `Encryption.{ActiveVersion,Keys.N.{Version,Key}}`, `Meta.{AppId,AppSecret,GraphVersion}`, `Google.{ClientId,ClientSecret,AdsEnabled,AdsApiVersion,AdsValidateOnly,YouTubeEnabled,YouTubePrivacy}`, `LinkedIn.{ClientId,ClientSecret,ProfilesEnabled,PagesEnabled,ApiVersion}`, `TikTok.{ClientKey,ClientSecret,Enabled}`, `X.{ClientId,ClientSecret,Enabled}`, `Worker.{Enabled,PollSeconds,LeaseSeconds,HealthChecks}` — see `docs/SOCIAL_SETUP.md` |
| `Public` | `WebBaseUrl` (every emailed link and OAuth redirect is built on it), `ApiBaseUrl` |
| `Readiness` | `Token` — enables `/api/health/ready` for `pnpm smoke` |

Web-side env vars (`apps/web/.env.local.example`):

| Variable | Use |
|---|---|
| `API_INTERNAL_URL` | Server-side URL for BFF → API calls |
| `NEXT_PUBLIC_API_URL` | Browser-facing API URL (used by client code, none in our app today) |
| `COOKIE_SECURE` | `true` in production (HTTPS), `false` for local HTTP |

AI-service env vars (see `apps/ai-service/README.md` for the full table, signup links, and per-video cost estimates):

| Variable | Required? | Use |
|---|---|---|
| `Mongo__ConnectionString`, `Mongo__Database` | ✅ | Shares the .NET API's Mongo — worker mutates the same `videos` collection |
| `AzureBlob__ConnectionString`, `AzureBlob__Container` | ✅ | Shares the .NET API's Blob container — uploads to `videos/{ownerId}/{videoId}.{mp4,jpg}` |
| `ANTHROPIC_API_KEY` | ✅ | Claude — script generation. Default model: `claude-haiku-4-5-20251001` |
| `TAVILY_API_KEY` | ✅ | Research node — free tier gives 1,000 searches/month; each video uses 4 |
| `PEXELS_API_KEY` | ✅ | Stock B-roll — free, unlimited |
| `ELEVENLABS_API_KEY` | **required when hosted** | Set it and ElevenLabs narrates every video, with no fallback. Blank = Edge-TTS, which works locally and is refused from datacentres |
| `ELEVENLABS_DEFAULT_VOICE_ID` | optional | Voice to narrate with; Rachel if unset |
| `REPLICATE_API_TOKEN` | optional | Enables `visualStyle=AiImagery` (Pro-only). ~$0.024 / video at defaults |
| `AI_IMAGERY_{MAX_IMAGES_PER_JOB,BUDGET_PER_JOB_USD,COST_PER_IMAGE_USD}` | optional | Cost caps for the AI-imagery path |
| `VIDEO_WORKER_ENABLED`, `VIDEO_WORKER_POLL_SECONDS` | optional | Worker toggle + poll interval (default `true` / `4`) |

## Testing

`pnpm test` works from **any** directory in the repo — pnpm walks up to the
root `package.json` and runs from there, so you never have to work out the
relative path:

```bash
pnpm test           # every suite
pnpm test:api       # or: test:ai, test:web
```

### Before you tell anyone the release is out

```bash
pnpm smoke          # one call per door, against a RUNNING deployment
```

`pnpm test` proves the code is correct; `pnpm smoke` proves the deployment is
alive. They fail for different reasons and neither replaces the other — a green
`pnpm test` once told us nothing at all about a container quietly serving
five-day-old code, because the suites run against source and production runs an
image.

It signs in, creates a poster and exports it, sends one email, designs one
poster with AI, renders one short video, and pairs one screen — then deletes
what it made. Pennies per run, so it is safe to run every time. It fails on a
missing route, a render that can neither finish nor fail, or any error message
containing a traceback. See `docs/SMOKE.md`.

Which deployment it hits comes from the `# ----- Smoke test -----` block in
`.env`, which the runner reads itself. Anything already in the environment wins,
so `API_URL=https://… pnpm smoke` redirects a single run, and the report header
always names the file and URLs it used.

It signs in as its own seeded account (`smoke@sonar.local`, a pharmacy user on
the Pro plan) rather than the SuperAdmin — so its test data stays off the admin
account, and the plan-gated doors are actually open. The API creates it on
startup from the same `SMOKE_EMAIL` / `SMOKE_PASSWORD`.

From the repo root you can call the script directly instead. Note the `./`
means "in the current directory", so this one *does* depend on where you are:

```bash
cd <repo root> && ./scripts/test.sh
```

Prefer either of those over running the tools by hand. `dotnet test` picks up
whatever project is nearest, so from `apps/api/src/Acme.WebApi` it resolves to a
project with no test SDK: it prints a restore line, runs nothing, and exits 0 —
identical to a clean pass. The script targets `Acme.sln` explicitly and marks an
unrunnable suite SKIP rather than letting it read as green.

By hand, if you want a single suite:

```bash
cd apps/api/tests/Acme.Tests && dotnet test    # the test project itself
cd apps/api                  && dotnet test Acme.sln
cd apps/ai-service           && uv run pytest
cd apps/web                  && pnpm typecheck
```

The end-to-end smoke test is separate and needs a running stack:

```bash
./scripts/e2e/run.sh          # see scripts/e2e/README.md
```

## Deployment

Full deployment guides:

- **Azure App Service for Containers** — [`AZURE.md`](./AZURE.md). Ships all three services (api, web, ai-service) via `infra/azure/main.bicep` and `scripts/deploy.sh`. Preferred path.
- **Railway** — [`RAILWAY.md`](./RAILWAY.md). Cheaper for hobbyist workloads; note the ai-service needs at least ~1 GB RAM for MoviePy encoding.

### Azure — the one-liner

```bash
# 1. Provision (creates Cosmos, Blob, ACR, plan, and 3 App Services)
az deployment group create -g rg-sonar-pharmacyposter \
  --template-file infra/azure/main.bicep \
  --parameters @infra/azure/main.parameters.json \
  --parameters jwtSecret="$JWT_SECRET" \
               anthropicApiKey="$ANTHROPIC_API_KEY" \
               tavilyApiKey="$TAVILY_API_KEY" \
               pexelsApiKey="$PEXELS_API_KEY"

# 2. Build + push + restart all three services
./scripts/deploy.sh --pin

# Common flags:
#   ./scripts/deploy.sh --bump      # restart only, don't touch container config
#   SKIP_AI=1 ./scripts/deploy.sh   # skip the heavy 1.5 GB ai-service build
#   SKIP_API=1 SKIP_WEB=1 ./scripts/deploy.sh   # only ship the ai-service
```

The ai-service is provisioned pointing at Docker Hub (`infosonarinformatics/sonar-marketing-ai:dev` by default) with `alwaysOn=true` so the video worker doesn't idle-park. **Plan sizing:** don't run the ai-service on `B1` (1.75 GB RAM) — MoviePy encodes will OOM. Use `B2` (3.5 GB) or `P1v3`.

### Local → Azure mapping

| Local | Azure |
|-------|-------|
| `mongo` container | **Azure Cosmos DB for MongoDB (RU or vCore)** |
| `azurite` container | **Azure Blob Storage** (Hot tier, container `canvas-assets`) |
| `web` container | **App Service for Containers** (public) |
| `api` container | **App Service for Containers** (public) |
| `ai-service` container | **App Service for Containers**, `alwaysOn=true` |
| `JWT_SECRET`, Anthropic / Tavily / Pexels keys | **Azure Key Vault** reference (`@Microsoft.KeyVault(SecretUri=…)`) |
| Sonar API call | Real Sonar Backend URL via `Sonar:BaseUrl` |

## License

Timothy Babalola

# Project for Sonar Informatics. Healthcare Marketing Poster

Kindly ignore my Acme abbreviation :)

A SaaS for community pharmacies to design, share, and send professional marketing posters. Pharmacists pick a template, drop in their brand kit and details, edit on a drag-and-drop canvas, then export to PDF or send to patients by email / SMS — all gated by a tiered subscription model.

This document describes the architecture, domain model, and the local + production setups.

## Stack

| Layer | Technology |
|-------|------------|
| Web | Next.js 14 (App Router), TypeScript, Tailwind, shadcn/ui, Zustand, React-Konva, next-themes |
| API | .NET 8 Web API (Controllers) with Clean Architecture |
| AI Service | Python 3.11, FastAPI, LangChain (optional, hooks into Azure OpenAI) |
| Database | Azure Cosmos DB (MongoDB API) - local Mongo container in dev |
| Auth | JWT issued + validated by the .NET API, rotating refresh tokens |
| Storage | Azure Blob Storage (Azurite locally) for brand logos |
| PDF export | QuestPDF (Community license) via canvas-to-SVG pipeline |
| Email | SMTP via System.Net.Mail, with mock provider for dev |
| SMS | Pluggable `ISmsSender` (mock provider for dev, Twilio/Azure Communication ready) |
| External | `ISonarApiClient` calls the Sonar Backend to enrich profiles with FCode + ODS data; falls back to a deterministic mock when the upstream isn't configured |
| Orchestration | Docker Compose (dev), Railway / Azure Container Apps (prod) |

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
│   │       │   ├── pricing/              # Subscription plans
│   │       │   └── api/                  # BFF route handlers
│   │       ├── components/
│   │       │   ├── editor/               # Konva canvas + sidebars + dialogs
│   │       │   ├── landing/              # Marketing site sections
│   │       │   ├── site/                 # Global SiteHeader
│   │       │   └── ui/                   # shadcn primitives
│   │       ├── lib/                      # api client, canvas helpers
│   │       └── store/                    # Zustand stores
│   ├── api/                              # .NET 8 Web API
│   │   ├── src/
│   │   │   ├── Acme.Domain/              # Entities, value objects, errors
│   │   │   ├── Acme.Application/         # Use cases, DTOs, interfaces
│   │   │   ├── Acme.Infrastructure/      # Mongo, JWT, Blob, PDF, HTTP clients
│   │   │   └── Acme.WebApi/              # Controllers, middleware, DI
│   │   ├── tests/
│   │   └── Acme.sln
│   └── ai-service/                       # Python FastAPI + LangChain
│       ├── app/
│       └── pyproject.toml
├── packages/
│   └── shared-types/                     # Source-of-truth TypeScript contracts
├── docker-compose.yml
├── .env.example
├── RAILWAY.md
└── README.md
```

## Architecture

```
        Browser  ─https─▶  web (Next.js)
                                │
                                ├─ client-side fetch /api/* (BFF route handlers)
                                │                              │
                                │                              ▼
                                └─ server-side fetch ───────▶ api (.NET) ─────▶ MongoDB
                                                              │
                                                              ├─▶ Azure Blob (logos)
                                                              ├─▶ Sonar API (mock-friendly)
                                                              ├─▶ SMTP (mockable)
                                                              └─▶ SMS provider (mockable)
```

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
| AI generations | 3 / month | 50 / month | Unlimited |
| Campaign recipients | 0 | 500 / month | 5,000 / month |
| Watermark on exports | Yes | No | No |
| Custom templates | No | Yes | Yes |
| Email export | No | Yes | Yes |
| Team collaboration | No | No | Yes |
| Custom branding | No | No | Yes |
| Analytics dashboard | No | No | Yes |
| Priority AI | No | No | Yes |

Limits are enforced in `Acme.Application` services before mutations:

- `PosterService.CreateAsync` / `DuplicateAsync` → checks `Plans.For(user.Tier).MaxPosters` against `repo.CountAsync(...)` and returns `Forbidden` if exceeded.
- `CampaignService.CreateAndSendAsync` → checks `MaxCampaignRecipientsPerMonth` against `repo.CountRecipientsThisMonthAsync(...)`.

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

- **Email** — generates the poster as PDF (QuestPDF) and attaches it to a multi-recipient email via `IEmailSender` (SMTP in prod, mock that logs to console in dev).
- **SMS** — sends a short message via `ISmsSender` (mock by default; the contract is Twilio / Azure Communication Services ready — swap the impl).

Recipient validation is channel-aware: emails go through a strict regex, SMS strips non-digit / non-`+` characters and rejects under 7 characters. Limits are enforced before send. Stop button is enabled while a campaign is `Draft` or `Sending`.

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
- `subscription.ts` — `Plan`, `Usage`, `CurrentSubscription`, `SubscriptionTier`, `UpgradeRequest`, `isUnlimited`
- `template.ts` — `Template`, `TemplateSummary`, `TemplateCategory`, `UpsertTemplateRequest`, `PagedResponse`

Enums travel as strings (`"Free" | "Starter" | "Pro"`, `"Email" | "Sms"`, etc.). The .NET API uses `JsonStringEnumConverter` globally so request + response shapes match.

## Local Setup

### Prerequisites

- Node.js 20+
- pnpm 9+ (`npm i -g pnpm`)
- .NET 8 SDK
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

Mongo (port 27017) is your local Cosmos-DB-MongoDB-API stand-in. Azurite (port 10000) is the Azure Blob emulator used for brand logos.

### 3. Run each service

In separate terminals:

```bash
# .NET API → http://localhost:5050
cd apps/api/src/Acme.WebApi
dotnet run

# Next.js web → http://localhost:3000
cd apps/web
pnpm dev

# Python AI service → http://localhost:8000   (optional)
cd apps/ai-service
uv sync           # or: pip install -e .
uv run uvicorn app.main:app --reload
```

Or run everything via Docker:

```bash
docker compose up --build
```

### 4. Verify

- Web app: <http://localhost:3000>
- API health: <http://localhost:5050/health>
- API Swagger: <http://localhost:5050/swagger>
- AI service: <http://localhost:8000/docs>

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
| `Messaging` | `UseMock`, `Smtp.{Host,Port,EnableSsl,Username,Password,FromAddress,FromName}`, `Sms.{Provider,AccountSid,AuthToken,SenderId}` |
| `Cors` | `Origins.0`, `Origins.1`, … |

Web-side env vars (`apps/web/.env.local.example`):

| Variable | Use |
|---|---|
| `API_INTERNAL_URL` | Server-side URL for BFF → API calls |
| `NEXT_PUBLIC_API_URL` | Browser-facing API URL (used by client code, none in our app today) |
| `COOKIE_SECURE` | `true` in production (HTTPS), `false` for local HTTP |

## Testing

```bash
# .NET
cd apps/api && dotnet test

# Web
cd apps/web && pnpm test

# AI service
cd apps/ai-service && uv run pytest
```

## Deployment

See [`RAILWAY.md`](./RAILWAY.md) for a step-by-step Railway deployment guide that's tuned to stay within the £/$ free trial credit.

For Azure deployments, the compose stack maps cleanly onto:

| Local | Azure |
|-------|-------|
| `mongo` container | **Azure Cosmos DB for MongoDB (vCore or RU)** |
| `azurite` container | **Azure Blob Storage** (Hot tier) |
| `web` container | **Azure Container Apps** (or Static Web Apps for SSG) |
| `api` container | **Azure Container Apps** behind Front Door / APIM |
| `ai-service` container | **Azure Container Apps**, internal-only ingress |
| `JWT_SECRET` env var | **Azure Key Vault** reference (managed identity) |
| Sonar API call | Real Sonar Backend URL via `Sonar:BaseUrl` |

Suggested provisioning steps:

1. **Provision** with Bicep / Terraform: Cosmos DB account, Container Apps Environment, Container Registry (ACR), Key Vault, Storage account, Log Analytics workspace.
2. **Push images** with `az acr build` for each app under `apps/`.
3. **Configure secrets** in Key Vault, reference from Container Apps via managed identity (`secretRef`).
4. **Set env vars** per `.env.example` — replace local Mongo URI with the Cosmos connection string, set `AZURE_BLOB_*`, set `Sonar__BaseUrl` + `Sonar__ApiKey`, set `Messaging__UseMock=false` + real SMTP / SMS provider credentials.
5. **Wire ingress**: external HTTPS on `web` and `api`; internal-only on `ai-service`.
6. **Add managed identity** to `api` and grant `Storage Blob Data Contributor` on the Blob container and `Cosmos DB Built-in Data Contributor`.
7. **Front Door** in front of `web` and `api` for global routing, WAF, and TLS termination.

## License

Timothy Babalola

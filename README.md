# Project for Sonar Informatics. Healthcare Marketing Poster

Kindly ignore my Acme abbreviation :)

This are the technology used for the development of this project **Next.js 15**, **.NET 8 Clean Architecture**, **Python FastAPI + LangChain**, **Cosmos DB (MongoDB API)**, and **JWT auth issued by .NET**.

## Stack

| Layer | Technology |
|-------|------------|
| Web | Next.js 15 (App Router), TypeScript, Tailwind, shadcn/ui, Zustand, React-Konva |
| API | .NET 8 Web API (Controllers) + Clean Architecture |
| AI Service | Python 3.11, FastAPI, LangChain |
| Database | Azure Cosmos DB (MongoDB API) - local Mongo container in dev |
| Auth | JWT issued and validated by the .NET API |
| Storage | Azure Blob Storage |
| Orchestration | Docker Compose (dev), Azure Container Apps (prod) |

## Repository Layout



```
.
├── apps/
│   ├── web/                          # Next.js 15 frontend
│   ├── api/                          # .NET 8 Web API (Clean Architecture)
│   │   ├── src/
│   │   │   ├── Acme.Domain/          # Entities, value objects, domain errors
│   │   │   ├── Acme.Application/     # Use cases, DTOs, interfaces, CQRS
│   │   │   ├── Acme.Infrastructure/  # Cosmos, JWT, Blob, external services
│   │   │   └── Acme.WebApi/          # Controllers, middleware, DI composition
│   │   ├── tests/
│   │   └── Acme.sln
│   └── ai-service/                   # Python FastAPI + LangChain
│       ├── app/
│       └── pyproject.toml
├── packages/
│   └── shared-types/                 # Shared TypeScript types (canvas JSON state)
├── docker-compose.yml
├── .env.example
└── README.md
```

## Local Setup

### Prerequisites

- Node.js 20+
- pnpm 9+ (`npm i -g pnpm`)
- .NET 8 SDK
- Python 3.11+
- Docker Desktop

### 1. Clone & install

```bash
git clone <repo>
cd <repo>
cp .env.example .env
pnpm install
```

### 2. Boot infrastructure (Mongo + services)

```bash
docker compose up -d mongo
```

This starts a MongoDB 7 container that emulates the Cosmos DB MongoDB API surface for local development.

### 3. Run each service

In separate terminals:

```bash
# .NET API (http://localhost:5000)
cd apps/api/src/Acme.WebApi
dotnet run

# Next.js web (http://localhost:3000)
cd apps/web
pnpm dev

# Python AI service (http://localhost:8000)
cd apps/ai-service
uv sync           # or: pip install -e .
uv run uvicorn app.main:app --reload
```

Or run everything via Docker:

```bash
docker compose up --build
```

### 4. Verify

- Web app: http://localhost:3000
- API health: http://localhost:5000/health
- API Swagger: http://localhost:5000/swagger
- AI service: http://localhost:8000/docs

## Authentication Flow

1. Client `POST /api/auth/register` or `/api/auth/login` against the .NET API.
2. .NET validates credentials, issues a signed JWT (HS256, 1-hour expiry by default).
3. Client stores the token (httpOnly cookie via Next.js route handler recommended).
4. Subsequent requests include `Authorization: Bearer <token>`.
5. The Python AI service validates the same JWT using the shared signing key.

## Azure Deployment (Future)

The compose stack maps cleanly onto Azure managed services:

| Local | Azure |
|-------|-------|
| `mongo` container | **Azure Cosmos DB for MongoDB (vCore or RU)** |
| `web` container | **Azure Container Apps** (or **Static Web Apps** for SSG) |
| `api` container | **Azure Container Apps** behind **Front Door / APIM** |
| `ai-service` container | **Azure Container Apps** (with **Azure OpenAI** binding) |
| Local files | **Azure Blob Storage** (Hot tier) |
| `JWT_SECRET` env var | **Azure Key Vault** reference |

### Suggested deployment steps

1. **Provision** with Bicep / Terraform: Cosmos DB account (MongoDB API), Container Apps Environment, Container Registry (ACR), Key Vault, Storage account, Log Analytics workspace.
2. **Push images**: `az acr build` for each app under `apps/`.
3. **Configure secrets** in Key Vault and reference them from Container Apps via managed identity (`secretRef`).
4. **Set env vars** per `.env.example` - replace local Mongo URI with the Cosmos connection string, set `AZURE_BLOB_*`, etc.
5. **Wire ingress**: external HTTPS on `web` and `api`; internal-only on `ai-service` so it's reachable from `api` but not the public internet.
6. **Add managed identity** to `api` and grant `Storage Blob Data Contributor` on the Blob container and `Cosmos DB Built-in Data Contributor`.
7. **Front Door** in front of `web` and `api` for global routing, WAF, and TLS termination.

## Shared Types

Canvas JSON state types live in `packages/shared-types` and are consumed by:

- `apps/web` (TypeScript import)
- `apps/api` (mirrored as C# DTOs in `Acme.Application/Canvas/`)
- `apps/ai-service` (mirrored as Pydantic models in `app/schemas/canvas.py`)

The TypeScript definitions are the source of truth - keep them in sync when changing the canvas schema.

## Testing

```bash
# .NET
cd apps/api && dotnet test

# Web
cd apps/web && pnpm test

# AI service
cd apps/ai-service && uv run pytest
```

## License

Timothy Babalola

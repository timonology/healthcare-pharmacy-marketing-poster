# Azure deployment guide

This document walks through deploying the Pharmacy Poster monorepo to **Azure
App Service for Containers** with **Azure Cosmos DB for MongoDB** as the
database and **Azure Blob Storage** for assets. It's the lowest-friction PaaS
option for the stack and matches the Bicep template at
`infra/azure/main.bicep`.

The deployed shape:

```
+----------------+        +---------------+        +-----------------+
|  Browser       | -----> |  App Service  | -----> |  App Service    |
|                |        |  acme-web     |        |  acme-api       |
+----------------+        |  (Next.js)    |        |  (.NET 10)      |
                          +-------+-------+        +--------+--------+
                                  |                         |
                                  |                +--------v--------+
                                  |                |  Cosmos DB      |<-----+
                                  |                |  (MongoDB API)  |      |
                                  |                +-----------------+      |
                                  |                         |               |
                                  |                +--------v--------+      |
                                  +--------------> |  Blob Storage   |<-----+
                                                   |  canvas-assets  |      |
                                                   +-----------------+      |
                                                                            |
                                                   +-----------------+      |
                                                   |  App Service    |------+
                                                   |  acme-ai        |
                                                   |  (Python + ffmpeg)
                                                   |  video worker   |
                                                   +-----------------+
```

Three containerised App Services share one Linux App Service plan:

- **acme-web** (Next.js) — the only thing the browser speaks to.
- **acme-api** (.NET 10) — REST API, owns the Video entity, tier gating, blob
  URL signing. Writes new video jobs to Mongo.
- **acme-ai** (Python + FastAPI + ffmpeg + MoviePy) — hosts the LangGraph
  video agent. Polls the same Mongo `videos` collection every ~4s,
  renders through 5 stages (research → script → voice → visuals → editor),
  uploads MP4 + thumbnail to the same Blob container the API reads from.
  Never touched by the browser directly.

---

## 0. Prerequisites

- An Azure subscription with the right to create resource groups and Cosmos DB.
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) v2.60+
  signed in (`az login`).
- Docker installed locally (or use Azure Container Registry Tasks for builds in
  the cloud — see step 3 alt).
- A domain you can point at the web app later (optional but recommended).
- A 64-character random string for the JWT secret. Generate one with:
  ```bash
  openssl rand -base64 48
  ```

---

## 1. Create the resource group

```bash
LOCATION=uksouth          # or whatever region you prefer
RG=rg-sonar-pharmacyposter

az group create -n $RG -l $LOCATION
```

az deployment group create \
  --resource-group RSG_Web_Container \
  --template-file infra/azure/main.bicep \
  --parameters @infra/azure/main.parameters.json \
  --parameters jwtSecret="$JWT_SECRET"

---

## 2. Provision the infrastructure with Bicep

The Bicep template provisions everything in one shot — ACR, Cosmos, Storage,
the App Service plan, and the two App Services.

```bash
JWT_SECRET=$(openssl rand -base64 48)

az deployment group create \
  --resource-group $RG \
  --template-file infra/azure/main.bicep \
  --parameters @infra/azure/main.parameters.json \
  --parameters jwtSecret="$JWT_SECRET"
```

Take note of the outputs at the end — you'll see `apiHostname`, `webHostname`,
`acrLoginServer`, etc.

> **Tip:** Edit `infra/azure/main.parameters.json` to change the resource name
> prefix or App Service SKU (`B1` is fine for development; bump to `P1v3` for
> production traffic).

> **Bicep replaces every app setting on each deploy.** Anything set by hand in
> the portal is removed. Pass secrets as parameters every time, including the
> social ones: `socialEncryptionKey`, `metaAppId`/`metaAppSecret`,
> `googleClientId`/`googleClientSecret`/`googleAdsDeveloperToken`,
> `linkedInClientId`/`linkedInClientSecret`/`linkedInPagesEnabled`,
> `tikTokClientKey`/`tikTokClientSecret`, `xClientId`/`xClientSecret`, plus
> `readinessToken` and `clientIpSigningKey`. The full list and what each does
> is in `docs/PRODUCTION_CHECKLIST.md`; platform setup in `docs/SOCIAL_SETUP.md`.

If you'd rather click through the portal instead of using Bicep, the equivalent
manual steps are at the end of this document.

---

## 3. Build and push the container images

The Bicep template enables admin credentials on the ACR so we can push images
directly with the CLI.

```bash
ACR=pharmposteracr       # matches namePrefix + "acr" from Bicep
az acr login -n $ACR

# .NET 10 API
docker build -t $ACR.azurecr.io/acme-api:latest ./apps/api
docker push  $ACR.azurecr.io/acme-api:latest

# Next.js web (the Docker context is the monorepo root)
docker build -f apps/web/Dockerfile -t $ACR.azurecr.io/acme-web:latest .
docker push  $ACR.azurecr.io/acme-web:latest

# Python ai-service (ffmpeg + moviepy + edge-tts + langgraph — this
# image is ~1.5-2 GB. First build takes ~5-10 min; subsequent builds
# reuse the base + apt layer.)
docker build -f apps/ai-service/Dockerfile -t $ACR.azurecr.io/acme-ai:latest ./apps/ai-service
docker push  $ACR.azurecr.io/acme-ai:latest
```

> **Alt:** Use `az acr build` to skip the local Docker step:
> ```bash
> az acr build -r $ACR -t acme-api:latest ./apps/api
> az acr build -r $ACR -t acme-web:latest -f apps/web/Dockerfile .
> az acr build -r $ACR -t acme-ai:latest  ./apps/ai-service
> ```

> **Or use `scripts/deploy.sh`** — the repo ships a wrapper that builds all
> three images with a `:dev` moving tag and a `:<sha>` immutable tag, pushes
> to Docker Hub, and (with `--pin` or `--bump`) points the App Services at
> the new images. See `--help` for options; `SKIP_AI=1` lets you skip the
> heavy ai-service build when you only touched the .NET or web code.

After the first push, restart the App Services so they pull the new images:

```bash
az webapp restart -g $RG -n pharmposter-api
az webapp restart -g $RG -n pharmposter-web
az webapp restart -g $RG -n pharmposter-ai
```

---

## 4. Verify the API

```bash
curl https://pharmposter-api.azurewebsites.net/health
# {"status":"ok","time":"2026-..."}
```

If you see `Mongo connection refused` or a 500 instead, jump to the
**Troubleshooting** section.

The API's first startup runs `MongoIndexInitializer` and seeds the template
library — it can take 30-60 seconds on the very first container pull.

---

## 5. Verify the web app

Open `https://pharmposter-web.azurewebsites.net`. You should see the landing
page. Register a new account, complete the three-step onboarding wizard, and
land on the dashboard.

If you get 502s, check `WEBSITES_PORT` is `3000` for the web app and `8080`
for the API. The Bicep template sets these correctly; portal-driven deploys
sometimes miss them.

---

## 6. Custom domain + HTTPS (optional but recommended)

1. In the web App Service → **Custom domains** → **Add custom domain** and
   follow the verification steps (TXT + CNAME or A records on your DNS).
2. App Service issues a free managed TLS certificate after verification.
3. Once the custom domain is live, update the API's `Cors__Origins__0`
   application setting to the new origin and **restart the API**:

   ```bash
   az webapp config appsettings set -g $RG -n pharmposter-api \
     --settings Cors__Origins__0=https://app.your-domain.example
   az webapp restart -g $RG -n pharmposter-api
   ```

4. Also update the web app's `NEXT_PUBLIC_API_URL` if you give the API a
   custom domain too.

---

## 7. Application settings reference

These are pre-populated by the Bicep template. Change them in
**App Service → Configuration → Application settings**, then restart.

### `pharmposter-api`

| Key | Value |
|-----|-------|
| `WEBSITES_PORT` | `8080` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Mongo__ConnectionString` | (Cosmos primary connection string) |
| `Mongo__Database` | `acme` |
| `Jwt__Secret` | 64-char random string |
| `Jwt__Issuer` | `acme-api` |
| `Jwt__Audience` | `acme-clients` |
| `Jwt__ExpiryMinutes` | `60` |
| `Auth__RefreshTokenDays` | `30` |
| `AzureBlob__ConnectionString` | (Storage account connection string) |
| `AzureBlob__Container` | `canvas-assets` |
| `Cors__Origins__0` | `https://pharmposter-web.azurewebsites.net` |
| `Sonar__UseMock` | `true` (until you have real Sonar credentials) |
| `Messaging__UseMock` | `false` once you wire up SendGrid / SMTP |

### `pharmposter-web`

| Key | Value |
|-----|-------|
| `WEBSITES_PORT` | `3000` |
| `NODE_ENV` | `production` |
| `COOKIE_SECURE` | `true` |
| `NEXT_PUBLIC_API_URL` | `https://pharmposter-api.azurewebsites.net` |
| `API_INTERNAL_URL` | `https://pharmposter-api.azurewebsites.net` |

### `pharmposter-ai`

The Python video-agent service. Boots FastAPI + starts an async worker that
polls Mongo for `Pending` videos and runs the 5-stage LangGraph pipeline.

**Image source:** the Bicep template provisions this App Service pointed at
`infosonarinformatics/sonar-marketing-ai:dev` on Docker Hub (matches what
`deploy.sh` builds and pushes). If your Docker Hub repo is **private**,
pass `dockerHubUsername=...` and `dockerHubPassword=...` at `az deployment
group create` time so App Service can authenticate. If it's public, leave
both blank.

| Key | Value |
|-----|-------|
| `WEBSITES_PORT` | `8000` |
| `Mongo__ConnectionString` | (same value as the API) |
| `Mongo__Database` | `acme` |
| `AzureBlob__ConnectionString` | (same value as the API) |
| `AzureBlob__Container` | `canvas-assets` |
| `ANTHROPIC_API_KEY` | Claude — required for script generation |
| `ANTHROPIC_MODEL` | `claude-haiku-4-5-20251001` (default) |
| `TAVILY_API_KEY` | Web-search — required for the research node |
| `PEXELS_API_KEY` | Stock B-roll — required for the visuals node |
| `ELEVENLABS_API_KEY` | **Required.** Blank means Edge-TTS, which Microsoft does not serve to datacentres — every render dies at the voice stage. No fallback: see docs/AI_SETUP.md |
| `ELEVENLABS_DEFAULT_VOICE_ID` | Optional; the account's Rachel voice if unset |
| `REPLICATE_API_TOKEN` | Optional; blank disables AI imagery (Pro-only) |
| `REPLICATE_FLUX_MODEL` | `black-forest-labs/flux-schnell` (default) |
| `AI_IMAGERY_MAX_IMAGES_PER_JOB` | `8` |
| `AI_IMAGERY_BUDGET_PER_JOB_USD` | `0.05` |
| `AI_IMAGERY_COST_PER_IMAGE_USD` | `0.003` |
| `VIDEO_WORKER_ENABLED` | `true` |
| `VIDEO_WORKER_POLL_SECONDS` | `4` |

The Bicep template also sets `alwaysOn=true` on this app because the video
worker is long-lived — without it, App Service would idle-park the container
after 20 minutes and jobs would sit in `Pending` until the next incoming
request.

**Plan size warning:** the ai-service runs FFmpeg + MoviePy encodes and
should not be on `B1` (1.75 GB RAM). Bump to `B2` (3.5 GB) or `P1v3`
before running real workloads — B1 will OOM-kill the container mid-render
on longer clips. You can either upgrade the shared plan or move just the
ai-service to its own bigger plan.

Sensitive values (`Jwt__Secret`, Storage keys, `ANTHROPIC_API_KEY`,
`TAVILY_API_KEY`, `PEXELS_API_KEY`, `ELEVENLABS_API_KEY`,
`REPLICATE_API_TOKEN`, SMTP credentials) should ideally be sourced from
**Azure Key Vault references** instead of being stored inline —
`@Microsoft.KeyVault(SecretUri=https://your-kv.vault.azure.net/secrets/anthropic-api-key/)`.

---

## 8. Wire up real email sending (SendGrid)

The default deployment leaves `Messaging__UseMock=true` so campaigns don't try
to send. When you're ready:

1. Provision a SendGrid account (Azure Marketplace has a free tier).
2. In the SendGrid dashboard, create an API key.
3. Set on `pharmposter-api`:

   ```bash
   az webapp config appsettings set -g $RG -n pharmposter-api --settings \
     Messaging__UseMock=false \
     Messaging__Smtp__Host=smtp.sendgrid.net \
     Messaging__Smtp__Port=587 \
     Messaging__Smtp__EnableSsl=true \
     Messaging__Smtp__User=apikey \
     Messaging__Smtp__Password=YOUR_SENDGRID_API_KEY \
     Messaging__Smtp__FromAddress=no-reply@your-domain.example \
     Messaging__Smtp__FromName="Pharmacy Poster"
   ```

4. Restart the API.

---

## 9. CI/CD with GitHub Actions

The simplest pipeline: build images on push to `main`, push to ACR, restart
both App Services.

```yaml
# .github/workflows/deploy.yml
name: Deploy to Azure

on:
  push:
    branches: [ main ]

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: azure/login@v2
        with:
          creds: ${{ secrets.AZURE_CREDENTIALS }}

      - name: Build & push API
        run: |
          az acr build -r pharmposteracr -t acme-api:${{ github.sha }} -t acme-api:latest ./apps/api

      - name: Build & push Web
        run: |
          az acr build -r pharmposteracr -t acme-web:${{ github.sha }} -t acme-web:latest -f apps/web/Dockerfile .

      - name: Restart App Services
        run: |
          az webapp restart -g rg-pharmacyposter -n pharmposter-api
          az webapp restart -g rg-pharmacyposter -n pharmposter-web
```

Add the secret `AZURE_CREDENTIALS` (a service-principal JSON from
`az ad sp create-for-rbac --sdk-auth`) to the repo. For zero-downtime deploys,
swap to deployment slots and use `az webapp deployment slot swap` instead of
`restart`.

---

## 10. Troubleshooting

**`Mongo connection refused`** — Cosmos DB for MongoDB takes 2-3 minutes to
finish provisioning. Run `az cosmosdb show -g $RG -n pharmposter-cosmos
--query provisioningState`. It should say `Succeeded`. After that, copy the
primary connection string (`az cosmosdb keys list -g $RG -n pharmposter-cosmos
--type connection-strings`) and confirm `Mongo__ConnectionString` matches.

**`502 Bad Gateway` from App Service** — almost always `WEBSITES_PORT`
doesn't match the port the container actually listens on. Web = `3000`, API =
`8080`. Set it on **Configuration → Application settings** and restart.

**Web responds but API requests 401** — the API thinks the JWT is invalid.
Confirm `Jwt__Secret`, `Jwt__Issuer`, and `Jwt__Audience` are identical to
what the API was using when the tokens were issued. If you rotated the
secret, log out and back in.

**`Cookies__not__set`** — `COOKIE_SECURE=true` is required behind App
Service (always HTTPS). The web app refuses to write cookies if the request
arrived over plain HTTP. Make sure `httpsOnly` is on (Bicep sets it).

**CORS errors in the browser** — the API only accepts origins listed in
`Cors__Origins__0`/`__1`/etc. Add your real hostname there and restart.

**Persistence inconsistencies between sessions** — the API now logs
`Patient created owner=...` / `Patient list for owner=...`. Stream logs with
`az webapp log tail -g $RG -n pharmposter-api` and compare the owner IDs
between create and list.

---

## Manual portal-only alternative (if you're not using Bicep)

1. **Container Registry** — Create → SKU Basic → enable admin user.
2. **Cosmos DB for MongoDB** — Create → API: Azure Cosmos DB for MongoDB →
   server version 7.0 → throughput 400 RU/s shared. Create a database `acme`.
3. **Storage account** — Create → Standard LRS → enable HTTPS only → in
   "Containers" add `canvas-assets`.
4. **App Service Plan** — Create → Linux → B1.
5. **Web App (API)** — Create → Docker → Linux → image: `acme-api:latest`
   from your ACR. After creation, fill in the Application settings from
   section 7 above.
6. **Web App (Web)** — Same as above but with the `acme-web:latest` image
   and the web app settings.
7. Push images to ACR (section 3) and restart both apps.

Bicep is faster and reproducible — strongly prefer it once you've done this
once.

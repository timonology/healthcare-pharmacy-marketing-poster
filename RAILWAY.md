# Deploying to Railway

A step-by-step guide to get this monorepo running on Railway from a GitHub repo. Tuned to stay on the **$5/month Hobby trial credit** by deploying only the parts that aren't cosmetic.

## What gets deployed

| Service | What it is | Deploy on Railway? |
|---|---|---|
| `web` | Next.js front-end | Yes |
| `api` | .NET 8 Web API | Yes |
| MongoDB | Database | Yes (Railway plugin) |
| `ai-service` | Optional Python LangChain helper | **No** — needs Azure OpenAI keys you don't have |
| Azurite / Azure Blob | Logo storage for brand kit | **No** — logo upload won't work, everything else does |

The free trial gives you $5 of compute, which comfortably covers Mongo + 2 services with light traffic.

## Before you start

1. Push the project to GitHub. Make sure `.env` is **not** committed (it's already in `.gitignore`).
2. Sign up at [railway.app](https://railway.app) with your GitHub account. Click "Verify with GitHub" so Railway can read your repos.
3. Generate a strong JWT secret you'll paste in later. Anything random and 32+ characters works:
   ```bash
   openssl rand -hex 32
   ```
   Save the output somewhere — you'll need it twice.

## Step 1. Create the project and add MongoDB

1. Click **New Project** → **Deploy from GitHub repo** → pick your repo.
   Railway will start trying to deploy something. Cancel/skip that for now; you'll wire each service manually.
2. Inside the project, click **+ New** → **Database** → **MongoDB**.
   Wait for it to go green. The plugin exposes a few variables; the one you'll use is `MONGO_URL`.

## Step 2. Add the API service

1. Click **+ New** → **GitHub Repo** → pick the same repo. (Yes, the same repo — Railway runs each service from a different folder of it.)
2. Click the new service. Open **Settings** and set:

   | Field | Value |
   |---|---|
   | Service name | `api` |
   | Root Directory | `apps/api` |
   | Watch Paths | `apps/api/**` |
   | Builder | Dockerfile (auto-detected from `apps/api/Dockerfile`) |

3. Open **Variables** and add these one by one:

   ```
   ASPNETCORE_ENVIRONMENT = Production
   Mongo__ConnectionString = ${{MongoDB.MONGO_URL}}
   Mongo__Database         = acme
   Jwt__Secret             = <paste your openssl secret here>
   Jwt__Issuer             = acme-api
   Jwt__Audience           = acme-clients
   Jwt__ExpiryMinutes      = 60
   Auth__RefreshTokenDays  = 30
   ```

   The `${{MongoDB.MONGO_URL}}` syntax tells Railway to inject the live Mongo connection string from the plugin you created in Step 1.

4. Open **Settings → Networking → Generate Domain**. Railway hands you a URL like `acme-api-production.up.railway.app`. Copy it — you'll need it for the web service.

5. Watch **Deploy logs**. You should see the .NET app boot, run the index initializer, seed templates, and then `Now listening on: http://0.0.0.0:PORT`. Click the domain — you'll see Swagger at `/swagger` and `/health` returns `ok`.

## Step 3. Add the web service

1. **+ New** → **GitHub Repo** → same repo again.
2. Open **Settings**:

   | Field | Value |
   |---|---|
   | Service name | `web` |
   | Root Directory | *(leave blank — needs the whole repo because the web build pulls in `packages/shared-types`)* |
   | Dockerfile Path | `apps/web/Dockerfile` |
   | Watch Paths | `apps/web/**`, `packages/**` |

3. **Variables**:

   ```
   NEXT_PUBLIC_API_URL = https://<your-api-domain>.up.railway.app
   API_INTERNAL_URL    = https://<your-api-domain>.up.railway.app
   COOKIE_SECURE       = true
   ```

   - `NEXT_PUBLIC_API_URL` is what the browser uses for client-side fetches.
   - `API_INTERNAL_URL` is what the Next.js BFF (server-side) uses. On Railway, public HTTPS is the simplest thing that works. (You can switch to Railway's private networking later — see "Optional optimizations" below.)
   - `COOKIE_SECURE=true` because Railway serves the app over HTTPS.

4. **Settings → Networking → Generate Domain**. You get a URL like `acme-web-production.up.railway.app`. Open it in your browser.

5. The first request triggers the build. Watch **Deploy logs** — Next.js compiles, then the container starts listening. When it's green, your site is live.

## Step 4. Update API CORS for the web URL

The API has a `Cors:Origins` setting that defaults to `localhost:3000` and `localhost:3030`. Add your live web domain:

1. Go to the **api** service → **Variables**.
2. Add:
   ```
   Cors__Origins__0 = https://<your-web-domain>.up.railway.app
   ```
   (Yes, the double underscore + number. ASP.NET Core configuration uses `__` for nesting and indexed names for arrays.)
3. Railway redeploys automatically.

## Step 5. Try it

Visit the web domain. **Create account** → fill in email/password → you should be redirected straight to **Brand Kit**. Sign out, sign back in. Click **Templates** — six pre-seeded templates appear with previews. Click **Use template** to drop one into your posters list and open the editor.

The auto-save badge in the editor flips between *Saving* and *Saved* — every change goes back to Cosmos via the .NET API.

What won't work without extra setup:

- **Logo upload** in the brand kit. Needs Azure Blob (or another object store). Everything else in the brand-kit page works.
- **AI suggestions** in the canvas. Needs the Python service + Azure OpenAI key.

## Free-tier survival tips

- **Stay on Hobby**, not Pro. Hobby includes $5/month. Pro is metered.
- **Pause when not in use**. From the Railway dashboard, click each service → **Settings → Pause Service**. Paused services don't accrue compute.
- **Skip the AI service**. It's the heaviest container in the stack and adds no value without an Azure OpenAI subscription.
- **Use Mongo Atlas free tier instead of Railway Mongo** if you hit the credit cap. Atlas's M0 cluster is free forever; just paste its connection string into `Mongo__ConnectionString` and uninstall the Railway Mongo plugin.
- **Watch the Usage tab**. Railway shows live spend per service. The web container uses the most because Next builds are CPU-heavy; that's a one-time cost per deploy, not ongoing.
- **Limit log volume**. The .NET app logs every request at Info level. Drop to Warning in `appsettings.json` if you hit the log retention quota.

## Optional optimizations

### Use Railway's private network for BFF→API

Railway services on the same project can talk over a private network using `<service-name>.railway.internal` hostnames. To switch:

1. On the **web** service → **Variables**, change `API_INTERNAL_URL` to:
   ```
   API_INTERNAL_URL = http://api.railway.internal:8080
   ```
   (Replace `api` with whatever you named the service. Port stays 8080 because that's the container's `EXPOSE`d port; Railway maps the public-facing $PORT separately.)
2. Save. The web service redeploys.

This avoids the public round-trip on every server-side fetch and is faster + cheaper.

### Map a custom domain

In **Settings → Networking → Custom Domain**, add `app.yourdomain.com`. Railway gives you the CNAME to add to your DNS. Update `Cors__Origins__0` to match.

### CI: only redeploy when relevant files change

Already configured via the Watch Paths above. Editing the README won't trigger a build.

## Troubleshooting

**Build succeeds, container exits immediately.**
Open **Deploy logs**. If the .NET app says "connection refused" to Mongo, double-check `Mongo__ConnectionString = ${{MongoDB.MONGO_URL}}` — the curly-brace syntax is Railway-specific and must be exact.

**Login works but every page bounces back to /login.**
You probably forgot `COOKIE_SECURE=true` on the web service. Browsers drop secure cookies on HTTP; Railway serves over HTTPS so that flag is required. Add it, redeploy.

**CORS errors in the browser console.**
Add your web domain to `Cors__Origins__0` on the api service. The default config only knows about localhost.

**API returns 404 for /api/...**
The web service's `API_INTERNAL_URL` is wrong. Open the api service domain directly — if `/swagger` loads, the URL works. Re-paste it into the web variables, with `https://` and **no** trailing slash.

**"Application failed to respond" page.**
The container is up but isn't binding to `$PORT`. Make sure both Dockerfiles still contain the `${PORT:-…}` shell expansion in their final command.

## Mental model recap

```
   Browser  ──HTTPS──▶  web (Next.js)
                            │
                            ├── client-side fetch → /api/* (BFF route handlers)
                            │                           │
                            │                           ▼
                            └─ server-side fetch ─▶  api (.NET) ──▶ MongoDB
```

Every browser request hits the **web** container. Auth and CRUD calls go to Next.js BFF route handlers (which carry the JWT cookie), and those route handlers proxy to the **api** container. The browser never talks to the API directly. That's why you only need one CORS origin (the web domain) and why `COOKIE_SECURE=true` is enough to lock down auth in production.

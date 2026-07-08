# Deploying to Railway

A step-by-step guide for deploying this monorepo to Railway from a GitHub repo. Designed to stay on the **$5 / month Hobby trial credit** by only deploying the parts that aren't cosmetic.

## What gets deployed

| Service | What it is | Deploy on Railway? |
|---|---|---|
| `web` | Next.js front-end | Yes |
| `api` | .NET 8 Web API | Yes |
| MongoDB | Database (pharmacy profiles, posters, campaigns) | Yes (Railway plugin) |
| `ai-service` | Optional Python LangChain helper | **No** — needs Azure OpenAI keys |
| Azurite / Azure Blob | Logo storage for brand kit | **No** — brand-kit logo upload simply won't work in this deploy; the rest of the app does |
| Sonar Backend | Pharmacy FCode enrichment | Stays mocked (`Sonar:UseMock=true`) until you have real Sonar credentials |
| SMTP | Email campaigns | Stays mocked (`Messaging:UseMock=true`) — flip when you wire a real provider |

The free trial gives you $5 of compute, comfortably covering Mongo + 2 small services with light traffic.

## Before you start

1. Push the project to GitHub (`.env` is already ignored).
2. Sign up at [railway.app](https://railway.app) with GitHub.
3. Generate a strong JWT secret:
   ```bash
   openssl rand -hex 32
   ```
   Save the output — you'll paste it as `Jwt__Secret`.

## Step 1. Create the project and add MongoDB

1. **New Project** → **Deploy from GitHub repo** → pick your repo. Cancel the auto-deploy that starts; you'll wire each service manually.
2. Inside the project, **+ New** → **Database** → **MongoDB**. Wait for it to go green. The plugin exposes `MONGO_URL`.

## Step 2. Add the API service

1. **+ New** → **GitHub Repo** → pick the same repo again.
2. Open the new service → **Settings**:

   | Field | Value |
   |---|---|
   | Service name | `api` |
   | Root Directory | `apps/api` |
   | Watch Paths | `apps/api/**` |
   | Builder | Dockerfile (auto-detected from `apps/api/Dockerfile`) |
   | Build Command | *(leave blank — the Dockerfile handles it)* |
   | Start Command | *(leave blank — the Dockerfile's ENTRYPOINT runs `dotnet`)* |

3. Open **Variables** and add these:

   ```
   ASPNETCORE_ENVIRONMENT  = Production
   Mongo__ConnectionString = ${{MongoDB.MONGO_URL}}
   Mongo__Database         = acme
   Jwt__Secret             = <paste your openssl secret>
   Jwt__Issuer             = acme-api
   Jwt__Audience           = acme-clients
   Jwt__ExpiryMinutes      = 15
   Auth__RefreshTokenDays  = 30

   Sonar__UseMock          = true

   Messaging__UseMock      = true
   Messaging__Smtp__FromAddress = no-reply@yourdomain.com
   Messaging__Smtp__FromName    = Pharmacy Poster
   ```

   The `${{MongoDB.MONGO_URL}}` syntax tells Railway to inject the Mongo plugin's connection string at deploy time. (Replace `MongoDB` if your plugin service has a different display name.)

4. **Settings → Networking → Generate Domain**. Copy the URL — you'll paste it into the web service.

5. Tail **Deploy logs**. You'll see the .NET app boot, run the index initializer, seed templates, then `Now listening on: http://0.0.0.0:PORT`. Visit `/swagger` to verify.

## Step 3. Add the web service

1. **+ New** → **GitHub Repo** → same repo again.
2. **Settings**:

   | Field | Value |
   |---|---|
   | Service name | `web` |
   | Root Directory | *(leave blank — needs the whole repo for `packages/shared-types`)* |
   | Dockerfile Path | `apps/web/Dockerfile` |
   | Watch Paths | `apps/web/**`, `packages/**` |
   | Build Command | *(blank)* |
   | Start Command | *(blank)* |

3. **Variables**:

   ```
   NEXT_PUBLIC_API_URL = https://<your-api-domain>.up.railway.app
   API_INTERNAL_URL    = https://<your-api-domain>.up.railway.app
   COOKIE_SECURE       = true
   ```

   - `NEXT_PUBLIC_API_URL` is the URL the browser would use (the BFF pattern means client code rarely touches it directly).
   - `API_INTERNAL_URL` is the URL the Next.js BFF (server-side) calls. Public HTTPS is the simplest thing that works; see *Optional optimizations* below for switching to Railway private networking.
   - `COOKIE_SECURE=true` because Railway serves the app over HTTPS, and browsers drop `Secure` cookies on plain HTTP.

4. **Settings → Networking → Generate Domain**.

5. **Settings → Networking → Target Port** — confirm this matches the port Next is binding to (printed in the logs as `Local: http://localhost:<PORT>`). If they don't match you get 502s. Setting it explicitly to `8080` is safest because that's what Railway's `$PORT` typically is, and the Dockerfile's `CMD` binds to `$PORT`.

## Step 4. Update API CORS for the web URL

In the **api** service → **Variables**, add:

```
Cors__Origins__0 = https://<your-web-domain>.up.railway.app
```

The double-underscore + index syntax is how ASP.NET Core configuration represents `Cors:Origins[0]` in env vars.

## Step 5. Try it

1. Register an account → you should be sent to `/onboarding`.
2. Fill the three steps (Pharmacy → Contact → Plan) → land on `/dashboard`.
3. The dashboard shows your pharmacy name, plan card, recent posters (empty), campaigns card (empty).
4. **Browse templates** → pick one → **Use template** → poster opens in the editor with your brand info already applied via *Apply pharmacy info*.
5. Editor *Share* button opens the email modal; *PDF* downloads via QuestPDF; *PNG* exports the Konva stage.

What won't work without extra setup on this Railway deploy:

- **Logo upload in the brand kit** — needs a real Azure Blob account.
- **AI suggestions on the canvas** — needs the Python service + Azure OpenAI key.
- **Real Sonar FCode lookup** — `Sonar__UseMock=true` returns a deterministic stub; flip to `false` and add `Sonar__BaseUrl` + `Sonar__ApiKey` once you have credentials.
- **Real email/SMS campaigns** — `Messaging__UseMock=true` logs the dispatch to stdout; flip and add SMTP / Twilio / Azure Communication credentials when ready.

## Free-tier survival tips

- Stay on the **Hobby plan** ($5 / month included). Pro is metered.
- **Pause idle services**. Each service has a **Settings → Pause Service** switch.
- **Use MongoDB Atlas free tier** instead of Railway's Mongo plugin if you start running out of credit — Atlas's M0 cluster is free forever. Just paste its connection string into `Mongo__ConnectionString` and uninstall the plugin.
- **Watch the Usage tab**. The web container's compute spikes during builds (Next type-checks); deploys after small TSX edits should reuse cached layers.
- **Limit log volume**. The .NET app logs every request at Info level — drop to Warning in `appsettings.json` when you hit log-retention limits.

## Optional optimizations

### Use Railway's private network for BFF → API

Railway services on the same project can talk via `*.railway.internal` hostnames over an internal IPv6 network. Change `API_INTERNAL_URL` on the web service to:

```
API_INTERNAL_URL = http://api.railway.internal:${{api.PORT}}
```

(Replace `api` with the exact API service name in your project; `${{api.PORT}}` is interpolated at deploy time to whatever port Railway gave the api container.)

This avoids the public-edge round-trip on every server-side fetch.

### Map a custom domain

In **Settings → Networking → Custom Domain**, add `app.yourdomain.com`. Railway gives you the CNAME to set up. Don't forget to add the new domain to `Cors__Origins__0` on the api side.

## Troubleshooting

**`401` on every endpoint after login.**
You forgot `COOKIE_SECURE=true` on the web service, so the browser dropped the `Secure` cookie. Add it, redeploy.

**Web shows 502 Bad Gateway, container is running.**
Settings → Networking → Target Port doesn't match what Next bound to. Check the deploy logs for `Local: http://localhost:<port>` and set the target to that number.

**`400 Bad Request` on `/api/me/onboard`.**
Make sure you redeployed the API after this commit — older builds didn't have `JsonStringEnumConverter` registered globally, which is what lets the API parse `"Free"` from the client.

**API logs say "Connection refused localhost:27017".**
`Mongo__ConnectionString` isn't set or doesn't match the Mongo plugin's service name. The exact value should be `${{MongoDB.MONGO_URL}}` — the curly-brace syntax is Railway-specific.

**Can't connect to Mongo from `mongosh` on your laptop.**
Railway exposes **two** Mongo URLs and they're not interchangeable:

| Variable | Hostname looks like | Use it from |
|---|---|---|
| `MONGO_URL` | `mongodb://mongo:pass@mongo.railway.internal:27017` | Other Railway services in the **same project** |
| `MONGO_PUBLIC_URL` | `mongodb://mongo:pass@containers-us-west-XX.railway.app:7XXXX` | Your laptop, Compass, mongosh — anything outside Railway |

In your project: **MongoDB plugin → Variables tab** → copy `MONGO_PUBLIC_URL`. Then:

```bash
mongosh "$(pbpaste)"            # macOS: paste straight from clipboard
# or
mongosh "mongodb://mongo:THEPASSWORD@containers-us-west-XX.railway.app:7XXXX"
```

Common gotchas:

- **Wrong URL**: trying `mongo.railway.internal` from your laptop never resolves — it's only valid inside Railway's network.
- **Password contains special characters** (`@`, `:`, `/`, `#`): URL-encode them. Railway-generated passwords sometimes include `+` or `=`. `python3 -c "import urllib.parse;print(urllib.parse.quote('YOUR_PASS', safe=''))"`.
- **Plugin deprecation**: if you provisioned Mongo when Railway offered the legacy "MongoDB" plugin, it may have been deprecated. In the project, delete the old plugin and add **New → Database → MongoDB** (the current one with a persistent volume). Re-attach `Mongo__ConnectionString = ${{MongoDB.MONGO_URL}}` on the api service.
- **Firewall / VPN**: corporate networks sometimes block non-standard ports. Try from a phone hotspot to rule it out.

**API startup throws `Timed out connecting to Mongo`.**
The API now fails fast on startup with a clear message in deploy logs telling you the hostname it tried to reach. If you see it pointing at `localhost:27017`, the env var didn't expand — confirm the Mongo plugin's display name in Railway matches the prefix you used (`${{MongoDB.MONGO_URL}}` only works if the plugin is literally called `MongoDB`).

**`Connection refused 127.0.0.1:10000` in API logs.**
That's the Azure Blob client trying to reach Azurite. Brand-kit logo uploads will fail until you point `AzureBlob__ConnectionString` at a real Azure Storage account. Everything else still works.

**CORS errors in the browser console.**
Add your web domain to `Cors__Origins__0` on the api service. The default only knows about localhost.

**"Application failed to respond" page.**
The container is up but isn't binding to `$PORT`. Confirm the Dockerfile's final command uses `${PORT:-…}` shell expansion (it does in this repo).

## Mental model recap

```
   Browser  ─https─▶  web (Next.js)
                          │
                          ├─ client-side fetch /api/* (BFF route handlers)
                          │                              │
                          │                              ▼
                          └─ server-side fetch ───────▶ api (.NET) ──▶ MongoDB
                                                          │
                                                          ├─▶ Mock email/SMS sender
                                                          ├─▶ Mock Sonar API
                                                          └─▶ QuestPDF (renders PDFs)
```

Every browser request hits **web**. Auth and CRUD calls go through Next.js BFF route handlers carrying the JWT cookie. Those route handlers forward to **api** with a Bearer token. The browser never talks to the API directly.

Once the deploy is healthy, the same flow that runs locally on `:3000` + `:5050` runs in the cloud over HTTPS.

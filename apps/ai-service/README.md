# acme-ai-service

FastAPI service that runs the Marketing25 video agent (research → script →
voice → visuals → editor) plus the existing canvas AI endpoints.

## Environment variables

The service reads its config from the process environment. In local dev,
`docker compose` forwards these from the repo-root `.env` file into the
container. **Put your values in `/.env` at the repo root** — the ai-service
does NOT have its own `.env`.

The full set of keys expected by `app/config.py`:

| Key | Required? | What it's for |
|-----|-----------|---------------|
| `JWT_SECRET` | ✅ | Must match the .NET API's signing key. |
| `JWT_ISSUER` | ✅ | Default `acme-api`. |
| `JWT_AUDIENCE` | ✅ | Default `acme-clients`. |
| `AZURE_OPENAI_ENDPOINT` | canvas only | Existing canvas AI feature. |
| `AZURE_OPENAI_API_KEY` | canvas only | " |
| `AZURE_OPENAI_API_VERSION` | canvas only | Default `2024-08-01-preview`. |
| `AZURE_OPENAI_DEPLOYMENT` | canvas only | Default `gpt-4o-mini`. |
| **Video agent** ↓ | | |
| `Mongo__ConnectionString` | ✅ | Same Mongo the .NET API writes to. |
| `Mongo__Database` | ✅ | Default `acme`. |
| `AzureBlob__ConnectionString` | ✅ | Same Azure Blob the .NET API uses. |
| `AzureBlob__Container` | ✅ | Default `canvas-assets`. |
| `ANTHROPIC_API_KEY` | ✅ | Claude (script generation). |
| `ANTHROPIC_MODEL` | optional | Default `claude-haiku-4-5-20251001`. |
| `TAVILY_API_KEY` | ✅ | Research node (web search). |
| `PEXELS_API_KEY` | ✅ | Stock B-roll (visuals). |
| `ELEVENLABS_API_KEY` | **required when hosted** | Set it and ElevenLabs narrates every video, with **no fallback**. Unset, Edge-TTS (free) is used — which works locally but is refused from datacentre networks, so a hosted deployment without this key fails every render at the voice stage. |
| `ELEVENLABS_DEFAULT_VOICE_ID` | optional | Voice to narrate with. Unset, the first voice on the account is used and logged. |
| `REPLICATE_API_TOKEN` | optional | Enables `visualStyle=AiImagery` (Pro only). |
| `REPLICATE_FLUX_MODEL` | optional | Default `black-forest-labs/flux-schnell`. |
| `AI_IMAGERY_MAX_IMAGES_PER_JOB` | optional | Default `8`. |
| `AI_IMAGERY_BUDGET_PER_JOB_USD` | optional | Default `0.05`. |
| `AI_IMAGERY_COST_PER_IMAGE_USD` | optional | Default `0.003`. |
| `VIDEO_WORKER_ENABLED` | optional | Default `true`. Set `false` to run canvas-only. |
| `VIDEO_WORKER_POLL_SECONDS` | optional | Default `4.0`. |

## Where to put your values

Paste this block into your **repo-root `.env`** file. Only the last section is
new for the video agent; the others should already be filled in from earlier
work.

```bash
# ─── Video agent (new) ────────────────────────────────────────────
ANTHROPIC_API_KEY=sk-ant-…
ANTHROPIC_MODEL=claude-haiku-4-5-20251001
TAVILY_API_KEY=tvly-…
PEXELS_API_KEY=…
ELEVENLABS_API_KEY=            # leave empty LOCALLY to use free Edge-TTS; required when hosted
ELEVENLABS_DEFAULT_VOICE_ID=   # optional; first voice on the account if unset
REPLICATE_API_TOKEN=           # leave empty to disable AI imagery
REPLICATE_FLUX_MODEL=black-forest-labs/flux-schnell
```

Then bring the service up:

```bash
docker compose up -d --build ai-service
docker compose logs -f ai-service
```

You should see:

```
INFO  Video worker started (poll=4.0s)
```

## Where to get each key

- **Anthropic** — https://console.anthropic.com → API Keys. Any tier works;
  Haiku 4.5 is what the agent uses by default (~$0.005 per script).
- **Tavily** — https://tavily.com → Dashboard. Free tier gives 1,000
  searches/month; each video uses 4 queries.
- **Pexels** — https://www.pexels.com/api/new/ → Free forever, unlimited.
- **ElevenLabs** — https://elevenlabs.io → optional; free tier gives
  ~10 minutes of speech/month. Not needed to start — Edge-TTS is free and
  the default.
- **Replicate** — https://replicate.com → Optional. Only needed for AI
  cartoon/illustration visuals (Pro tier only). ~$0.003 per image, 8 per
  video = ~$0.024 per Pro video.

## Health check

```bash
curl http://localhost:8000/health
# {"status":"ok"}
```

If the worker didn't start, the log will say why (e.g. `Mongo__ConnectionString
empty — not starting`).

## Trigger a video from the CLI

```bash
JWT=$(cat /path/to/your/jwt)   # or grab from browser dev tools
curl -X POST http://localhost:5050/api/videos \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{
    "topic": "why book your flu jab this autumn",
    "scriptSource": "Ai",
    "visualStyle": "Realistic"
  }'
```

Then `docker compose logs -f ai-service` to watch the pipeline stages.
Expect ~30–60s total.

## Pipeline docs

See `app/video_agent/README.md` for the full pipeline design (ported from
Yami Social).

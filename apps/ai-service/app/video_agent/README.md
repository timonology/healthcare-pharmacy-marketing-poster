# Video agent

Ported from the [Yami Social](https://github.com/…) project's LangGraph pipeline.
Given a topic (and optional creative guidance or a full custom script), it
produces a 15-second 9:16 marketing video ready for Reels / TikTok / YouTube
Shorts.

## Pipeline

```
Topic
  │
  ▼
┌───────────────┐  Tavily search → Claude distills 7 sharp, sourced facts.
│  researcher   │  Quality gate: ≥5 facts, avg score ≥0.55. Up to 2 retries.
└───────┬───────┘
        │
        ▼
┌───────────────┐  Claude turns facts → JSON script with segments, hook,
│ scriptwriter  │  visual keywords, CTA. Three modes: ai / guided / custom.
└───────┬───────┘
        │
        ▼
┌───────────────┐  Edge-TTS (free) OR ElevenLabs (voice_id="11labs:...").
│    voice      │  Falls back through 4 Microsoft voices on transient failure.
└───────┬───────┘
        │
        ▼
┌───────────────┐  Pexels stock B-roll aligned to script segments (default)
│   visuals     │  OR Replicate Flux Schnell one image per beat (Pro only).
└───────┬───────┘
        │
        ▼
┌───────────────┐  MoviePy: fit + crop to 1080×1920, crossfade cuts,
│    editor     │  segment-caption overlays, watermark (free tier),
│               │  H.264 encode at ultrafast + crf 26.
└───────────────┘
```

No uploader node — Marketing25 surfaces the video via a signed blob URL and
the pharmacy user downloads / shares it themselves.

## Trigger flow

1. `.NET API` receives `POST /api/videos { topic, ... }`, gates the request by
   tier + monthly quota, and inserts a Mongo `videos` document with
   `status: Pending`.
2. `worker.py` polls the same collection every 4s. `find_one_and_update`
   atomically flips one `Pending` row to `Rendering`, sets `note: "Starting…"`,
   and hands the doc to the pipeline.
3. Each node updates the doc's `note` field with a `Stage… N%` string.
   The web UI polls every 2s while any row is in-flight and shows a progress bar.
4. Editor writes the MP4 to `/app/media/jobs/{id}/final.mp4` (persistent volume).
5. Worker extracts a thumbnail with ffmpeg, uploads both blobs to
   `videos/{ownerId}/{videoId}.{mp4,jpg}` — the same key scheme the .NET
   `VideoService.ToDtoAsync` signs read URLs for.
6. Worker flips the row to `Ready` and populates title, script, description,
   tags, and metadataJson for audit.

## Config

Required for full pipeline:

- `ANTHROPIC_API_KEY` — Claude script generation.
- `TAVILY_API_KEY` — research node.
- `PEXELS_API_KEY` — stock B-roll.
- `Mongo__ConnectionString`, `Mongo__Database` — must match the .NET API.
- `AzureBlob__ConnectionString`, `AzureBlob__Container` — same container.

Optional:

- `ELEVENLABS_API_KEY` — when set, ElevenLabs narrates **every** video and there
  is no Edge-TTS fallback (see `tts_policy.py`). Edge-TTS is the local path only.
- `ELEVENLABS_DEFAULT_VOICE_ID` — optional; otherwise Rachel, the same voice the
  dialog offers first (`voices.py`).

### Who decides the voice list

`GET /api/video/narration` answers it, and it is the only place that can: the
provider is decided by whether `ELEVENLABS_API_KEY` is set, and that key lives
here. The web dialog used to keep its own two arrays and work out the price from
a `11labs:` prefix — correct while the voice id chose the synthesiser, wrong the
moment the key did. On every hosted deployment it offered Edge voices the render
would silently swap, and priced those renders at a tenth of their cost.

So: `voices.py` holds the catalogue, `tts_policy.py` holds the routing rule and
the `11labs:` marker, and nothing in the web app or the .NET API inspects a
voice id.
- `REPLICATE_API_TOKEN` — enables `visualStyle=AiImagery` (Pro tier).
- `AI_IMAGERY_MAX_IMAGES_PER_JOB` (default 8), `AI_IMAGERY_BUDGET_PER_JOB_USD`
  (default $0.05) — cost caps.
- `VIDEO_WORKER_ENABLED=false` — disable the poller for canvas-only deploys.

## Local dev

```bash
# --build on ai-service: app/ is baked into the image with no source mount,
# so without it you run the last image you happened to build. `curl -s
# localhost:8000/health` reports the build that is actually answering.
docker compose up -d mongo azurite
docker compose up -d --build ai-service
# Then queue a job from the web UI, or:
curl -X POST http://localhost:5050/api/videos \
  -H "Authorization: Bearer $JWT" -H "Content-Type: application/json" \
  -d '{"topic":"why book your flu jab this autumn","scriptSource":"Ai","visualStyle":"Realistic"}'
```

Follow `docker compose logs -f ai-service` for pipeline output.

## Notes

- Progress writes use a fresh pymongo client per update (not motor) because
  they run inside the CPU-render thread. Motor is used only on the async
  polling side.
- Failures are non-blocking: the worker sets `status: Failed` with a
  human-readable `note` and moves on to the next pending job. The user can
  delete + re-queue.

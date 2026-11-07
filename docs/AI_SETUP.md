# AI integrations — setup guide

Everything the video pipeline needs, in the order you should set it up.

Five external services. **Three are required** — without them the worker
starts but every render fails. Two are optional upgrades with free
fallbacks already wired in.

| Service | Purpose | Required? | Fallback if missing |
|---|---|---|---|
| **Anthropic** | Writes the script, title, description, hashtags | ✅ Required | none — render fails |
| **Tavily** | Researches the topic so scripts cite real facts | ✅ Required | none — render fails |
| **Pexels** | Stock B-roll clips matched to each script beat | ✅ Required | none — render fails |
| **ElevenLabs** | Voiceover | ✅ **Required when hosted** · optional locally | Edge-TTS — works on a laptop, **refused from any datacentre** |
| **Replicate** | AI-generated imagery instead of stock footage | ⬜ Optional | AI-imagery style disabled |

---

## 1. Anthropic — script generation

**Sign up:** https://console.anthropic.com → **API Keys** → **Create Key**

Copy the key immediately; the console won't show it again. Format:
`sk-ant-api03-...`

You'll also need billing set up under **Plans & Billing** — new accounts
get a small trial credit, then it's pay-as-you-go.

```
ANTHROPIC_API_KEY=sk-ant-api03-...
ANTHROPIC_MODEL=claude-haiku-4-5-20251001
```

Keep the model on Haiku. A 15-second script is a small generation task
and Haiku handles it well for a fraction of the cost of the larger
models. Only move up if script quality genuinely disappoints you.

---

## 2. Tavily — research

**Sign up:** https://tavily.com → sign in → **API Keys** on the dashboard

Format: `tvly-dev-...` (dev keys) or `tvly-...` (production).

```
TAVILY_API_KEY=tvly-...
```

Tavily has a free monthly search allowance that's generous enough for
early production use. The pipeline makes 2–3 searches per video, so
watch the counter once you're generating at volume.

---

## 3. Pexels — stock video

**Sign up:** https://www.pexels.com/api/ → **Get Started** → copy your key

Format: a long alphanumeric string, no prefix.

```
PEXELS_API_KEY=...
```

Free, with a rate limit measured in requests per hour rather than a
spend cap. The pipeline requests roughly one search per script segment
(4–8 per video) and downloads the clips it picks.

Attribution isn't legally required by the Pexels licence for this use,
but crediting creators is good practice if you ever publish a
behind-the-scenes post.

### The pharmacy's own photographs

A pharmacist can attach up to four pictures on the create-video dialog —
a shopfront photo, the team, their brand logo, a poster they published.
Those are pinned to particular beats of the script (the opening always,
the closing call-to-action next, the rest spread through the middle) and
every other beat stays Pexels stock.

**No key, and no cost.** These never reach an image model. They are
cover-cropped to 9:16 by the same ffmpeg filter every other still in the
pipeline uses — scaled up and cropped, never stretched. Replicate is a
separate toggle with a separate charge; sending a pharmacist's own
photograph to it would bill them for a picture they already had.

Accepted: JPEG, PNG or WebP, up to 8MB each. **Not SVG**, unlike the
brand-kit logo upload — a logo is drawn by a renderer that understands
vectors, these go to ffmpeg, which rasterises nothing.

Pictures can also be added to a video that is already finished, from the
*Ask for a change* dialog. That re-enters the pipeline at the visuals
stage — normally a closed lane, because for a text instruction it means
"find different footage" and spends. Pinning the pharmacy's own pictures
does not: no model is called, the script and narration come back from
storage, and the stock footage is re-fetched from Pexels for free. The
plan carries a `pinsOwnPictures` flag so the worker can tell the two
apart, and `RefineStageContractTests` keeps both sides of that rule in
step. AI-imagery videos are excluded — re-cutting one regenerates every
beat through Replicate, which does spend.

**Framing.** Each picture carries `focalX`, `focalY` (0–1) and `zoom`
(0.5–1.5; absent means 1.0). At 1.0 the render uses the original cover-crop
filter byte for byte. Below 1.0 the picture is scaled down over a blurred,
cover-cropped copy of itself. The browser preview and `framing.py` share one
geometry, pinned by `packages/shared-types/fixtures/framing-geometry.json`.
Phone photos are turned upright from their EXIF tag before ffmpeg sees them,
because browsers apply the tag and ffmpeg does not.

A picture that will not download or will not decode gives up its beat to
stock footage and the render carries on. The pharmacist sees nothing
about it; the technical reason lands in the video's `diagnostic` field
("Attached images not used: image 2 (upload): could not be decoded"). A
render costs research, a script, narration and an encode, and spends a
video from the monthly allowance either way — so one bad JPEG is never
allowed to take the whole thing down.

---

### Changing a shot of a finished video

After every render the worker stores `shotTimeline` on the video (which moment
is on screen when) and one still per moment under
`videos/{owner}/{video}/shots/NN.jpg`. The refine dialog shows those as tiles;
the pharmacist taps a shot and says what goes there. That arrives as
`refine.shotEdits = [{index, show}]` and is applied as given. **No model chooses
the shot.** A typed request may name one by position only ("last", "shot 3"),
parsed by `shot_edits.positions`. The search order for a chosen shot is: their
words, then their words with the video's subject, then a clip already in this
video. Never the generic pharmacy list. Free: Pexels plus the stored narration.

The editor shares the whole narration across the script's moments
(`beats.fit_timeline`) instead of appending filler clips, so the tiles are
exactly the shots on screen.

**Script length.** The writer's word budget comes from the ordered length at
145 words a minute, and is enforced after writing. A script over the ceiling
gets one "tighten" call, which is accepted only if its captions still match its
words. Look for "Script tightened" or "Script over budget" in the log.

## 4. ElevenLabs — better voiceover (optional)

**Sign up:** https://elevenlabs.io → **Profile** → **API Key**

```
ELEVENLABS_API_KEY=...
```

**On a developer machine**, leave it blank. The pipeline uses Edge-TTS,
which is free and genuinely fine — `en-US-JennyNeural` is clear enough
for pharmacy explainers.

**On any hosted deployment, set it.** Edge-TTS is an unofficial route
into Microsoft's consumer speech service, and that service does not
serve datacentre address ranges. Every render on Railway or Azure dies
at the voice stage without this key. It is not a quality upgrade there;
it is the only narrator that works.

> This cost us a production outage. Renders failed at `Voicing… 45%`
> while the same code produced perfect videos locally, and setting
> `ELEVENLABS_API_KEY` appeared to change nothing — because the old
> code chose the provider from the *voice id*, so a key with an Edge
> voice selected was ignored. See `app/video_agent/tts_policy.py`.

**There is no fallback, deliberately.** When the key is set, ElevenLabs
narrates and a failure fails the render carrying ElevenLabs' own reason
("invalid api key", "quota exceeded"). The old behaviour — fall through
to Edge-TTS on any error — replaced a fixable provider message with a
confusing one from a service we never meant to use in that environment.

Which voices the app offers follows from the same fact: `GET
/api/video/narration` reports `elevenlabs` or `edge`, and the video
dialog shows only voices that will actually be used. Set
`ELEVENLABS_DEFAULT_VOICE_ID` to pin one; unset, it is Rachel.

---

## 5. Replicate — AI imagery (optional)

**Sign up:** https://replicate.com → **Account settings** → **API tokens**

Format: `r8_...`

```
REPLICATE_API_TOKEN=r8_...
REPLICATE_FLUX_MODEL=black-forest-labs/flux-schnell
```

Only used when a user picks the **AI Imagery** visual style, which is
gated to the Pro tier. Leave blank and that style is simply
unavailable; Realistic (Pexels stock) still works for everyone.

Cost is controlled by three caps so a single job can't run away:

```
AI_IMAGERY_MAX_IMAGES_PER_JOB=8
AI_IMAGERY_BUDGET_PER_JOB_USD=0.05
AI_IMAGERY_COST_PER_IMAGE_USD=0.003
```

`COST_PER_IMAGE_USD` is your estimate of what Replicate charges per
generation — the pipeline multiplies it out and stops before crossing
`BUDGET_PER_JOB_USD`. If Replicate changes its pricing, update this
number or the cap silently stops matching reality.

---

## Roughly what a video costs

Directional only — check current pricing on each provider, these change.

| Component | Per video |
|---|---|
| Anthropic (Haiku, ~2k tokens) | fractions of a cent |
| Tavily (2–3 searches) | free tier, then per-search |
| Pexels | free |
| The pharmacy's own attached photographs | free — no model is called |
| Edge-TTS voiceover (local only) | free |
| ElevenLabs voiceover (hosted) | ~$0.05 per 15s video |
| **Typical total (stock footage)** | **well under $0.01** |
| Replicate AI imagery, if enabled | capped at $0.05 |

The dominant cost of this feature is compute for the ffmpeg encode, not
the AI APIs.

---

## Where to put the keys

### Local development

Copy `.env.example` to `.env` at the repo root and fill in the blanks.
`docker-compose.yml` maps them into the container — note that a few
names change on the way in:

| `.env` name | Container env name |
|---|---|
| `AZURE_BLOB_CONNECTION_STRING` | `AzureBlob__ConnectionString` |
| `AZURE_BLOB_CONTAINER` | `AzureBlob__Container` |
| everything else | unchanged |

```bash
cp .env.example .env
# edit .env
docker compose up --build ai-service
```

`--build`, always. The image bakes `app/` in at build time and there is no
source mount, so plain `docker compose up ai-service` starts whatever was
last built — which may be weeks old. Check what you actually got:

```bash
curl -s localhost:8000/health     # {"status":"ok","build":"..."}
```

### Railway (recommended for the ai-service)

Project → your service → **Variables** → **Raw editor** → paste:

```
JWT_SECRET=<same value as the .NET API>
JWT_ISSUER=acme-api
JWT_AUDIENCE=acme-clients
Mongo__ConnectionString=mongodb+srv://...
Mongo__Database=acme
AzureBlob__ConnectionString=DefaultEndpointsProtocol=https;AccountName=sonarwebstore;...
AzureBlob__Container=canvas-assets
ANTHROPIC_API_KEY=sk-ant-api03-...
ANTHROPIC_MODEL=claude-haiku-4-5-20251001
TAVILY_API_KEY=tvly-...
PEXELS_API_KEY=...
ELEVENLABS_API_KEY=            # required on any hosted deployment
ELEVENLABS_DEFAULT_VOICE_ID=   # optional; Rachel if unset
REPLICATE_API_TOKEN=
REPLICATE_FLUX_MODEL=black-forest-labs/flux-schnell
VIDEO_WORKER_ENABLED=true
VIDEO_WORKER_POLL_SECONDS=4
SHORTS_WIDTH=720
SHORTS_HEIGHT=1280
VIDEO_FPS=24
FFMPEG_THREADS=2
VIDEO_BITRATE=2000k
```

`VIDEO_BITRATE` is a **ceiling**, not a target: quality comes from `FFMPEG_CRF`
(default 26), and `VIDEO_BITRATE` (default `4000k`) caps it through x264's
`-maxrate`/`-bufsize`. Until September 2026 the two were passed together, and
x264 quietly ignores a bitrate when a CRF is given — so the setting did nothing
and a re-cut onto detailed footage came out at 16 Mbps. Every finished video
streams to in-pharmacy screens, so keep the cap at what shop wifi can carry.

### Azure App Service

Same names, entered under **Environment variables → App settings**.
Watch the underscores — they are not interchangeable:

- `JWT_SECRET` — all caps, **single** underscore
- `Mongo__ConnectionString` — PascalCase, **double** underscore
- `AzureBlob__ConnectionString` — PascalCase, **double** underscore

Pydantic matches these aliases exactly and case-sensitively. A single
wrong underscore means the setting is invisible to the app, and
`JWT_SECRET` being invisible crashes the container on startup before it
can log anything useful.

---

## Verify it worked

Start the service and watch the logs. A correctly configured worker
announces exactly what it can see:

```
INFO  app.main: Lifespan startup: initialising video worker
INFO  app.video_agent.worker: Video worker config: enabled=True poll=4.0s
      mongo=set db=acme blob=set container=canvas-assets
      anthropic=set tavily=set pexels=set replicate=off eleven=off
INFO  app.video_agent.worker: ✅ Video worker started (poll=4.0s)
INFO  app.video_agent.worker: Mongo reachable: cluster0.xxxxx.mongodb.net (db=acme)
INFO  app.video_agent.worker: Video worker idle (0 claimed, 0 pending in db)
```

Read that config line carefully — any `MISSING` is a key that didn't
land. `replicate=off` and `eleven=off` are expected if you skipped the
optional two.

Then queue a video from the UI and watch it move:

```
Claimed video abc123... (owner=..., topic='...')
Video abc123... → stage=researching pct=5
Video abc123... → stage=scripting pct=25
Video abc123... → stage=voicing pct=45
Video abc123... → stage=collecting pct=65
Video abc123... → stage=editing pct=75
Editor: starting ffmpeg encode → final_1234567890.mp4
Editor: render complete (3.4 MB)
Video abc123... → Ready
```

End to end takes 60–120 seconds depending on the host.

---

## Also required (not AI, but the pipeline won't work without them)

**MongoDB Atlas** — the `videos` collection is the queue between the
.NET API and this worker. Under **Network Access**, the worker's
outbound IPs must be allowlisted. Cloud hosts don't publish stable
outbound IPs, so `0.0.0.0/0` is the practical setting; the database is
still protected by its username and password.

**Azure Blob Storage** — where finished MP4s and thumbnails land. Both
the .NET API and the ai-service need the **same** connection string and
the **same** container name. If they disagree, the worker uploads
successfully and the API then can't find the file.

---

## Security

Treat every key on this page as a password.

- Never commit `.env` — it's already in `.gitignore`, keep it that way
- Never paste a key into a chat, issue, or screenshot
- Rotate immediately if a key is exposed; all five providers let you
  revoke and reissue from the same dashboard you created them in

**Outstanding action:** the Anthropic, Tavily, Pexels, MongoDB Atlas and
Docker Hub credentials for this project were pasted into a chat window
during debugging and should be considered compromised. Rotate all five
and update them wherever they're configured. Revoking an old key takes
seconds; discovering someone else has been spending on it takes months.

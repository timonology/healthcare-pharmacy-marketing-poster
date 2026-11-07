# Video rendering pipeline

Short overview of what's in this folder and what you need to configure to make
each piece production-ready.

## What ships out-of-the-box

- **Poster → animated MP4** (`FFmpegPosterAnimationRenderer`). Rasterises the
  poster canvas via `Svg.Skia`, then invokes the system `ffmpeg` binary to
  produce a 15-second 9:16 MP4 with a slow Ken Burns zoom. Requires `ffmpeg`
  on the container PATH — the API Dockerfile installs it.
- **AI text-to-video** (`StubAiVideoProvider`). Deliberately throws
  `NotSupportedException` so unconfigured deploys can't silently render blank
  videos. Replace with a real vendor implementation (see below).
- **Async worker** (`VideoRenderWorker`). Polls Mongo for `Pending` videos,
  moves them through `Rendering → Ready | Failed`, uploads output to Azure
  Blob, and generates a JPG thumbnail from the mid-frame.

## Wiring a real AI video provider

1. Pick a vendor:
   - **Runway Gen-3** — high quality, expensive.
   - **Pika** — good for stylised content.
   - **Stability Video** — solid open-model API.
   - **HeyGen / Synthesia** — best for spokesperson avatars, arguably the best
     fit for pharmacy marketing.
2. Implement `Acme.Application.Videos.IAiVideoProvider`. The interface returns
   a `RenderedVideo(byte[] Mp4Bytes, byte[] ThumbnailJpgBytes)` — most vendors
   let you download a rendered MP4 and a poster frame.
3. Register your implementation in
   `Acme.Infrastructure.DependencyInjection.AddInfrastructure` in place of
   `StubAiVideoProvider`:

   ```csharp
   services.AddSingleton<IAiVideoProvider, YourVendorProvider>();
   ```

4. Add credentials via env vars (never in appsettings.json). Suggested keys:
   ```
   Video__Ai__Provider = runway            // or pika, stability, heygen
   Video__Ai__ApiKey   = <secret>
   Video__Ai__Model    = gen3-alpha        // vendor-specific
   ```

## Tuning the poster animation

The FFmpeg command in `FFmpegPosterAnimationRenderer.RenderAsync` uses a
gentle Ken Burns (`zoompan=z='min(zoom+0.0006,1.12)'`). You can swap in:

- **Pan-scan**: `zoompan=z='1.1':x='if(gte(zoom,1.1),x+1,x)'` for a
  right-panning motion.
- **Music bed**: add `-i /path/to/music.mp3 -shortest -c:a aac -b:a 128k` to
  the argument list. Bundle a royalty-free track in the image.
- **Watermark logo**: overlay a PNG using `-i logo.png` and
  `overlay=W-w-20:H-h-20` for a bottom-right stamp.

## Tier gating

Enforcement lives in `VideoService.EnforceVideoLimit`. Current limits from
`Acme.Application.Subscriptions.Plans`:

| Tier    | Max videos / month | AI enabled |
|---------|--------------------|------------|
| Free    | 0                  | ❌         |
| Starter | 5                  | ❌         |
| Pro     | 30                 | ✅         |

Change the numbers or the AI flag in `Plans.cs` — the UI and API pick them up
automatically because everything reads from the same source of truth.

## Local testing without FFmpeg

If you don't want to install `ffmpeg` locally, register a no-op renderer in DI
that returns a pre-rendered placeholder MP4. Copy the pattern from
`StubAiVideoProvider`.

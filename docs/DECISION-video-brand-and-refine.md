# Branding, booking links and refinement in video

Four features were asked for: pharmacy branding injected into video jobs, a
booking URL with a QR code, a post-generation *Refine with AI* pass, and
failures that stay retryable without charging twice.

This records what was found before building, what was decided, and — for the
parts not built yet — what still has to be measured rather than guessed.

---

## The answer, first

- **A render could be stranded, and the pharmacy had already paid.** Fixed
  first, because every other feature adds ways for a render to die.
- **Profile wins, brand kit fills the gaps.** *Reversed after implementation —
  see below. The original decision was the other way round and was wrong.*
- **The QR is not in the video.** It goes on posters and shop-window screens,
  where a second device does the scanning.
- **Refine re-enters the pipeline at the earliest stage the change touches.**
  That is what separates an edit from a regeneration, and it is the only part
  with real engineering left in it.

---

## What breaks, in order of seriousness

### 1. A crashed render was unrecoverable, and already charged — S1

`STATUS_RENDERING` appeared exactly twice in the worker: the constant, and the
claim that set it. Nothing ever read it back. No lease, no expiry, no sweeper on
either side.

The consequence is not a stuck row. `EnforceVideoLimit` runs in
`VideoService.CreateAsync`, so the monthly allowance or a purchased credit is
spent when the video is **created**, not when it succeeds. A container restart, a
deploy, or ffmpeg taking an OOM kill therefore took payment and left a job that

- the worker would never pick up again — its filter matched `Pending` only, and
- `Retry` refused, because it accepted `Failed` only.

None of the plausible failures produce `Failed`. Reaching it requires the worker
that died to still be alive to write it.

**Fixed.** A lease with an owner and an expiry; a claim that also takes
`Rendering` rows whose lease has lapsed — including rows with no lease at all,
which is every job claimed before this existed and precisely the ones stuck
today; renewal on every progress report, so the only way a lease lapses is the
process actually stopping.

Two things the lease alone did not cover, found on review:

- **A worker that hangs rather than dies** loses its lease, watches another
  worker legitimately reclaim the job, then wakes and writes `Ready` over a
  render still in flight. Every write now carries `leaseOwner` and a worker that
  finds it no longer matches discards its result.
- **The heartbeat was the most expensive thing in the loop.** The progress
  callback opened a MongoClient, wrote, and closed it on every tick — a connect
  and a TLS handshake roughly once a second. Tolerable for a progress bar; not
  for the write that keeps healthy jobs alive. One client per render now.

And the recovery was unreachable from the UI: the Retry button was gated on
`status === "Failed"`, the one state a stranded render never reaches. The server
now answers `canRetry`, because the test is a lapsed lease and the browser
cannot see one.

### 2. Two sources of pharmacy identity — S2

`User.Profile` is what onboarding collects and what poster tokens resolve from.
`BrandKit` separately holds `PharmacyDetails` (name, licence, phone, address),
`LogoBlobKey`, `BrandColors` and `RegulatoryFooter`.

**Originally decided: brand kit first, profile where a field is blank**, on the
grounds that the kit is the only source with the logo and the colours, so a
video branded from the profile alone would have to reach into it anyway.

**That was wrong, and it was reversed before any of it shipped.** The argument
is about *what to read*, not *which wins* — you can read both records and still
prefer the profile. Building it exposed the actual problem, which the original
note had half-seen and then walked past.

### What was actually there

Three surfaces already resolved this, three different ways:

| Surface | Order |
|---|---|
| Poster tokens — `{{pharmacy name}}`, `{{address}}` | Profile only; the brand kit was never consulted for text |
| Campaign email sender name | Profile, then brand kit, then "Pharmacy" |
| `User.PharmacyDisplayName` | Profile, then the account holder's own name, then "Pharmacy" |

A video is normally sent in the same campaign as a poster. Taking the brand kit
first would have named the same pharmacy one thing in the video and another on
the poster beside it — for every pharmacy whose two records disagree, with no
way for them to tell which we were going to use. The original note listed that
exact risk ("a video that contradicts the poster in the same campaign") and then
accepted it. It should not have been accepted; it was avoidable by matching what
already shipped.

### Decided

**Profile first, brand kit fills gaps.** The profile is what onboarding asks
for, what the app nags about until it is finished, and what posters already
print. A pharmacy correcting its name will do it where it was asked for it.

The logo, the colours, the licence number and the regulatory footer come from
the brand kit unconditionally — not a precedence question, because the profile
has no such fields. The booking URL is profile-only for the same reason, and
because `PharmacyProfile.NormalizeBookingUrl` is the single gate keeping a
`javascript:` URL off a poster and out of a QR code; a second source would be a
second way round it.

### And it lives in one place

`PharmacyBrand.Resolve` in the domain. `CampaignService`'s private copy was
deleted and now calls it, so the rule exists once rather than three times.
`PharmacyBrandTests` pins the order, the whitespace handling the three copies
disagreed about, and that a pharmacy genuinely trading as "Pharmacy" is not
mistaken for one that has filled in nothing.

`User.PharmacyDisplayName` is deliberately **not** folded in. It falls back to
the account holder's own name, which is right for a header the pharmacist is
looking at and wrong for anything a patient reads. Two rules, because the
audience differs — collapsing them would put a person's name on a poster.

### Resolved at creation, not at claim time

The original plan was for the render worker to resolve it when it picked the job
up. The worker is Python; the rule is C#. Resolving there would have put the
same precedence in two languages with nothing keeping them in step, which is the
fault shape behind nearly everything that has gone wrong here.

So the .NET side resolves once and **stamps the answer onto the video document**,
and the worker reads a field. `BrandContext` in the Python side carries no
precedence logic at all.

The cost is staleness, and it is bounded by **re-resolving on Retry** — the
moment a pharmacy has just fixed something and asked for another go, which is
when a misspelt name is most likely to be corrected. A render already under way
keeps the identity it started with, which is also the honest answer: it is what
was ordered.

`Video.Brand` is null on every job queued before this, and renders exactly as it
always did. No migration.

---

## The booking link

Stored on `PharmacyProfile`, beside the address and phone, and exposed as
`{{booking_url}}`. That is the same kind of thing — a contact detail a patient
acts on — so it reaches posters and screens through plumbing that already
exists rather than a second path that can disagree with the first.

Validated in the domain because of where it ends up: printed on posters and
encoded into QR codes in waiting rooms. Neither can be corrected afterwards, and
a QR is unreadable by the person holding it — they cannot tell a wrong address
from a right one until it fails to load. So it is forgiving about input (a bare
domain gets `https://`) and strict about output (http and https only; a
`javascript:` or `data:` URL here would become something that runs on a
patient's phone when they scan a poster).

### Why the QR is not in the video

A QR is scanned by a *second* device. It earns its place on something physical —
a printed poster, a shop window, a leaflet on a counter. The viewer of a
fifteen-second vertical short is holding the phone they would have to scan with.

So the code renders on posters and in-pharmacy screens, and video gets nothing.
An end-card carrying the booking URL as readable text is the sensible thing for
video. **It is now built**: the last three seconds carry a band in the
pharmacy's primary colour with its name, and one line beneath — the booking
link, or failing that the phone, or failing that the address. One line, in the
order a patient can act on it; three lines of small print over moving footage
is read by nobody.

Two details that matter more than they look:

- It is an **overlay, not an appended clip**. Appending would make a
  15-second short 18 seconds long, push it past the duration the pharmacy
  chose, and leave three seconds of silence after the narration ends.
- The text colour is picked from the band's luma, not fixed to white. A
  pharmacy whose brand colour is pale would otherwise get white on near-white
  and an end card that reads as a blank bar.

Skipped entirely on a video under six seconds, and on any failure at all — a
branded end card is worth having and not worth failing a finished render over.

**The logo is not drawn yet, and that is deliberate rather than forgotten.** The
brand-kit logo may be an SVG, which is exactly what broke poster rendering: the
imaging library throws on one instead of returning nothing, and the whole poster
was dropped. Drawing it in the video needs the rasterised copy the .NET side
already produces for posters, not the raw blob. The key is stamped and the
worker logs that it had one and did not use it, so the gap is visible rather
than silent.

The scriptwriter is also told the pharmacy's name, so the spoken call to action
can be "come and see us at X" and — more to the point — so the narration and the
card agree. A video that says one name while the card underneath shows another
is worse than an unbranded one. It is a hint, not an instruction to recite:
saying the name twice in fifteen seconds is an advert nobody watches to the end.

One consequence accepted rather than solved: a poster **published to social**
carries the code too, because social publishing shares the same cached render.
That is the same weakness — somebody scrolling Instagram is holding the phone
they would have to scan with. It is weaker than the video case, since a poster is
also seen on a desktop and gets screenshotted, and the alternative is a separate
render per channel, which is a great deal of machinery for one image. Noted here
so it is a decision and not an oversight.

The QR follows the logo's existing pattern — the canvas stores a path, each
renderer resolves it. Change the booking link and every poster already made
starts pointing at the new page, which is the same behaviour the logo has and
the reason a canvas stores a path rather than an image.

**Resolution happens in four places, not one**, and that is the standing hazard.
Most surfaces share the cached render in `PosterImageService.EnsureRenderedAsync`
— in-pharmacy screens, campaign email, PNG export, social publishing — but three
callers resolve the canvas themselves:

| Caller | Route |
|---|---|
| `PosterImageService` | the shared cached render, used by screens, email, PNG export and social |
| `PosterExportController` | `export.pdf` |
| `PosterExportController` | share-by-email |
| `AdminController` | `posters/{id}/image-check`, the support diagnostic |

The first version of this work wired only the shared render, on the assumption
that everything went through it. PDF export and share-by-email would have
printed a gap where the code belongs, and the diagnostic that exists to explain
"the poster in the email is missing things" would have said nothing about it —
the QR resolves to an empty source, and empty sources are filtered out before
the probe ever sees them.

The shape of the risk is worth naming, because the next brand asset will meet
it: one function, four callers, an optional parameter for compatibility, and a
caller that forgets it fails **silently**. Nothing warns.

**Now pinned by `BrandAssetCallSiteTests`**, which reads the sources and asserts
every `BrandCanvasAssets.ResolveForRender(...)` passes all four arguments. The
comments on each call site stay, but a comment is advice and this is a build
failure. Four assertions, because the interesting ways for it to go wrong are
not all the same:

- **Every call passes four arguments** — the fault itself, reported as
  `file:line` so it names the surface about to render posters without the
  pharmacy's details or without its booking code.
- **No caller passes a literal `null` for the profile or the encoder.** Counting
  arguments is not enough on its own: `ResolveForRender(canvas, logoKey, null,
  null)` passes four and reproduces the fault exactly, while looking correct to
  a reviewer checking the count. Null stays legal — the admin preview and most
  tests have no profile and no encoder — but a production rendering surface
  doing it becomes a decision made out loud. A `qr: null` written as a named
  argument is read as the null it is, since that is the spelling somebody
  reaches for when they mean to be explicit.
- **No caller reaches the method unqualified** — a `using static`, or a second
  class forwarding to it, would put a call where the scan cannot see it and the
  test would go on passing while the thing it guards broke.
- **The declaring file holds exactly one declaration** — an overload would be
  measured against the wrong parameter list. Not forbidden; it should just be a
  decision somebody makes on purpose.

Two details that decide whether a test like this is worth having. It **blanks
comments before scanning**, preserving offsets so line numbers stay true: the
declaring file and all four call sites are heavily commented precisely because
this is the fault we keep making, and a sentence in a doc comment failing the
build is how a test earns a reputation for crying wolf. And it asserts the scan
found **at least the four known call sites**, because a source scanner that
matches nothing passes every other assertion and reports success — worse than
no test, since its name still appears in the output.

Both faults were re-injected against a copy of the tree and confirmed to fail:
dropping the QR encoder from `export.pdf` reports `PosterExportController.cs:50`
with three arguments, and making one call unqualified is caught twice over — as
a stray, and by the call-site count falling to three.

---

## Refine with AI

The pipeline is linear — researcher → scriptwriter → voice → visuals → editor —
and keeps nothing between runs. `script` and `metadata` reach Mongo; the audio
file and the clip list are temp paths that vanish with the container.

So a naïve refine is a full re-run: the same spend as a new video, about a
minute, and no honest way to make it free.

**Decided: re-enter at the earliest stage the change touches, reusing the
artifacts of the run before.**

| Stage | Calls | Metered today | A refine that starts here must redo |
|---|---|---|---|
| researcher | Tavily | no | everything |
| scriptwriter | Anthropic | no | voice, visuals alignment, editor |
| voice | ElevenLabs when a key is set (no fallback); Edge-TTS locally | no | editor |
| visuals | Pexels, or Replicate Flux Schnell for AI imagery | **yes** — `aiImageCostUsd`, `aiImagesGenerated` | editor |
| editor | ffmpeg/moviepy, local CPU | no | nothing |

Read down the last column: an overlay, caption or timing change re-runs only the
editor — seconds, no external call, nothing to bill. A narration change re-runs
the scriptwriter and forces the voice, but can keep the visuals. A look change
re-runs the visuals and keeps the audio.

That difference is the whole feature. Without artifact reuse every refine costs
what a new video costs, and "refine" is a misleading name for it.

**The prerequisite is the work.** Re-entering a graph is easy; having something
to re-enter *with* is not. The audio and the clip list have to be persisted to
blob storage under the job, addressable well enough that a refine can pick up
exactly what it does not need to redo. If that turns out to be harder than it
looks — the clip list may not be cleanly addressable — the honest fallback is an
editor-only refine, which is smaller and still genuinely useful.

### The one exception: pinning the pharmacy's own pictures

*Added 22 September.* The table above prices a stage. That is nearly right, and
the exception is worth writing down because it looks like a loophole and is not.

Attaching a photograph to a finished video re-enters at **visuals** — the
footage is what changes. By the table that lane spends, and it is held shut. But
what costs money at that stage is the Replicate call, not the stage: pinning a
picture the pharmacy already owns calls no model at all. The script and the
narration come back from blob storage, the stock footage re-fetches from Pexels,
which is free, and the picture is theirs.

So this is a **separate door**, `RefineStages.PicturesMayBePinned`, rather than a
softer `Spends()`. The distinction matters: widening `Spends()` would also have
opened *"find me different footage"*, because the classifier routes there from an
ordinary English sentence and nothing would have stopped it. Nothing routes to
the picture lane from a sentence — a photograph arrives as a file, and a file
cannot be classified into the wrong lane.

Two consequences follow, and both are deliberate:

- **AI-imagery videos are refused.** Re-entering visuals on one regenerates
  every beat through Replicate at around five cents. The stills are already
  stored against `stillBlobKeys`, so the day the visuals node learns to reuse
  them this becomes free too — and that one method is where it changes.
- **The worker needs to be told which kind of visuals refine this is.** It
  refuses any stage outside `FREE_STAGES`, correctly, as the guard between a
  routing mistake and an unbudgeted bill. The plan therefore carries
  `pinsOwnPictures`, and `RefineStageContractTests` reads both sides so the two
  cannot drift.

There is no preview and no typed confirmation on this path. That handshake
exists because a model interpreted a sentence and could have interpreted it
wrongly; there is nothing here for it to interpret.

### What has to be measured before the charge policy is set

**The meters now exist.** Every render records what it consumed onto the video
as `usage`, and the design decision worth knowing is this:

> **Units are recorded always; money only where a rate is configured.**

Characters synthesised, tokens spent, images generated, searches run, seconds
of encoding — those are facts about what happened and they stay true. A price
is a fact about a contract on a particular date, and providers change theirs.
Multiplying at write time bakes a stale number into the record and loses what
it came from; keeping both means a rate correction applies retrospectively to
every video ever rendered, by arithmetic, rather than starting again.

It also means the instrumentation is useful *before* anybody fills the rates
in. "Six thousand characters of premium narration per video" answers a pricing
question on its own — somebody reads it off the provider's price list once.

The rates live in configuration and are **unset by default**, which is
deliberate: a plausible-looking default produces a total that reads as
authoritative, is wrong by whatever the real contract says, and hides the fact
that nobody checked. The refine charge policy is waiting on these numbers, so a
made-up one would be acted on. Totals therefore carry `knownUsd` *and*
`unpricedUnits` — the second says how much of the render the first does not
cover.

The three numbers, and where they now come from:

| Needed | Now measured as |
|---|---|
| Replicate spend per video | `replicate:images` × the rate already configured |
| ElevenLabs spend per video | `elevenlabs:characters` — **rate still unset** |
| Editor-only re-encode time | `stageSeconds.editor` |
| How often refines reach an expensive stage | `refine.stage` on each video |

So what remains is mostly not engineering: somebody has to put ElevenLabs' and
Anthropic's current per-unit prices into configuration, then let it run.

**Attempts accumulate**, which took three fields rather than one because they
answer three different questions:

| Field | Answers |
|---|---|
| `usage` | What the most recent attempt did |
| `usageAttempts` | What each attempt did, last twenty, each tagged ready or failed |
| `usageTotals` | What this video has cost in total, every attempt included |

`usageTotals` is incremented rather than recomputed, so it is immune to the
truncation above it — a video retried more than twenty times still has a true
total. **It counts units as well as money**, which is the point rather than a
flourish: the whole design records units so spend can be computed
retrospectively once rates are configured, and if only the latest attempt's
units survived, that retrospective sum would be wrong for exactly the videos
that cost the most.

This replaced a straight overwrite, under which a video that failed twice and
succeeded on the third try reported only the third try.

**A refine is not quite free, and the meters now say so.** Two model calls make
one work: the classification that routes the instruction, and the interpretation
that turns it into editor settings. Both were unmetered while the editor lane
was advertised as costing nothing — "free" asserted rather than measured, which
is the exact habit the ledger exists to break.

The interpretation is now recorded against the video, including when the refine
is then **refused**: that is the case where it is the only thing that happened,
and it would otherwise have been invisible. A refine rejected before any model
call — a closed stage, an unparseable one — still records nothing, because
nothing was spent.

The classification remains unmetered. It runs in the API request, before any
video is chosen, so there is no video to attribute it to; counting it needs a
per-pharmacy or per-day meter rather than a per-video one. Small, but it is a
known gap rather than a solved problem.

The original statement of the problem, kept because it is still the shape of
the decision:

Three numbers, none of them knowable by reading the code:

1. **Replicate and ElevenLabs spend per video.** Replicate is already recorded
   per job as `aiImageCostUsd` — the data is there to total. ElevenLabs is not
   metered at all and would need to be.
2. **How long an editor-only re-encode actually takes.** This decides whether an
   overlay tweak feels like editing or like waiting.
3. **How often refines reach the scriptwriter or the visuals** rather than
   stopping at the editor. A feature that is 90% free is priced differently from
   one that is 90% billable.

The policy the table suggests — editor-only refines free, anything reaching
scriptwriter or visuals costs a credit — is a reasonable default and is stated
here so it can be argued with rather than absorbed. It should not ship before
(1) and (3) exist.

---

## Alongside it

- **One job at a time per instance** was the constraint, and a cheap refine
  would have queued behind a sixty-second full render — so the feature meant to
  feel instant would not have. **Now two lanes**, each with its own claim loop
  and its own concurrency, polling the same collection through disjoint
  filters.

  The correctness argument is the disjointness, and it rests on one MongoDB
  behaviour worth stating because it is easy to get wrong:
  `{"refine.confirmed": {"$ne": True}}` matches documents where `refine` is
  absent, null, or unconfirmed, because `$ne` matches missing fields. So "has a
  confirmed refine" and "does not" partition the collection with no gap and no
  overlap. Checked against eight document shapes, including a null `refine` and
  a confirmed one naming a stage that is not open.

  **One Python definition, not four.** Building the lane briefly left the same
  five stage names written out in four places — the graph's entry points, the
  classifier's prompt vocabulary, the lane filter, and the canonical list — each
  correct, each independently editable, and only one of them covered by the
  contract test. They now live in `app/video_agent/stages.py` and the rest
  import it, so the seam is two sides (C# and Python) rather than five.

  The refine lane deliberately claims **every** confirmed refine, including
  closed stages, and refuses those with a reason. Filtering them out instead
  would leave them matching neither lane, sitting Pending for ever with nobody
  looking.

- **A refused refine goes back to Ready, not Failed** — usually. The video the
  pharmacist asked to change still exists and is still good, and marking it
  Failed would take a working video away from them because we could not
  re-fetch a stock clip. The exception is a video that had already failed: it
  carries its confirmed refine through Retry, and there is no rendered file
  behind it, so Ready would show a play button attached to nothing.
- **The worker writes the document directly**, bypassing the domain — which is
  why `Failed` and `Ready` have no transition methods and the lease had to be
  added in two places that must agree by hand. It will bite harder during the
  refine work, where both sides write more of the same row.
- **A worker that loses its lease keeps rendering to completion** rather than
  aborting. Bounded at one wasted render and logged loudly; cancelling properly
  belongs with the refine work, when the pipeline gets a cancellation path.

---

## Testing this properly

Done:

- `VideoRenderRecoveryTests` — a render whose worker died can be retried, one
  claimed before leases existed can be retried, one genuinely running cannot,
  and a retry re-queues **the same row**, which is the whole of the
  no-double-charge guarantee.
- `BookingUrlTests` and `ProfileBookingUrlTests` — normalisation, and that the
  link survives every route through setup. The second exists because onboarding
  and the profile page disagreed about what an absent field meant, and one
  silently wiped what the other saved.
- `BookingQrRenderTests` — the code is inlined for every renderer, absent when
  there is no link, encoded once however many times it appears, and the resolved
  canvas changes when the link changes. That last one is what makes the render
  cache correct, and it is why the encoder has to be deterministic.
- `PharmacyBrandTests` — which record wins, what counts as a filled-in field,
  and that a pharmacy actually called "Pharmacy" keeps its branding. The
  whitespace case is there because the three hand-written copies of this rule
  disagreed about it.
- `VideoBrandStampTests` — a stamp replaces rather than merges, Retry does not
  clear it (so a service that forgot to re-resolve degrades to a stale name
  rather than to no branding at all), and every field of the resolved brand has
  somewhere to live in the document. That last one is by reflection, because a
  property added to `PharmacyBrand` and forgotten in the mapper would be
  accepted, saved, and silently absent on the next read.
- `BrandAssetCallSiteTests` — every caller passes the whole brand context. A
  source test, because the fault is an argument missing from a call and the
  moment it fails is a poster somebody prints. See the four-caller table above.

### Found while building the re-entry

**A refine would have erased the video it was refining.** When a render
finishes, the worker writes eleven fields back from the graph's *final state* —
including `clip_sources`, `ai_image_cost_usd` and `ai_images_generated`, each
behind an `or` that substitutes an empty value. A refine entering at the editor
never runs the visuals node, so none of them would be in the state.

Resizing a caption would therefore have wiped the clip references, leaving that
video **unrefinable for ever after** because rehydration would have nothing to
re-download, and zeroed the record of what the render cost — which is the very
number the charge policy is waiting on.

It is the third appearance of one fault: one writer, a partial picture, a blind
overwrite. Renaming a video destroyed its script; a campaign's progress write
erased a pharmacist's Stop; now this. Rehydration seeds those fields from the
document so the stages that do not run hand back what was already true, and the
check is mechanical — for each entry stage, every field the worker writes must
either be produced by a stage that runs or be seeded.

Two more things the design had assumed and the code did not do:

- **The aligned/extra split was not recoverable.** `clipSources` was a flat
  list, and the editor pairs the nth *aligned* clip with the nth script segment
  while treating extras as a fill pool. Order could not stand in for the split:
  a segment whose download fails still appends a placeholder to `aligned_clips`
  while recording nothing in `clipSources`, so position stops matching after the
  first failure — and would have silently shuffled the footage of exactly the
  videos that had a rough first render. Each source now records its `role`.
  Clips written before that are treated as fill, which re-cuts correctly with a
  possibly different shot order, rather than guessing an alignment.

- **AI-imagery videos can be editor-refined, and where the fix belongs was not
  where it looked.** What is saved is the stills; what the editor plays is the
  clips built from them, which are local ffmpeg output and not worth storing.

  The obvious reading — "make the visuals node reuse the saved stills" — is
  wrong in both directions. A **visuals** refine is somebody asking for
  *different footage*; reusing the old pictures there hands back the images they
  just asked to change and reports success. An **editor** refine is somebody
  resizing a caption; regenerating there bills Replicate and, because Flux is
  stochastic, returns a video that no longer looks like the one they approved.

  So the reuse belongs to the editor path, and that means **rehydration**, not
  the visuals node: the stills are downloaded and rebuilt into clips with
  ffmpeg. `image_to_video_clip` calls no provider, so the editor lane stays
  genuinely free, the pictures stay the ones that were approved, and the stage
  cost model needs no special case for "visuals, but free this time".

  Each still belongs to one script beat, so the rebuilt clips are all aligned
  and there is no fill pool; durations come from the saved segment timings, so
  the recut keeps the original's pacing.

Still wanted:

- **Make the visuals node reuse saved stills.** It is the one thing standing
  between AI-imagery videos and a free editor refine, and the artifacts it
  needs are already in blob storage being paid for.
- ~~Persist the research facts before the scriptwriter lane opens.~~ **Done.**
  A render now keeps the claims its script was written from, capped at twenty so
  a malformed reply cannot grow the document without bound, and a rewrite
  resumes from them.

  The alternative — rewriting from the topic alone — was never a smaller version
  of the same feature. The scriptwriter would have invented whatever it liked in
  place of researched claims, on patient-facing health copy, in response to
  somebody asking for a friendlier tone. So a video with no recorded research is
  **refused** rather than quietly downgraded; that covers every video rendered
  before this, which is the honest answer for them.
- **A test harness for the web canvas helpers.** `applyPharmacyInfo` has none,
  and it is half of the token and brand-asset behaviour — the half a pharmacist
  sees. Two of the bugs found in this work lived on that side.
- **A test that kills the worker mid-render** and asserts the job comes back.
  The lease makes it correct in principle; nothing proves it.
- **Scanning a printed QR with a phone.** In the QA script, because a code that
  renders perfectly and goes nowhere is invisible to every other kind of check.

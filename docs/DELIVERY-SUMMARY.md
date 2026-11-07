# Delivery summary

Cumulative: what exists, what is known to be missing, and what still needs a
decision. For the period-by-period narrative see `WEEKLY_UPDATE.md`.

Last revised 2 October.

## Shipped

- **"Design with AI" now designs in the house style, for admins and pharmacies
  alike.** The model no longer draws coordinates: it picks one of four house
  layouts (circle portrait, diagonal split, wave footer, checklist; "bold" is a
  tone that sets larger type, not a layout) and
  writes the copy; `house_style.py` draws it with the same builders as the
  `sys-house-*` set, sizes and balances the headline, wraps every line for both
  renderers and runs the house checks. Copy that does not fit, or breaks a
  house rule (prices, reviews, brands, "your pharmacy", an invented pharmacy
  name or phone number), goes back to the model in plain English. Photos are
  cover-cropped and baked — circle PNGs, fades in the JPEG — and stored by the
  API, never hot-linked. Pharmacies get the same four photo choices as admins
  (None, Stock by default, Upload, AI only when configured and never silently
  swapped for stock); uploads are checked by their bytes on pick; stock search
  is rate-limited per person; the brand kit (name, phone, address, logo,
  booking QR) is applied after generation. Allowances and HMAC unchanged: one
  generation is one AI design whatever the photo.

- **Audit and activity log: who did what, including views.** Two new
  collections in the same database — `audit_events` (changes and security,
  kept) and `activity_events` (allow-listed views, 90-day TTL) — one shape:
  actor, actor kind (pharmacy / admin / system), the pharmacy acted as, action,
  entity, result (ok / denied / failed), ip, request id, small sanitised meta.
  Written by a middleware from endpoint attributes after the response is
  decided, so a 403 from a policy is recorded as `denied`; services add what
  only they know through `IAuditScope`. Every POST/PUT/PATCH/DELETE is marked
  `[Audit]` or `[NoAudit("why")]` (110 today) and `scripts/check-audit.mjs`
  fails the build otherwise. Manage-as is `admin.impersonate.start/stop` with
  the admin as actor and the shop as `pharmacyId`. Console: *Security audit* |
  *Staff activity* | *Pharmacy activity*, filters, CSV export (audited). Admins
  see their linked pharmacies and their own actions; SuperAdmins everything.
  Legacy `admin_audit` history copied in once, up to a cutoff. See
  ADMIN_GUIDE → Audit log.

- **A pharmacy's own photographs in its videos.** Up to four pictures attached
  on the create-video dialog — an upload, the brand-kit logo, or a published
  poster — pinned to particular beats: the opening always, the closing
  call-to-action next, the rest spread through the middle. Every other beat
  stays Pexels stock. No image model is called and there is no extra charge; the
  Pro AI-imagery toggle remains separate. Cover-cropped to 9:16 by the same
  ffmpeg filter every other still uses, so a phone photograph fills the frame
  without stretching. A picture that will not download or decode gives up its
  beat to stock and the render finishes — the pharmacist's note stays human, the
  technical reason goes to `diagnostic`.
- **Adding photographs to a video that is already made.** *Ask for a change* now
  takes pictures as well as a sentence. They are added to whatever the video
  already has, up to four, and it is re-cut with them in — the words, the voice
  and the rest of the footage kept. No preview and no typed confirmation: a
  photograph names what it wants by being one, so there is nothing for a model
  to interpret and nothing to agree to.
  <br>**It opens one closed lane, narrowly.** Changing footage is the `Visuals`
  stage, held shut because it spends. What actually costs money there is the
  Replicate call, and pinning the pharmacy's own picture makes none: the script
  and narration come back from storage and the stock footage re-fetches from
  Pexels for free. So it is a separate door rather than a wider `Spends()` —
  widening that would have opened "find me different footage" as a side effect,
  because the classifier routes there from plain English and nothing would have
  stopped it. AI-imagery videos are excluded: re-cutting one regenerates every
  beat through Replicate.
- **In-pharmacy display screens** — a television or tablet on the pharmacy wall
  loops published posters. Registered screens, shared playlists, paired once
  with a six-character code, no account on the device, and content that updates
  itself within thirty seconds of an edit. Free 0 / Starter 1 / Pro unlimited.
  See `DISPLAY_SCREENS.md`.
- **Pharmacy hierarchy** — parent/child pharmacies with scoped "manage as", a
  named current pharmacy in the header at all times, and an audit trail of every
  stint. Called *branches* throughout the pharmacy-facing app.
- **Campaign posters in the email body** — a signed link to a rendered image
  instead of a PDF attachment nobody opened. Aligned, mobile-safe, videos
  positioned below, sender name and pharmacy block resolved from the brand kit.
  **3.7 KB per recipient, down from 244 KB.** The token binds the *blob key*,
  not the poster id, so editing a poster cannot rewrite an email already sent;
  180-day validity. Falls back to an embedded image, with a warning, when
  `Public:ApiBaseUrl` is not reachable from the internet — a mail client fetches
  images through the provider's servers, so a loopback or private address is a
  broken image for every recipient and looks perfect to whoever tested it.
- **Queued campaign dispatch** — creating a campaign no longer sends it. A
  worker claims it atomically, sends in groups, records per-recipient progress
  and re-reads status between groups so Stop is honoured within one group. Cap
  1,000 per campaign, confirmation above 200. Monthly allowance counts queued
  and in-flight recipients, and a stopped campaign's `SentCount`.
  See `DECISION-campaigns-at-scale.md`.
- **Booking URL and QR code** — stored on the pharmacy profile, normalised and
  validated on save. Six token spellings resolve to the address as text; a
  QR slot encodes it (QRCoder, ECC level Q, pure managed — no System.Drawing).
  Resolved by all four C# callers: `PosterImageService`'s shared cached render
  (which serves display screens, campaign email, PNG export and social),
  `export.pdf`, share-by-email, and the admin `image-check` diagnostic. The web
  editor draws it separately from the same stored path. Pinned by
  `BrandAssetCallSiteTests`.
- **Leased video renders** — a render holds an owner and an expiry, renewed
  from evidence of progress. A worker that dies releases the video for Retry
  without re-charging the monthly allowance; a worker that is merely slow keeps
  its claim, and one that has lost its lease discards its result rather than
  overwriting a newer render. All three terminal writes are guarded on
  `leaseOwner`.
- **Split document ownership for videos** — the .NET side updates eighteen
  named fields and never `ownerId`, `createdAtUtc` or the ten the Python worker
  owns. Previously a whole-document replace, so renaming a video erased its
  script, generated metadata and cost record.
- **Account email** — welcome, address verification (recorded, not yet
  enforced), and a full password reset flow with pages, tokens and expiry.
- **Contact import and export** — spreadsheet import with a line-by-line report,
  duplicate matching on email *or* phone, plan limits respected, and an export
  that exports the whole list rather than the visible page.
- **Test environment** — separate Azure resource group, seeded demo pharmacies,
  its own database and sending domain.
- **`pnpm ship:dev` / `:test` / `:prod`** — one command builds, pushes and
  deploys an environment.

- **Zoom and pan for the pharmacy's own photos.** Each attached photo now
  stores `focalX`, `focalY` and `zoom` (0.5–1.5). At 1.0 the render uses the
  original crop filter byte for byte, and **absent means 1.0**, so no row was
  migrated and no existing video changes. Below 1.0 the photo is scaled down and
  the gaps are a blurred, cover-cropped copy of it (`boxblur` + `overlay`), not
  padding. Above 1.0 it is a plain tighter crop. Same control on create and
  refine: a Zoom slider beside Across/Down, two-finger pinch, and Ctrl/⌘ + wheel
  (trackpad pinch). A plain scroll still scrolls the dialog. Pan axes come from
  the geometry at the current zoom, so a drag along an axis with no slack does
  nothing.
  <br>**The preview is the render.** One geometry function exists in Python
  (`framing.py`) and TypeScript (`framing.ts`). Both are held to a 467-row
  golden table (`check-framing.mjs`, `test_framing.py`), which includes the
  odd sizes where banker's rounding would put them a pixel apart. The render's
  photo edge is checked against that geometry in real ffmpeg pixels. Out of
  range values are clamped on every side, and an unreadable zoom is 1.0: it
  never costs the picture its beat.

- **Shot picker: the pharmacist chooses the shot; nothing guesses it.** *Ask
  for a change* now opens with every moment of the video as a tile: a still
  of that shot as rendered, its time, its caption line, and "Your photo" where
  relevant. Tap the shots to change (up to 6), say what each should show, and
  press *Change … and re-cut*. There is no preview and no confirmation, because
  nothing is interpreted. It is free: stock search plus the stored narration.
  A typed request may still name a shot, but **only by position** ("first",
  "last", "shot 3", "second to last"), read by code. Anything else is answered
  with "tap the shot". The model that used to choose the shot from a
  description is gone. It read stock-library titles and refused real requests
  ("no moment shows medicine"). For a chosen shot the search is the
  pharmacist's words, then their words with the video's subject. If both miss,
  a clip already in this video is reused. On a refine the generic pharmacy
  list is never used; failing leaves the video as it was. Built with the
  timeline fix, so "last" is the last moment and every shot on screen is a
  tile. New endpoints: `GET /api/videos/{id}/shots`,
  `GET /api/videos/{id}/shots/{index}/thumbnail`, `POST /api/videos/{id}/shots`.

- **Videos on in-pharmacy screens.** A playlist can now hold finished videos
  (up to 10) alongside posters — *Add video* in the playlist editor lists only
  Ready videos, and the Screens page badges screens that play video. The kiosk
  (`/display`, same pairing token, same 30s poll) plays each video once, muted,
  letterboxed (a 9:16 video on a landscape panel is never stretched), then
  moves on. The content API returns each slide's type and a media URL: posters
  keep their token-guarded, offline-cached PNG path; a video gets a
  same-origin `/api/display/video/{id}?sig=…` link — an HMAC signature for that
  one file, never the screen token or a blob key — streamed through a web route
  that passes `Range`/`206` (Safari requires it). Videos are not cached for
  offline; the screen's browser may cache a played video (versioned URL plus
  the API's new ETag on signed downloads), so loops do not re-download every
  MP4. Ready is checked on every poll, so a video that is re-cut, fails or
  is deleted leaves the screen within 30s. Links are signed to six-hour windows
  so polls do not churn the ETag. A kiosk tab still running pre-video code is
  sent posters only (`X-Display-Capabilities`). Tests: 7 .NET integration, 15
  .NET unit, 3 web files (9 failure modes proven by `test:prove`), and a
  smoke step with four self-test scenarios. See `DISPLAY_SCREENS.md` → *Videos on a
  screen*.

- **Third house-style set (ten more, one or more in every category):** Shingles and Pneumonia vaccination (Vaccination); Stop smoking support and Contraception advice (Awareness); Pharmacy First and Prescription delivery (Promotion); Antibiotics awareness and Return unused medicines (Safety, photo-free by design — their natural photos are branded packaging); Hay fever help (Seasonal); Repeat prescriptions (General). New motifs: numbered steps, condition chips, a two-tone capsule. Photos hand-checked as before — rejected: every "quit smoking" result (lit cigarettes), a blurred clinician, a courier with a clothing logo and shipping label. Copy follows NHS wording; brand list extended (stop-smoking, contraceptive, hay fever and vaccine brands). **Run the asset script again** — it fetches only the eight new photos.
- **Second house-style set (six more, insert-only):** Know your numbers, Diabetes risk check and Weight management (Awareness); Flu vaccination, Winter vaccinations and Travel clinic (Vaccination — there is no Travel category). New arch-shaped photo motif; per-template categories and release dates now come from the generator. Photos hand-checked as before — a BP photo with a printed cuff and a glucometer with a model label were rejected or cropped out; weight management uses a supportive walking photo, never a tape measure, and never mentions medication. Two new checks: text running into the text below it (with long real names), and a wave dipping below the photo it sits on. **Run the asset script again** before shipping: it skips the six photos already built and fetches only the new six.
- **House-style poster templates (six, insert-only).** Trusted circle, Diagonal care, Team wave, Advice checklist, Book with us, Local pharmacy — medical blue/white, circle or diagonal photo, teal wave or footer bar, plus accents, A4, category General, tokens for every contact detail and one booking QR. Photos are six hand-checked Pexels images (no logos, text or packaging), baked by `app.tools.house_template_assets` — circle crops as transparent PNGs, fades into the JPEG — and embedded in the API, which uploads them to template storage before inserting each template, and holds a template back rather than insert it without its photo. Designs are generated by `app.tools.house_templates`, which checks every text line against both the PDF's wrap rule and the real font, contrast against what is behind it, and the house rules. Two renderer mismatches fixed on the way: the editor now fills closed lines like the PDF, and the PDF no longer draws a grey box for an empty (deliberately absent) image. **One step needed:** run the asset script once (see `TEMPLATE_AUTHORING.md`) and commit the six files.

## Bugs found and fixed

- **Found from a smoke run and its logs (28 Sep): four render faults, two of them behind the "0 stills" and "shot 2 changed as well" warnings.** `shot_timeline`, `previous_aligned` and `previous_sources` were not declared on the graph's state schema, and LangGraph drops undeclared keys silently — so no render ever stored its shot timeline or stills (the picker fell back to the plan's tiles), and every refine re-searched every shot instead of keeping the untouched ones. Declared, and `test_state_schema.py` now fails on any key used through the graph but not declared. Separately, a damaged attachment cost a minute per render (a looped encode waiting out its timeout); it is now dropped in under a second. And `VIDEO_BITRATE` was ignored because a CRF overrode it — a 19s re-cut was 38 MB; it is now a real ceiling. The smoke now fails, rather than warns, when a fresh video has no stills at all.

Kept because the second column is the reusable part — each row is a category of
fault, not a one-off.

| Bug | Why nobody noticed |
|---|---|
| **Videos ran 22.8s for a 15s product** (production smoke) | The word budget was only *requested* in the writer's prompt. Models do not count words reliably, and nothing downstream trims a script: the video is as long as its narration. At ~145 wpm, 22.3s of speech is about 54 words against a ceiling of 40. The scriptwriter now counts the words that will actually be spoken and, if the ceiling is broken, asks once for a tightened version that keeps the hook and the call to action. If that fails or is no shorter, the original is kept, because a long video is a cost problem and a failed render is a lost video. `test_script_budget.py` drives the real scriptwriter node with an overrunning fake model. |
| **The shots the refine could see were not the shots in the video** ("remove the last shot which shows medicine" → "no moment shows medicine") | The editor gave each moment its *planned* length (the scriptwriter's estimate, capped at 8s). When the voice ran longer than planned (19.8s for a 4-moment script), it appended "top-up" clips from the spare pool to cover the gap. Those shots belonged to no moment, so the refine described 4 moments of a 6-shot video. The shot the pharmacist called "last" was a filler the model had never been shown. The model also trusted the stock-library title ("employees on a couch") over the pharmacist's own eyes. Every earlier fix patched how that shot list was described; none noticed the list was incomplete. Now the real narration is shared across the moments (`beats.fit_timeline`) and a short clip loops instead of being followed by another, so every shot on screen is a moment. The render stores when each moment is on screen (`shotTimeline`), and the model is told "last shot, 0:15–0:20" with the rule that a position in the request outranks a stock title. |
| **"Change the last video" was refused as "your own photograph"** | Which moments show the pharmacy's photos was re-planned from the video's picture list on every refine. That list can hold pictures the video never showed: `AddPictures` appends them when the change is queued, and nothing took them off if the worker then refused or failed that change. So a video with stock at its first and last moments was treated as having photos there, and a request to change the last shot was refused. Three fixes: (1) after a render, the recorded moments are the truth (`beats.recorded_photos`), not the list; (2) an add-pictures change records how many it added (`addedPictures`), and the worker takes them back off if the change does not happen; (3) a shot edit may replace a photo moment when asked, and that choice survives later refines. Covered end to end through the real visuals node (`test_refine_scenarios.py`), with the old behaviour put back to confirm the tests fail. |
| Found in review: adding a photo would have brought back one the pharmacist replaced | The first version of "recorded moments are the truth" re-planned the whole picture list whenever pictures were added. It now plans only the photos currently shown plus the new ones. An add-pictures change from an API build that does not record the count falls back to the old whole-list plan, rather than silently placing nothing. |
| Found in review: one failed photo download would have removed that photo for good | A photo that would not download fell back to stock and was recorded as footage, so every later refine read it as replaced. The moment is now recorded as a photo moment either way, and the next render tries the photo again. |
| Found in review: a failed refine's message pointed at a Retry button that is not there | "Try again — it won't use another video from your allowance" is right for a failed first render and wrong for a change to a finished video. Refines now say the video is as it was and to ask for the change again. |
| A refine that crashed marked a finished video Failed | The refusal path already put the video back to Ready. The crash path set Failed, which hid a working video and offered Retry on a change rather than the video. A failed change to a finished video now leaves it Ready, says why, and removes any pictures that change added. |
| **The same footage appeared twice in one video** (reported as "a duplicate caption") | Four causes, all in how gaps were filled. A moment whose search failed took `pool[i % len(pool)]` from a pool that *started* with clips already on screen, so the unused spare clips at the end were almost never reached. The editor topped up short footage by cycling that same pool. Every reuse cut the identical middle section, so a repeat was frame-for-frame. And a refine's new searches did not exclude clips the video already had, so a re-searched moment could come back with a kept moment's clip, or the very clip it was asked to replace. Now: unused spares first, then the least-used clip that is not on a neighbouring moment (`beats.fill_gaps`); a reused clip is cut from a different part of itself; the top-up picks the least-shown clip; and a refine excludes every clip the video already has. The shot-edit model is told which moments repeat another ("the same footage as moment 2"), so "remove the duplicate video" can name the right one. |
| **A refused change looked like a change that silently did nothing** | When the worker cannot make a change it puts the video back to Ready with the reason in `note`. The video card only showed `note` while rendering or failed, so the pharmacist watched it load, saw the same video, and had no way to know a sentence explaining why was sitting in the database. The card now shows "Last change not made: …", and the page announces the outcome when a change finishes, either way. |
| **Footage was paired with moments by list position** | Every clip's record went into one flat list, and a refine gave the nth record to the nth moment. A moment showing the pharmacy's photograph, or one that borrowed a clip after a failed search, recorded nothing, so every later record was one moment early. That caused three things: a caption re-cut **dropped the pharmacy's photograph** and slid each shot along; a shot edit kept the wrong shots; and nothing tied footage to a moment at all. The code's own comment said position stops matching after the first failure, and the refine path relied on it anyway. Records now carry `segment` (one per moment, including photographs and borrowed clips), and `beats.py` reads older untagged records by stepping over photo moments. Tests put the old pairing back and fail. |
| "Remove the person washing their hands" found no such moment | The shot-edit model was shown each moment's *planned search word* ("flu prevention"), not what the downloaded clip actually showed. A pharmacist names what they saw. Each clip now records `shows`, taken from its Pexels page slug ("a woman washing her hands"), and clips from older videos are looked up by id when a shot edit needs them. |
| "We cannot removing duplicate captions from here." | Two faults in one sentence. The editor had no setting for a repeated caption, so the request was refused. And the refusal pasted the model's noun phrase after "We cannot". Captions are now de-duplicated on every cut, the interpreter recognises the request, and the worker checks the script really repeats a caption before promising to remove one. If it doesn't, it says so and asks which words. |
| **Every refine failed with "We could not work out what to change just now"** | The refine classifier's `from app.llm import anthropic_chat, text_of` had been pasted onto line 2 of the module, *inside* its docstring, where it is text. Python loads the file fine and only fails when the name is called, inside a `try` written to turn provider outages into a polite "try again". So a NameError on every request looked exactly like a flaky provider, and the log line (`name 'anthropic_chat' is not defined`) was the only clue. The classifier had no tests. Now it has six that drive the real code path with a fake model (three fail against the broken file), and `test_no_undefined_names.py` checks every module in the service for names used but never defined. It found exactly these two and nothing else. |
| The same caption could flash twice | Each beat's caption is its first sentence, and scripts repeat the call to action at the start and the close, so the same words appeared twice. Captions are now built by `caption_flashes`, which drops a caption already shown (ignoring case, spacing and punctuation) and skips a beat with broken timings instead of passing moviepy a negative duration. |
| Watermarked videos said "Sonar Marketing" | Burned into the video by the worker. `check-brand` only scanned the web app and the API, the two codebases with screens, so the one string a customer sees in every free video was out of its reach. The worker is now scanned too. |
| **An upright phone photo rendered on its side** | Phones store the sensor image landscape plus an EXIF "rotate 90°" tag. Browsers apply the tag, so the thumbnail was upright; ffmpeg (4.4 and the image's 5.1) ignores it for JPEGs, so the render got the sideways pixels, cropped on the wrong axis against a focal point chosen on the right one. It passed every test because test images are made in code and carry no tag. The worker now writes an upright copy before ffmpeg (`_upright`) and strips the tag, so a newer ffmpeg that does honour it cannot rotate twice. |
| The framing chosen on the create dialog never reached the render | The payload destructured `focalX, focalY` and then built the object without them. It typechecked (dropping an optional field is legal), and centre is also the default, so a centred test photo looked correct. Only the refine dialog sent them. Fixed along with zoom, and the object now lists every framing field. |
| **Dialogs taller than the window could not be scrolled** | `DialogContent` is centred with a transform and had no max height or overflow, so a tall dialog lost its title and its action buttons off both edges. Short test dialogs never showed it. The create-video dialog with four framed photos is taller than a laptop screen. Every dialog now caps at the viewport and scrolls. |
| The zoom slider was about 18px wide | A label, a slider and a readout side by side in a thumbnail column a quarter of a 512px dialog wide, and nothing at all on a phone. The label now sits above a full-width slider, phones get two columns, and the how-to line is said once above the grid instead of wrapping under each photo. Found by working out the column width, not by looking: the sandbox has no browser. |
| A rotated CMYK JPEG lost its zoom silently | The upright copy was written as PNG for any mode other than RGB/L, and PNG cannot hold CMYK, so the save threw. The photo fell back to sideways with no size, which drops the zoom. Found in review; CMYK is now converted to an RGB JPEG, with a test. |
| `"zoom": null` refused the whole video | A plain `double` on the DTO rejects null during binding, before any fallback runs. It is now `double?`, and null means 1.0, the same as omitting it. |
| A trackpad pinch could not leave 100% | Found in review before it shipped. A pinch arrives as many one-pixel wheel steps; each one taken alone from exactly 1.0 lands inside the 0.5% snap and rounds straight back to 1.0. The unsnapped value is now carried between events, and a harness test fails if that is removed. |
| **A supplied image blob key was trusted, so one pharmacy could read and destroy another's files** | The attached-image key arrives in the create-video body. Nothing checked it belonged to the caller — and a key is used twice: the render worker downloads it, and deleting the video deletes it. So naming another pharmacy's poster key rendered their picture into a video the caller could download, and deleting that video destroyed their blob. It read as safe because the *upload* endpoint only ever mints keys under the caller's own prefix — but the create endpoint does not have to be given a key that came from it. Found by asking what `DeleteAsync` would do with a `source: "logo"` row, which is a case the feature is specified to support. Now every key must sit under `videos/{owner}/attachments/`, `posters/{owner}/` or `brand/logos/{owner}/`, with one identical refusal sentence so probing learns nothing. |
| Deleting a video deleted the pharmacy's brand logo | Same root, no attacker needed. The logo is an offered source, so a pharmacist could legitimately pin it — and delete removed every attached key, including one that belongs to the brand kit and is still on every poster. Delete now touches uploads only. |
| "Images must be under 8MB. That one is 8MB." | The size was rounded to the nearest tenth, so every file between 8MB and 8.05MB — the commonest refusal there is, a phone photograph a little over the line — was reported as exactly the limit it had just exceeded. A sentence that contradicts itself and gives the pharmacist nothing to act on. Rounded **up** now, so the figure shown is always visibly larger than the cap. Found by writing out what the message would actually say rather than by reading the arithmetic. |
| A fourth attached photograph silently went missing | Four pictures on a four-beat script: two interior beats collided, `set()` swallowed one, and nothing reported it. The test written for it then passed against a version with the shortcut branch removed — so the *test* was re-checked by swapping in three collision-prone spacings until it failed for the right reason. A regression test that has never failed is a test nobody knows is wired up, and that applies to the ones written last week too. |
| **The whole page froze until it was reloaded** | Two copies of `@radix-ui/react-dismissable-layer` were installed — 1.1.11 for `Dialog`, 1.1.19 for `DropdownMenu`. That module makes a modal modal by setting `document.body.style.pointerEvents = "none"` and restoring it on close, and both the saved value and the open-layer count live in *module scope*. Two copies means two bookkeepers writing the same property: open the ⋯ menu on a video card (copy A saves `""`, sets `none`), pick *Share* (copy B's counter is zero, so it saves the body's current value — `none`), close both, and copy B restores `none` for ever. The page renders perfectly, nothing is clickable, no error is logged. It happens by accident because Radix pins its internal dependencies to *exact* versions, so this is not a conflict pnpm can resolve — it correctly installs both. Pinned via `pnpm.overrides`, with `scripts/check-radix-layers.mjs` failing the build if a second copy returns. Two more duplicated singletons turned up the same way: `react-focus-scope` and `react-focus-guards`. |
| The boot marker could not tell a new image from an old one | `BUILD_ID` falls back to the mtime of `main.py` alone. That file changes rarely, so a rebuild that added `video_agent/user_media.py` and rewrote `video_agent/worker.py` left the marker reading five days old — and it was believed over the rebuild, repeatedly, by people reasoning carefully from it. Exactly the fault the marker exists to prevent, in the half of the code that was not fixed when the deploy path got a git SHA. The fallback is now the newest mtime anywhere under `app/` plus the file count: any edit moves the first, any added or removed module moves the second. |
| A 20MB photograph failed with no explanation | `[RequestSizeLimit]` was set to exactly the cap, so the framework rejected the request *before* the action ran and `WhyFileRefused`'s sentence was unreachable. What came back was ASP.NET's own validation envelope, which has no field the BFF could read, so the pharmacist was told only "That image could not be attached" — after waiting for the whole upload. Three fixes: the browser now refuses an oversized file before sending, with its name and size; the attribute is a *ceiling* at twice the cap so files in the range people actually pick reach the action and get the real message; and the proxy recognises the framework's envelope for anything past it. |
| A render could freeze for ever on a field that was `null` | Four worker-owned fields were inserted as explicit nulls before `[BsonIgnoreIfNull]`. Mongo's `$push` creates an array on an **absent** field and refuses a **null** one — and the usage write sits on the success path *and* the failure path, so the render could neither finish nor record that it had failed. `NullUsageFieldRepair` removes them at startup. Absent and null are different things, and "never write these fields" turned out to include never writing them as null. |
| The smoke test skipped the half it existed to check | It signed in as the bootstrap SuperAdmin. Staff sit on the Free plan, so the video and AI-design doors reported "no allowance" and **SKIPped** — a green run that never touched anything expensive — while every run littered the one account an operator has to be able to trust. Now a dedicated pharmacy user on a paid plan (`SmokeAccountSeeder`), with the published dev password refused **by value** outside Development. |
| The voice picker priced a render at a tenth of its cost | The catalogue was two hard-coded arrays in `videos/page.tsx`, and the browser decided from a voice id whether a render would cost anything. That held while the id chose the provider; it stopped the moment the *key* did. The dialog went on offering a free voice on deployments where it could never be used. Only ai-service knows whether the key is set, so it now answers the whole question — provider, voices, default, cost — and the browser renders the answer without deciding anything. |
| Every hosted render died at the voice stage | The provider was chosen from the **voice id**, not from whether a key was configured — so setting `ELEVENLABS_API_KEY` in production changed nothing, and the pipeline fell through to Edge-TTS, which Microsoft does not serve to datacentres. Worked perfectly on a laptop. Now: key set means ElevenLabs, no fallback, and the failure carries the provider's own reason. |
| The video thumbnail was missing from campaign emails | It was a base64 `data:` URL, and **Gmail strips those**. The poster in the same email had already been moved to a signed link for the same reason and the thumbnail was left behind — one question ("can a mail provider's fetcher reach this API?") answered twice, forty lines apart. The watch link worked, which made it look like a layout bug. |
| Refine opened a page with no way to save | The admin template composer pushed to `/canvas`, a scratch stage with Add rect / Add circle / Add text. Duplicate went there too, where a copy could only be lost. Both now go through the composer, which can name, edit and publish. |
| Videos were 27 seconds for a 15-second product | The scriptwriter's system prompt carried "140-160 words for a 60s Short" while the request asked for 15s; the model split the difference, and nothing downstream trims — the clip is exactly as long as its narration. Everything scaled with it: b-roll, compositing, a 91-second encode, storage. The budget is now derived from the ordered duration. |
| The smoke test called a working render "stuck" | It reported a render that finished 28 seconds past its timeout as "cannot finish *or* fail" — wording written for a genuine freeze. Slow and stuck are different: it now tracks whether progress is still arriving and only fails when it has stopped. |
| Stop was accepted, then erased | `SendInGroupsAsync` never re-read status, and `UpdateAsync` replaced the whole document from a copy taken before the Stop. The campaign ran to the last recipient and reported `Sent`. Fixed with `SaveProgressAsync` — a named-field update that never writes `status` and returns the status after the write. Same fault shape as the video document replace below; both are two writers and one record. |
| The monthly allowance counted only finished campaigns | `CountRecipientsThisMonthAsync` filtered on `SentAtUtc`, so five 500-recipient campaigns could be created in seconds on a 500/month plan. Now adds outstanding recipients of Queued/Sending campaigns and Stopped campaigns' `SentCount` — disjoint sets, so nothing double-counts. |
| Retry was gated on `Failed` | A campaign that reached 700 of 1,000 is `Sent`, so the button never appeared on the campaigns that needed it most. The same shape later turned up on video, where a stranded render is `Rendering` and Retry only accepted `Failed`. |
| A deploy mid-send marked campaigns `Failed` | `OperationCanceledException` fell into the general catch. A `Failed` campaign was not claimable, so an ordinary release stranded whatever was in flight — contradicting the worker's own comment about shutdown. |
| A fresh super-admin could not reach the admin console | `PharmacyProfile` had no representation for "staff, no pharmacy", so the onboarding guard held them at a setup form with nothing to complete. Would have hit the first production deploy on the first administrator. Surfaced as 20 Playwright failures that all looked like test problems. |
| Stranded video renders | `STATUS_RENDERING` was written once and never read. A worker that crashed or was deployed over left the video `Rendering` forever, already charged against the pharmacy's allowance, with no way back for the customer and no way to find them. Fixed with a lease: owner plus expiry, renewed from progress, claim matches lapsed-or-absent. |
| Renaming a video destroyed its script | `MongoVideoRepository.UpdateAsync` was a `ReplaceOneAsync` built from `VideoMapper.ToDocument`, which maps only the fields the domain knows. Every write from the API erased the seven the Python worker had produced. The rename appeared to work perfectly. |
| The audio blob key was recorded when nothing had been uploaded | Initialised to the intended key rather than `None`, so a failed upload still wrote the key — a refine would then resume from an object that does not exist. The stills loop twenty lines below already had the correct pattern. |
| `{{bookinglink}}` printed raw on patient-facing posters | The token regex and the substitution switch are two lists that have to agree by hand. The regex matched six spellings; the switch handled four. |
| Onboarding silently wiped a saved booking link | `OnboardAsync` treated an absent field as "clear it", `UpsertAsync` as "leave it alone". Two write paths disagreeing about what silence means. |
| The QR was missing from PDF export, share-by-email and the admin image check | Four callers resolve a canvas independently, and only the one being worked on was updated. The share-by-email call site carries a comment recording the identical prior lesson. |
| The second screen ever paired returned a 500 | `uniq_pairing_code` was `Sparse`, which only skips documents where the field is *absent*. Pairing clears the code and writes an explicit `null`, so the first pairing put a null in the index and the second collided. Identical to a `stripeCustomerId` bug already fixed — and the fix, a partial index, was documented forty lines above in the same file. One screen worked, so a demo would never find it. |
| Campaign email arrived with no poster | The imaging library *throws* on an SVG logo rather than returning nothing, so the guard against an undrawable logo never ran. The send was recorded as successful. |
| Batches sized by recipient count, not bytes | Mailjet carries inline attachments inside *each* personalisation, so the poster uploaded once per recipient — 50 recipients × a 2 MB poster is one request of well over 100 MB against a 15 MB limit. Real defect, fixed — but **not** the cause of the send failure; that is the network path, still open. |
| Requests uploaded without a declared length | `PostAsJsonAsync` streams chunked, so a server that rejects a request and closes without draining it leaves us with `Broken pipe` raised from inside the JSON serialiser — naming the one component not at fault, and discarding the status the server was trying to return. |
| Campaign email named neither the pharmacy nor its details | The sending path never read the brand kit. |
| Granting access needed the button pressed twice | Rows were read before the load that filled them had run. |
| A refused action looked like a dead button | The refusal rendered at the top of a scrolling dialog, usually off screen. |
| "Finish setup" went to the dashboard | Setup-outstanding was never recorded, and a missing value read as "already done". |
| Invitation and poster-share email undeliverable | Hardcoded an unverified sender; Mailjet refused it at the provider, invisible in-process |
| "Forgot password?" link went nowhere | Route check only matched `href="/..."`, so a fragment was never a candidate |
| Template download rendered `null` | Non-JSON response through a JSON-only proxy |
| Every pharmacy told it was a branch | `Ok(null)` became 204, and the client read that as "yes" |
| Patients and pharmacies pages unreachable | Rendered fine; nothing linked to them |
| Two sidebar menus rendered see-through | A colour name that had never been defined. A missing name produces no styling and no error. |
| Signed-out users told they were managing another pharmacy | The label's cookie lasted thirty days; the session it described lasted thirty minutes. |

## Test coverage

Every check was verified by re-injecting the bug it was written for. A check
that has only ever seen a working system is a check nobody knows is wired up.

- **9 static checks** on every build — route links, client/API contract, BFF
  proxies, e2e helpers, dead UI controls, brand names, undefined theme colours,
  sign-in shell integrity, and duplicated copies of Radix's document-scoped
  modules (see the freeze below — the symptom is a page that renders perfectly
  and cannot be clicked, which no other check could see)
- **416 test methods** covering rules in isolation (`Acme.Tests`), which expand
  to roughly **582 cases** because 55 of them are table-driven. Both numbers are
  counted from the source (`[Fact]` plus `[InlineData]`), not from a runner —
  see "What has actually been run" under Outstanding for why.
- **126 test methods across 19 classes** against real Mongo
  (`Acme.IntegrationTests`), including every email path, campaign dispatch and
  Stop, and the security questions display screens raise
- The attached-image rules are covered head-on (`VideoImageTests`): which files
  are accepted, how many, whose keys may be named, and which of them a delete
  may remove. The ownership cases are the ones worth keeping — they are the
  difference between a feature and a cross-tenant hole.
- **80 Python test methods across 8 files** (`apps/ai-service/tests`), expanding
  to **117 cases** once the parametrised ones are counted — the narration
  provider rule, the narration word budget, the model-reply boundary, what a
  failed render reports to a pharmacist, and which beats a pharmacist's own
  photographs take. Both figures are counted from the source by walking the AST,
  for the same reason the C# ones are.
- **123 Playwright tests** driving the real screens. That is
  `playwright test --list`, not a count of `test(` in the source: the
  page-access cases are table-driven and expand to one per route per role, so
  grepping undercounts by more than half. Two run in a browser that has never
  signed in, which is the only honest way to test a wall-mounted device.
- **Ratchets** — no email may name its own sender; every email path must name a
  test that exists
- **Source-reading tests**, for faults that live between functions rather than
  inside one, where the moment of failure is a database write or a print job
  nobody is watching. Four of them now:
  `VideoDocumentOwnershipTests` reads `MongoVideoRepository.cs` and asserts in
  both directions — the API writes nothing the worker owns, and leaves out
  nothing the domain owns. `BrandAssetCallSiteTests` reads every
  `BrandCanvasAssets.ResolveForRender` call site and asserts all four arguments
  are passed, since the two optional ones fail silently: without the profile a
  poster prints its tokens literally, without the encoder the booking QR is a
  gap. `EmailSenderTests` is the ratchet above. And `RefineStageContractTests`
  reads **three files in three languages** — `stages.py`, `worker.py` and
  `packages/shared-types/src/video.ts` — because the refine pipeline is defined
  once in C#, mirrored in Python so the classifier can name the stages, and
  mirrored again in TypeScript so a textarea knows where to stop. What those
  copies disagree about, left alone, is which stages spend money and how long an
  instruction may be. They each also assert they found something, because a
  source scanner that matches nothing passes every assertion and reports
  success. Adding to any of these lists is then the visible, arguable step it
  should be — the same shape as the anonymous-endpoint list in the
  authorization matrix.
- **`IntegrationTest` implements `IAsyncLifetime`** and drains campaign
  dispatch after every test, so no test leaves a campaign in flight for the
  next one. `Public:ApiBaseUrl` is deliberately set to a public-looking address
  in `ApiFactory`, so the suite exercises the linked-poster path rather than
  silently taking the embed fallback.

**Known weaknesses in the suite itself**, documented in `TESTING.md`:

- The mail sender is replaced by a stand-in in every integration test, so
  `MailjetEmailSender` runs only in its own unit tests
- No test reaches the network, so anything that happens *between* here and a
  provider — a dropped keep-alive, a middlebox — is invisible to the suite by
  construction. `scripts/mailjet-probe.sh` exists because that gap cannot be
  closed from inside the test process.
- `check-contract.mjs` is vacuous for DTO field changes — proven by deleting a
  field and watching it pass
- The poster editor's `applyPharmacyInfo` has **no web test harness at all**.
  The booking token and QR work there was verified by reading and by hand,
  including a destructive-blanking fault that a test would have caught.
- The render worker's lease behaviour is covered by domain tests and by a
  Python simulation of the claim query, not by two real workers racing

**Why the campaign-email faults stayed invisible**, since it was asked directly
and the answer is structural rather than careless. Five reasons, and one cause
underneath them:

1. The sender is substituted in every integration test.
2. Every campaign test sent to exactly one recipient — and the size fault does
   not exist below two.
3. Nothing ever rendered the email. The tests checked that a send was
   attempted, never what arrived.
4. Configuration was never under test, which is precisely where the
   missing-poster fault lived.
5. The fake always succeeds, instantly; timing and failure were assumed away.

Underneath all five: **each test checked the thing that had just changed. None
asked what the recipient actually receives.**

## Campaigns cannot be sent from this network — open

**Not an application fault.** Something between this machine and
`api.mailjet.com` gives up on a request that takes more than about twenty
seconds, and the uplink is slow enough that a megabyte often does.

The proof is a response Mailjet did not send:

```
Received HTTP response headers after 20600ms - 502
<html><body><h1>502 Bad Gateway</h1>
The server returned an invalid or incomplete response.</body></html>
```

Mailjet's API answers in JSON, always. An HTML page means an intermediary
terminated the connection and replied on its behalf — which on a TLS connection
means something is inspecting HTTPS on this network.

Every observation fits one explanation once the *durations* are read rather than
the sizes:

| Bytes | Duration | Rate | Result |
|---|---|---|---|
| 252,240 | 1.7 s | 145 KB/s | 200 |
| 1,008,621 | 2.5 s | 394 KB/s | 200 |
| 1,008,893 | ~26 s | 38 KB/s | connection cut mid-upload |
| 977,045 | ~21 s | 46 KB/s | 502 from a gateway |

Anything finishing inside about three seconds succeeds; anything still uploading
at twenty seconds is killed. Upload rate varies roughly tenfold, so the same
payload does both.

**Three theories were wrong before this one**, and each is worth naming because
each produced a real fix that did not solve the problem:

1. *Batching by recipient count.* A genuine defect. The failing send had one
   recipient.
2. *A request-size ceiling.* Inferred from two points, then disproved by
   1,008,893 bytes failing where 1,008,621 succeeded — 272 bytes apart.
3. *A stale pooled connection.* Plausible, and the retry it justified fires
   correctly in the logs. A fresh connection does not make a slow upload faster.

**What the application now does about it:** an HTML error body is reported as
coming from an intermediary rather than from Mailjet, because the two need
opposite responses — one is ours to fix, the other is not.

**Mitigation, not a fix.** Lowering `Messaging:Mailjet:MaxRequestBytes` shortens
each request so it finishes inside the ceiling. At 250,000 each request carries
one recipient and takes about six seconds at the worst rate observed. That is
fine for a handful of recipients and does not scale: a large campaign over a
40 KB/s link is slow whatever the chunk size, because the total is unchanged.

**What has changed since**, and it changes the size of the problem rather than
its nature: the poster now travels as a signed link, so the **total** is 3.7 KB
per recipient instead of 244 KB. A 1,000-recipient campaign is 3.7 MB, which is
about a minute and a half even at the 40 KB/s worst case — and it is now split
across many small requests from a background worker rather than one large one
holding an HTTP request open. Every individual request finishes well inside the
twenty-second ceiling. The network path is still wrong; it is no longer the
thing standing between us and sending a campaign.

**The actual resolution is still the network path** — the proxy, VPN or HTTPS
scanner doing the inspecting. `scripts/mailjet-probe.sh` confirms it with curl,
outside this application, and has still not been run.

## Outstanding

1. **Rotate the Mailjet key and secret committed in `appsettings.json`.**
   Verified still present. Carried across three reports and now the most
   urgent item, with nothing ahead of it.
2. **The working tree is not committed, and the build marker cannot tell you
   what is running.** `scripts/deploy.sh` builds from the files on disk but tags
   with `git rev-parse --short HEAD`, so a deploy of uncommitted work produces
   an image whose build id names a commit that does not contain it — two
   different images, one id. It now appends `-dirty-<timestamp>` and warns, but
   the real fix is to commit before deploying. Separately, ai-service on Railway
   reports a fixed `BUILD_ID` that has not moved across deploys: point it at
   `${{RAILWAY_GIT_COMMIT_SHA}}` or unset it.
3. `Messaging:UseMock` is `false` in the base `appsettings.json` — dev sends
   real mail.
4. Turn on `Auth:RequireEmailVerification` once verified accounts are the
   majority. Not currently set at all.
5. **AI design spend is capped per pharmacy but not per deployment.** The
   monthly allowance is enforced and a per-minute limit stops a loop, but
   nothing caps what the whole estate can spend in a day. Worth a ceiling
   before a large rollout; the per-call records in `ai_generations` are what a
   ceiling would be built from.
6. The legal pages exist and are linked, but **have not been reviewed by a
   solicitor** — particularly the lawful basis for holding patient contact
   details, and the list of processors.
7. **`Public:ApiBaseUrl` must be set to a genuinely public address in every
   environment that sends campaigns.** Unset, it falls back to
   `http://localhost:5050`, which the reachability check now catches — the
   poster is embedded and a warning is logged rather than the email silently
   arriving with a broken image. The fallback works; relying on it in
   production reintroduces the 244 KB-per-recipient cost.
8. **The voice provider's spend per video is not instrumented at all.** This
   blocks pricing a refine sensibly — see the product decision below.
9. **Video thumbnails now travel as signed links, not data URLs.** Fixed on
   18 September — Gmail strips `data:` from `img src`, so the still was simply
   absent for a large share of recipients. A `data:` URL is used only when the
   deployment has no address a mail provider's fetcher can reach, the same
   fallback the poster uses. What remains is that a campaign carrying several
   videos is still the largest thing left in the message.
10. **The ai-service image must be rebuilt, not restarted.** `app/` is baked in
   at build time and there is no source mount, so `docker compose up -d`
   silently keeps the last image. A container ran five days stale and answered
   404 to the new refine endpoint, which the API could only report to the
   pharmacist as "we could not work out what to change just now" — a deploy
   problem wearing the costume of a comprehension failure. Use
   `docker compose up -d --build ai-service`, then confirm with
   `curl -s localhost:8000/health`, which now returns the build that is
   actually answering. The boot log also lists every POST route the running
   image serves.
11. **What has actually been run, and what has not.** As of **22 September**:

   | Suite | Last run | Result |
   |---|---|---|
   | `pnpm test:ai` | 22 Sep | **117 passed, 0 failed** |
   | `pnpm smoke:selftest` | 22 Sep | 12 of 12 scenarios |
   | 9 static checks + web typecheck | 22 Sep | pass |
   | `pnpm smoke` (local stack) | 22 Sep | 24 passed, 1 warning, 0 failed |
   | Playwright | 18 Sep | pass |
   | `pnpm test:api` | **not in this period** | — |
   | `pnpm test:api:integration` | **not in this period** | — |

   The Python run clears the `tests/test_health.py::test_health` failure that
   had been on the record since 16 September, and is the first execution of the
   attached-image tests under a real runner rather than inspection.

   **The C# suites have not been run in this period, so every C# change —
   including the blob-key ownership fix and the whole picture re-cut lane — is
   inspection-verified only.** That is the largest gap on this page. Before
   deploying: `dotnet restore`, `pnpm test:api`, `pnpm test:api:integration`
   against a live Mongo, and the browser suite.

   Two caveats worth carrying. The smoke run above predates the last round of
   fixes to the runner itself, so the version described on this page has not yet
   been through a real deployment. And `pnpm install` is needed before the Radix
   `pnpm.overrides` take effect — until then `check-radix-layers` fails,
   correctly, because the duplicate copies are still on disk.

   A passing typecheck still says nothing about behaviour: the web canvas and
   the video pages have no test harness at all.

## Needs a product decision

- Should a pharmacy that skipped setup be allowed to send campaigns? Today it
  can, and the email goes out with no pharmacy name in the From line.
- Should a pharmacy that downgrades keep its display screens? Today it does —
  the limit is checked at registration only.
- **Should Stop be stronger than "within one group"?** Making it instant means
  abandoning a request already with the provider, which risks a partial send
  with no record of who received what. The current behaviour is the safer one
  and the interface states it plainly, but it is a choice.
- **Should refining a video charge the monthly allowance?** It should be
  decided from measured spend — per-video Replicate and ElevenLabs cost,
  editor-only re-encode time, and how often a refine actually reaches the
  scriptwriter or visuals rather than just the overlays. None of that is
  measured yet, and "The voice provider's spend per video is not instrumented
  at all" under Outstanding is why.

## Cleared

- **`pnpm smoke` — a pre-production smoke test.** One cheap call through every
  paid door against a *running* deployment: health and build id, the routes
  ai-service actually serves, sign-in through both the API and the web BFF,
  poster create + PDF/PNG export checked by magic bytes, one campaign email, one
  AI design, one video render polled to Ready, one screen pairing, and the
  pharmacy's own photograph actually reaching the video. It fails on a missing
  route, on a job that can neither finish nor fail, and on any customer message
  containing a traceback.
  <br>**The picture check had to be built to be un-foolable.** A file that
  cannot be read gives up its beat to stock footage and the render carries on —
  by design, so one bad JPEG never costs a video. That tolerance is what hides a
  dropped picture: Ready status, playable MP4, empty note. The first version
  checked for silence and therefore also passed a deployment with no attachment
  support at all, which says nothing and looks identical. It now uploads a real
  PNG *and* a file that is deliberately not a picture: a render that is actually
  looking must have something to say about the second, so silence is a failure.
  The damaged file goes first, because the opening beat is the only one
  guaranteed to be pinned. Written because every existing suite passed
  while production was broken — the suites run against source, production runs
  an image, and both mock the providers whose contracts changed. See
  `docs/SMOKE.md`.
- **`pnpm smoke:selftest` — twelve fake deployments.** The runner is exercised
  against a stub that can be told to break in each way that matters, because a
  smoke test that has quietly stopped noticing is worse than none: its green
  line is read as assurance. Every check added to the runner is mutation-tested
  against it — disable the check, confirm the scenario fails — and that is how
  two dead checks were caught after being written: one regex that could never
  match the message it was meant to accept, and one that read silence as
  success.
- **One place that builds an Anthropic client** (`app/llm.py`), and one that
  reads a model reply. Five call sites constructed their own; four passed
  `temperature`, which newer Claude models reject with a 400, and every one of
  them read `.content` as a string when Claude returns content blocks. Three
  production failures in two days came through that boundary and none of them
  failed a test first, because the suites mock the providers. The shapes are now
  tested directly in `tests/test_llm_boundary.py`.
- **Pharmacies can design posters with AI.** A brief in plain English comes
  back as a saved poster with their own details and brand filled in, opened
  straight into the editor to edit. Free 3 a month, Starter 50, Pro unlimited —
  the numbers the pricing page has always advertised, enforced for the first
  time against a per-call record rather than a proxy. A per-minute limit stops
  a loop spending an unlimited plan. Design only: AI *imagery* is billed per
  picture and stays staff-only, so generated posters use stock photography and
  the pharmacy's own logo, both free.
- **`MaxAiGenerationsPerMonth` is real.** It was on the pricing page from the
  beginning and checked nowhere, because the nearest countable proxy — posters
  created from an AI template — missed every design a pharmacist generated and
  discarded, which are exactly the ones that cost money. `/api/subscription`
  reported `-1` rather than lie about it; it now reports the true count, from
  the same query the limit uses, so the number shown and the number enforced
  cannot disagree.
- **One definition of the billing month.** `new DateTime(UtcNow.Year,
  UtcNow.Month, 1, …)` was written out by hand in seven places — poster, video,
  campaign, user and AI counters, plus analytics. They agreed, which is the only
  reason nobody noticed. Now `BillingMonth.StartUtc()`, so moving to
  subscription anniversaries later is one line rather than a hunt.
- **Refine with AI, end to end.** A finished video's card carries "Ask for a
  change"; the pharmacist types what they want in plain English, is shown what
  it would do, and nothing runs until they agree to that plan. Behind it, the
  pipeline re-enters at the earliest affected stage on a queue of its own, so a
  caption change is not stuck behind a ten-minute render.
- **Brand identity resolved into the video pipeline**, from one resolver shared
  with posters and screens — brand kit first, pharmacy profile filling the gaps.
  Re-stamped on Retry and on a refine, so a corrected pharmacy name reaches the
  next cut.
- **A booking QR the pharmacist can place themselves**, and a booking link for
  it to point at. The link is stored on the **brand kit** — it is what the QR
  encodes and what `{{booking_url}}` resolves to, and both are laid out from the
  kit, so keeping it on the onboarding profile put it where nobody could find
  it. The profile displays it and links across; it cannot write it. Existing
  links are moved by a startup migration, which creates a brand kit for any
  pharmacy that had a link and no kit — which is why a pharmacy registration
  number is no longer required to save one.
- **Replace image is gone from the logo and QR shapes.** It set an uploaded
  picture on a shape whose id `applyPharmacyInfo` resets unconditionally, so a
  deliberate choice was deleted by a button pressed elsewhere with nothing said
  either time. Both now explain where the picture comes from instead.
- The six dead links on the marketing site. Every public link now points at a
  page that exists.
- Terms-of-service and privacy-policy pages exist and are reachable (content
  still needs review — see Outstanding).
- **The email poster's double-width rendering.** It is now rendered separately
  for email and linked rather than embedded — 244 KB per recipient to 3.7 KB.
- **The cause of twenty failing super-admin browser journeys** — the onboarding
  lockout. One product fault presenting as twenty test failures, which is why it
  read as test infrastructure. Not yet re-run to confirm; see Outstanding.

## Next

- **Open the paid refine lanes.** The staging machinery is built and tested in
  full, and every lane that spends is deliberately closed: a pharmacist asking
  for anything beyond a re-cut is told plainly that we have not opened it up
  yet. Opening one is a pricing decision waiting on the voice-provider spend
  instrumentation under Outstanding, not a piece of engineering —
  `RefineStages.IsEnabled` is pinned by a test so it cannot drift in unnoticed.
- **Meter the refine classification itself.** It runs before a video is chosen,
  so it lands outside the per-render usage ledger. Small per call, and the one
  call a pharmacist can make repeatedly without a render happening.
- Google Business Profile publishing (approved, not built)
- Google Ads campaign creation wizard
- Video on display screens

# Progress update — 26 October to 3 November

The theme of this period is **social publishing beyond Facebook**: posts that
repeat on a schedule, YouTube, and LinkedIn — which went live in production on
30 October with a real post from the app.

---

## What a pharmacy can now do

**Find things in any list.** Templates, My Posters, Videos, Campaigns, Contacts
and the admin tables share one Sort (newest / oldest) and Date (all time, last
7 or 30 days, this month, custom) toolbar. The view is kept in the address, so
a link opens the same view. Filtering happens in the database, not the browser.

**Post on a schedule.** *Recurring posts* (Starter 2 active, Pro 20): a poster
or video, the accounts, and daily / chosen weekdays / every few days / specific
dates at a local time. Clock changes are handled; a post more than two hours
late is skipped rather than sent late; one broken account misses only its own
post. Pause, resume, end, and edit future posts only. Emails for posted,
failed or needs-reconnecting, and paused.

**Post videos to YouTube** as Shorts. The caption's first line is the title.
Uploads stay private until Google audits the project.

**Post posters and videos to LinkedIn.** Profiles now; company Pages are built
and switch on once LinkedIn approves the app. LinkedIn's 60-day sign-in is
shown from two weeks before it runs out, with a one-click Reconnect that
renews the profile and its Pages together.

**Post videos to TikTok** (built 3 November, in TikTok's sandbox). The share
screen asks, as TikTok requires, who can see each post, whether others may
comment, Duet or Stitch, and whether it is commercial content. Until TikTok
audits the app, posts are private.

**Post posters and videos to X** (built 6 November). Connect an X account in
Social & Ads and share to it like LinkedIn (one post at a time: X refuses repeated text, so it is not offered for recurring posts).
The share dialog counts the caption X's way (280, links 23, emoji 2) and
stops a post X would refuse. X's API is now pay-per-use, so each post uses a
little of the credits bought in the X Developer Console.

---

## Faults found and fixed

The ones that reached production:

- **Facebook connect refused from `www.marketing25.co.uk`.** The `www` address
  now redirects to the one address OAuth is registered for.
- **LinkedIn's redirect URL was not registered** — caught by the production
  smoke test on 29 October, fixed in the LinkedIn app.
- **AI video failed on Railway with a 401** — a key pasted with stray quotes or
  spaces. Keys are now cleaned on load and logged by fingerprint only.

Found in review, before anyone hit them:

- Reconnect after sign-in ticked nothing, so it renewed nothing ("Connect 0").
  Everything found is now ticked.
- Disconnecting a LinkedIn profile could have revoked the sign-in its Pages
  still used.
- LinkedIn would have read "(open till 9pm)", a # inside a link, or an emoji at
  the length limit as markup or cut it badly.
- Disconnecting Google Ads could have revoked a YouTube channel's grant.
- A YouTube upload could outlive the worker's lease and be marked Uncertain.
- Retry messages said "Trying again automatically" twice.
- The recurring-post preview endpoint was missing from the audit log rules.
- The privacy policy and data-deletion page did not name YouTube or LinkedIn;
  the home page claimed Google Business Profile posting, which does not exist.

---

## Testing

- New .NET tests: sort/date (unit and real-database), recurring posts (clock
  changes, two schedulers, late skip, pause, plan limits, emails), YouTube and
  LinkedIn (every failure class, uploads, connecting, daily check),
  disconnect rules.
- New web tests: list filters, the recurring form, reconnect and expiry.
- Smoke: YouTube and LinkedIn connect checks, LinkedIn sign-in expiry, and
  opt-in real posts (`SMOKE_YOUTUBE_PUBLISH`, `SMOKE_LINKEDIN_PUBLISH`). Six
  new self-test scenarios.
- QA script: sections 10a (social accounts), 10b (recurring) and 10c (lists).

**What has actually been run.** On your machine: `pnpm test:api` and
`pnpm smoke` against production (51 passed; the one failure, LinkedIn's
redirect, has since been fixed), and a real LinkedIn post. Here, without .NET
or a browser: the web typecheck, the web helpers' tests, the static checks and
the smoke self-test scenarios. **Run `pnpm test:api` again** — the LinkedIn
tests and the latest fixes have not yet been compiled.

---

## Still to do

- **Production settings:** `Readiness__Token` / `READINESS_TOKEN`,
  `Edge__ClientIpKey` / `CLIENT_IP_SIGNING_KEY`. (No Google Ads developer token is
  needed any more: Google moved access to the Cloud project — request Basic access there.)
- **Videos run about 24s for a 15-second product** (smoke WARN). Needs the
  "Script overran" line from the ai-service log.
- **Approvals:** Meta App Review (Live mode), LinkedIn Community Management
  (Pages), Google's YouTube audit and app verification, TikTok's audit.
- **TikTok:** test in the sandbox, then apply for TikTok's Content Posting API
  audit (needs a demo video of the share screen).
- **X:** create the app (Web App, Read and write, callback
  `…/social/callback/x`), set `Social__X__ClientId` / `ClientSecret`, buy
  credits, then connect once by hand (QA 10a.18–10a.23).
- **Next build:** Phase 2 Autopilot.

---

# Progress update — 23 to 26 September

The theme of this period is **asking a finished video for a change, and having
it actually happen.** At the start of the week that did not work. Every
typed change came back with "we could not work out what to change". When that
was fixed, changes were refused for reasons that were wrong ("no moment shows
medicine"), or were made to the wrong shot, or were refused silently so the
video just reloaded unchanged. Each report was real, and each fix uncovered the
next one on the same video.

The root cause was structural. **The list of shots the refine worked from was
not the list of shots in the video.** It was rebuilt each time from records that
drifted out of step, then matched against a sentence by a model reading
stock-library titles. The fix was not another patch to that list. The
pharmacist now **taps the shot they want to change**.

The video changes went to production this week. The smoke test against
production passed 26 of 27 checks. The one warning, a 15-second video running
to 22.8 seconds, has since been fixed.

---

## What a pharmacy can now do

**Change a shot by tapping it.** *Ask for a change* opens with every shot of
the video as a tile. Each tile shows a still of the shot as rendered, its time
("Shot 4 · last · 0:15–0:20"), the words spoken over it, and "Your photo" where
relevant. Tap up to six, say what each should show, and press *Change … and
re-cut*. There is no preview step and no charge. The voice and the words are
kept, and it does not use a video from the allowance. Leave the box empty and
the shot gets something on the video's own topic. If the stock library has
nothing for what was asked, another shot from the same video is reused rather
than something unrelated.

**Name a shot by position.** "Change the last shot to people walking" and
"shot 2 to a salad" still work typed. A request that names no position, such as
"the one with medicine", is answered with *tap the shot*. The system never
guesses a shot from a description.

**Frame their own photographs.** Each attached photo can be dragged to choose
what is kept and zoomed out (down to half size) to fit more of it in. The gaps
are filled with a softened copy of the same photo, never black bars. The
thumbnail is exactly what the video shows, because the browser and the render
use the same geometry, pinned together by a 467-row table of expected answers.

**See when a change was not made.** A change that cannot be made now says so:
a message when it finishes, and "Last change not made: …" on the video card.
Previously the video simply reloaded unchanged.

---

## Faults found and fixed

The ones that reached a pharmacist:

- **Every typed change failed** with "we could not work out what to change".
  The import the classifier needed had been pasted inside a comment block, so
  every request crashed, and the crash was reported as a comprehension failure.
  A new check now scans the whole service for names used but never defined.
- **The shots on screen were not the shots the refine knew about.** A script
  planned for 15 seconds was voiced at 19.8, and the editor filled the gap by
  appending extra clips that belonged to no moment. "The last shot" pointed at
  one of those. The narration is now shared across the moments, so every shot
  on screen is a tile.
- **"Your own photo" was refused as unchangeable,** in a video that showed
  stock at that point. Photos added by a change that never happened stayed on
  the video's list, and each refine re-guessed their placement from that list.
  Placement is now read from what was actually rendered, and photos added by a
  change that does not happen are taken back off.
- **The same footage appeared twice**, reported as "a duplicate caption". Gaps
  were filled from a pool that started with clips already on screen, and a
  reused clip showed the same frames. Unused clips now go first, a repeat is
  cut from a different part of the clip, and a refine never brings back a clip
  the video already has.
- **Photo framing chosen at create never reached the video.** The dialog read
  the values and then left them out of the request.
- **Upright phone photos rendered on their side.** Browsers honour the phone's
  rotation tag; ffmpeg does not. The worker now straightens the photo first.
- **Dialogs taller than the screen could not scroll**, so with four framed
  photos the Generate button was out of reach.
- **Watermarked videos said "Sonar Marketing".**
- **Videos ran 22.8 seconds for a 15-second product** (production smoke). The
  word budget was requested in the prompt but never enforced. The writer now
  counts the spoken words and, over the limit, asks once for a tighter version
  that keeps the hook and the call to action. The tighter version is only
  accepted if its captions still match its words.

Found in review, before anyone hit them:

- A crashed change marked a finished video Failed.
- Adding a photo would have brought back one the pharmacist had replaced.
- One failed photo download would have removed that photo for good.
- The last tile was refused when an earlier moment had been skipped.
- Tiles showed the previous render's stills from the browser cache.

---

## Testing

This week added a rule, not just tests: **a regression test only counts once it
has been seen to fail with its bug put back.**

- **`pnpm test:prove`** puts **50 fixed bugs** back into the code one at a time,
  runs their tests, and requires a failure. Each test must first pass on the
  unmodified code, so a broken runner cannot report success. **50 caught,
  0 missed.** A review of the script found it once counted a runner that could
  not start as "caught"; that is fixed and recorded in `TESTING.md`.
- **`docs/REGRESSION-TESTS.md`** maps all 61+ fixed bugs to the test that
  guards each one.
- **The web app has unit tests for the first time: 42.** Before this week it
  had none, so none of its fixes were protected. They run in plain Node with a
  small purpose-built DOM, with no new dependency.
- **.NET: 631 tests, all passing** on the last full run. The run before that
  caught a new field I had not registered with an existing guard test. The fix
  was one line, and the guard now also stops the API ever overwriting the shot
  timeline.
- **ai-service: 234 test functions**, including real-ffmpeg checks that each
  shot's still is taken from its own moment and that a zoomed photo lands where
  the preview put it.
- **The smoke test now changes a video.** It checks that:
  - every shot has a tile;
  - a bad shot is refused;
  - a request with no position is refused, not guessed;
  - a tapped shot changes and nothing else does;
  - the length is identical (the voice was reused);
  - the allowance does not move.

  The self-test has **19 fake deployments**, up from 12. Each of the six new
  broken ones must block a release, and one designed fallback must only warn.
- **147 manual QA cases**, 41 of them for video.

**What has actually been run.** The .NET suite (631 passing) and `pnpm smoke`
against production were run on your machine. Here, in a sandbox without
pytest, .NET or a browser: every Python suite except `test_health.py`, which
needs FastAPI; the web tests and typecheck; the 10 static checks; all 19 smoke
self-test scenarios; and `pnpm test:prove`. **The smoke test's new refine
section has not yet run against production.**

---

## Needs a decision

- **Production is running uncommitted code.** The last commit is 11 September;
  210 files have changed since, and the production smoke reports build
  `791cb3f-dirty`. If the laptop were lost, the only copy of two weeks' work
  would be the images already deployed. Commit and push before anything else.
- **Should a rewrite of a finished video's words be allowed?** It costs about a
  cent for a normal video with a free voice. Recommendation: allow it, and
  count it as one video from the allowance. Carried.
- **The mail provider's key and secret are still committed to the repository**
  and have not been rotated. Carried.

## Known gaps

- **Videos made before this week show numbered tiles, not stills**, until their
  first change. Their shot records were written the old way, and matching shots
  on them is best effort.
- The browser test suite (Playwright) has not been rerun against the shot
  picker.
- A model can still ignore the word budget twice; the video is then longer
  than ordered, and the smoke warns.

---

# Progress update — 19 to 22 September

The theme of this period is **the difference between a test passing and a thing
working.** Three separate checks in this repository reported success while the
thing they were watching was broken, and each failed in its own way: one asked a
question that could never be answered "no", one read silence as agreement, and
one reported a number that could not change. None of them were careless. All
three are the same mistake at different addresses — a check whose green state
also covers the failure it exists to catch.

Two features landed alongside that: **photographs in videos**, and **adding
photographs to a video that is already made.**

---

## What a pharmacy can now do

**Put its own photographs in a video.** Up to four — the shopfront, the team,
the brand logo, a poster already published — attached when the video is created.
They are pinned to particular beats: the opening always, the closing
call-to-action next, the rest spread through the middle. Everything else stays
stock footage. No image model is called, so there is **no extra charge**; the
Pro AI-imagery toggle is a different thing entirely. Photos are cropped to fill
the tall frame rather than squashed.

**Add photographs afterwards.** *Ask for a change* now takes pictures as well as
a sentence. They join whatever the video already has, up to four, and the video
is re-cut with them in — the words, the voice and the rest of the footage kept.
It costs nothing and does not use another video from the monthly allowance.

A picture that cannot be read gives up its moment to stock footage and the
render finishes. That is deliberate: a bad file must never cost a pharmacist the
video *and* the allowance it spent. The pharmacist sees nothing about it; the
technical reason goes to a support-facing field.

---

## The freeze

Reported as "the page freezes and I have to refresh". It was not a hang — the
page was rendering perfectly and could not be clicked.

Two copies of one Radix module were installed: the one that makes a modal modal
by setting `pointer-events: none` on the page body and restoring it on close.
Both the saved value and the count of open layers live in module scope, so two
copies means two bookkeepers writing the same property with no knowledge of each
other. Open the ⋯ menu on a video card, choose *Share*, close both, and the
second copy restores `none` permanently.

It happened by accident and was invisible from the repository: Radix pins its
internal dependencies to *exact* versions, so two Radix packages from different
release trains is not a conflict a package manager can resolve. It installs both
and is right to. A ninth build check now fails if a second copy returns, and it
found two more duplicated modules of the same kind while being written.

---

## Three things that were not being checked

**A blob key was trusted** — this one was a missing check rather than a broken
one. The attached-image key arrives in the request body,
and it is used twice — the render worker downloads it, and deleting the video
deletes it. Nothing verified it belonged to the caller. Naming another
pharmacy's file would have rendered their picture into a video you could
download, and destroyed their file when you deleted it. Found by asking what
delete would do with a legitimate case the feature is specified to support.

**A boot marker that could not change.** The ai-service prints a build id so a
stale container can be spotted. Unset, it fell back to the modification time of
a single file — one that rarely changes. A rebuild that added a module and
rewrote the worker left the marker reading five days old, and it was believed
over the rebuild more than once. Exactly the fault the marker was written to
prevent. It now reads the whole source tree.

**A smoke check that read silence as success.** The new check asked whether the
render had complained about the attached picture. A deployment with no
attachment support does not complain either — it has nothing to complain with —
so a system where the feature did not exist at all passed. The smoke now uploads
a real photograph *and* a file that is deliberately not one: a render that is
genuinely looking must have something to say about the second, so silence is now
a failure.

---

## Testing

- **416 API test methods** covering rules in isolation, expanding to roughly
  **582 cases**, plus **126 methods across 19 classes** against a real database.
- **80 Python test methods** across 8 files, **117 cases** once the parametrised
  ones expand.
- **9 automated checks on every build**, and **119 manual QA cases** — ten new
  ones covering attached photographs, including two S1 cross-tenant cases.
- **12 fake deployments** in the smoke self-test, up from nine. The checks that
  gate a release were mutation-tested against them — disable the check, confirm
  the scenario now fails — specifically the "picture was used" read, the
  oversize-refusal wording, and both new scenarios. That is how two of the three
  faults above were caught: after being written, before being trusted. The
  branch for a deployment that drops the connection mid-upload is *not* covered
  by a scenario; it was verified by hand against a socket that hangs up.

**What has actually been run.** `pnpm test:ai` — **117 passed, nothing failed**,
which also clears a `test_health` failure that had sat on the record since 16
September. The self-test passes all twelve scenarios; the nine static checks and
the web typecheck pass. `pnpm smoke` last ran against the local stack on 22
September: 24 passed, one warning, nothing failed — and that run predates the
last round of fixes, so the current state of the runner has not been through a
real deployment yet.

**The C# suites have not been run in this period.** Every C# change here — the
blob-key ownership fix, the whole picture re-cut lane, the size-cap ceiling — is
inspection-verified only. That is the biggest single gap in this update. **The C#
suites have not been run in this period**, so every C# change — including the
blob-key fix and the whole picture re-cut lane — is inspection-verified only.
`pnpm install` is needed before the Radix fix takes effect.

---

# Progress update — 13 to 18 September

The theme of this period is **the deployment, not the code.** Every suite in the
repository was green while production was broken — and not through carelessness.
The suites run against source; production runs an image. The suites mock the
model and voice providers, correctly, because nobody wants a test run that costs
money. The consequence is that *what a provider actually sends* was untested,
and three production failures in two days came through exactly that boundary.

So the headline is a new kind of test: **`pnpm smoke` makes one real call
through every paid door of a deployment that is actually running**, reads what a
customer would read, and fails on the things a green suite cannot see.

Two customer-facing features landed alongside it: **posters designed by AI**,
with the plan limits enforced for the first time, and **"ask for a change" on a
finished video.**

And three faults are worth reading even if you skip everything else — hosted
video had **never** been able to narrate, videos were nearly **twice** the
length they were sold as, and a render could freeze for ever on a field that was
`null` instead of absent.

---

## New — a pharmacy can design a poster with AI

A brief in plain English comes back as a finished, saved poster with the
pharmacy's own details and brand already in it, opened straight into the editor.
Free 3 a month, Starter 50, Pro unlimited.

**Those numbers have been on the pricing page from the beginning and were
checked nowhere.** The nearest countable thing — posters created from an AI
template — missed every design a pharmacist generated and then discarded, which
are precisely the ones that cost us money. Each generation is now recorded as it
happens, the limit is enforced against that record, and the count shown to the
pharmacist comes from the same query as the limit, so the two cannot disagree. A
per-minute ceiling stops a loop draining an unlimited plan.

**Design only.** AI *imagery* is billed per picture and stays staff-only, so a
generated poster uses stock photography and the pharmacy's own logo, both free.

## New — ask a finished video for a change

A finished video's card carries "Ask for a change". The pharmacist types what
they want in plain English, is **shown what it would do**, and nothing runs
until they agree to that plan. Behind it the pipeline re-enters at the earliest
stage the change actually affects, on a queue of its own, so a caption tweak is
not stuck behind a ten-minute render.

**Every lane that would spend money is deliberately closed.** A pharmacist
asking for anything beyond a re-cut is told plainly that we have not opened it
up yet. Opening one is a pricing decision, not a piece of engineering — and it
is waiting on the spend measurement described under *Needs a decision*.

---

## Fixed — every hosted video render died at the voice stage

This one had never worked in a hosted environment, and it looked perfect on a
laptop.

The narration provider was chosen from the **voice id**, not from whether we had
a paid account. So setting the ElevenLabs key in production changed nothing — the
default voice was a free Microsoft one, the id decided the branch, and the
pipeline fell through to the free provider. That provider is an unofficial route
into Microsoft's consumer speech service, and Microsoft does not serve it to
datacentre networks. Every render on Railway died at the voice stage.

Three things combined to hide it: setting the key appeared to do nothing, the
fallback was silent, and the fallback led somewhere that *could not work in that
environment* — which turns a clear "the paid provider is unavailable" into a
confusing failure further down the line.

**The rule is now one line: if an ElevenLabs key is configured, ElevenLabs
narrates. No fallback.** If it fails, the pharmacist is told the provider's own
reason rather than a symptom from somewhere else.

**Fixed with it: the voice list was lying about price.** The catalogue lived as
two hard-coded arrays in the browser, and the browser worked out from a voice id
whether a render would cost anything. It went on offering a free voice on
deployments where that voice could never be used, and estimating a tenth of what
the render would actually cost. Only the render service knows whether the key is
set, so it now answers the whole question — which provider, which voices, which
default, what it costs — and the browser displays the answer without deciding
anything.

## Fixed — videos were 27 seconds when the product said 15

The scriptwriter's instructions carried "140-160 words for a 60s Short" as a
fixed constraint while the request asked for a 15-second clip. Given a hard word
count and a contradictory duration, the model split the difference: a measured
render produced **26.7 seconds** of narration for a clip the product calls 15
seconds, on every screen and in the default.

Nothing downstream trims it — the finished video is exactly as long as its
narration — so those twelve extra seconds were paid for over and over: stock
footage fetched, frames composited, seconds encoded, megabytes stored. The
encode alone was 91 seconds of a 208-second render. **The only symptom anybody
ever noticed was "videos take a while."**

The word budget is now derived from the length that was actually ordered, so
changing one cannot leave the other behind. A test pins the derivation, and the
smoke test reads the real duration out of the finished MP4 rather than guessing
it from the file size.

## Fixed — a render could freeze for ever on a field that was null

Four fields on a video record are written by the render service. On records
created before those fields were made optional, they were stored as an explicit
`null` rather than left out. The database will happily create a list on a field
that is *absent* and refuses one on a field that is *null* — and the write that
hit it was on the success path *and* the failure path, so the render could
neither finish nor record that it had failed. The customer watched a progress
bar for ever.

A startup repair removes those nulls, and the smoke test now treats "still
going and no longer moving" as a hard failure, because that is the worst state
a job can be in.

## Fixed — campaign emails arrived with an empty space above the watch button

The video's still image was a base64 `data:` URL, and **Gmail strips those from
image tags.** It works in Apple Mail and Outlook desktop, which is exactly why it
survived testing.

The poster in the same email had already been moved to a signed link for this
exact reason, and the thumbnail was left behind — one question ("can a mail
provider's fetcher reach this?") answered twice, forty lines apart. The watch
link worked throughout, which made it read as a layout problem.

## Fixed — Duplicate opened a page with no way to save

Duplicating an admin template opened it in a scratch stage — add rectangle, add
circle, add text, no properties panel and, the part that mattered, **no way to
save anything.** Publishing a template needs a name, a category and tags, and
that page has none of them, so a duplicate could be edited and then only lost.
Both Duplicate and the refine entry point now go through the composer, which can
name, edit and publish.

---

## New — `pnpm smoke`, and a self-test for it

One cheap call through every paid door, against a deployment that is actually
running. It signs in — once against the API and once through the web app's own
session layer, which an API-only check never touches — creates a poster **from a
published template**, publishes it, exports the PDF and PNG, sends one campaign
email, designs one poster with AI, renders one short video and pairs one display
screen. Then it deletes what it made, unless something failed, in which case it
leaves the evidence in place.

The details are where the value is, and each one comes from a real miss:

- It checks the routes the running image actually serves. A container ran five
  days stale and answered "not found" to an endpoint that had shipped days
  earlier; the app could only report that to the pharmacist as *"we could not
  work out what to change just now."*
- It checks the exported PDF's first bytes **and its size**. The first green run
  produced a 1 KB PDF — a valid, correctly-signed, entirely blank page. It
  passed, and proved nothing.
- It reads the finished video's real duration out of the file.
- It fails a hosted deployment that reports the free voice provider, in 200
  milliseconds, rather than discovering it three minutes and two model calls
  later.
- **It fails when a customer-facing message contains a traceback.** "The video
  failed" is not a release decision; "the video failed and showed the pharmacist
  `expected string or bytes-like object, got 'list'`" is.
- A failed render now carries two messages: the sentence the pharmacist reads,
  and a technical reason stored alongside it, so diagnosing a production failure
  does not require access to the host's log stream. That came from a real round
  trip where the actual reason was three clicks away in a log nobody running the
  test necessarily has.

**And the test has a test.** `pnpm smoke:selftest` runs the whole thing against
a fake deployment that can be told to break in each way that matters, and
asserts the exit code for each. A smoke test that has quietly stopped noticing
things is worse than none, because its green line gets read as assurance — and
this repository has been bitten by that shape twice already.

## New — a dedicated account for it, and a rule about its password

The smoke test used to sign in as the bootstrap super-admin. Two problems: every
run left posters, campaigns and videos on the one account an operator has to be
able to trust, and staff sit on the free plan — so the video and AI-design doors,
the expensive ones the test exists to check, reported "no allowance" and skipped.
**A green run that quietly skipped the paid half is exactly the reassuring
nonsense this test was written to stop.**

It now runs as an ordinary pharmacy user on a paid plan. The password shipped in
the example configuration is **refused by value** outside a developer machine —
not merely checked for length, because a nineteen-character password printed in
a public repository is the first one an attacker would try. Production can have
the account, but only with an explicit opt-in and a password that is neither
short, nor obvious, nor the published one. The seeder never touches an account
that already exists, so it cannot silently revert a rotated credential on the
next deploy.

## Housekeeping worth naming

- **One place builds an Anthropic client.** Five call sites built their own;
  four passed a setting newer models reject outright with an error, and all five
  read the reply in a shape the model no longer returns. Three production
  failures in two days came through that boundary and none of them failed a test
  first, because the suites mock the provider. The shapes are now tested
  directly.
- **One definition of "this month".** The billing month was written out by hand
  in seven places — poster, video, campaign, user and AI counters, plus
  analytics. They agreed, which is the only reason nobody noticed. Moving to
  subscription anniversaries later is now one line rather than a hunt.
- **The booking link moved to the brand kit**, which is what the QR encodes and
  what the token resolves to. On the onboarding profile it sat where nobody
  could find it. Existing links are moved by a startup migration.
- **A documentation sweep** — the README, the AI and deployment setup guides,
  the production checklist and the testing notes now state the narration rule,
  the smoke test and the release order, rather than describing the arrangement
  that caused the failures above.

---

## Testing

- **371 API test methods** covering rules in isolation, expanding to roughly
  **521 cases** because 51 are table-driven, plus **126 methods across 19
  classes** against a real database. Counted from the source, not from a runner —
  see the caveat below.
- **~99 Python tests** across 7 files, including new coverage for the narration
  rule, the word budget, the model boundary and how a failure is reported to a
  pharmacist.
- **Source-reading tests**, for faults that live *between* functions — now four.
  The newest reads three files in three languages, because the refine pipeline is
  defined once in C#, mirrored in Python so the classifier can name the stages,
  and mirrored again in TypeScript so a text box knows where to stop. What those
  copies would drift on, left alone, is which stages spend money.
- **8 automated checks on every build** and **107 manual QA cases**, unchanged.

**What has actually been run, stated plainly.** The browser suite was run on 18
September and reports passing. The last recorded Python run was 16 September and
**left one failure on the record** — the ai-service health check — which is
before several of the changes above and needs re-running either way. The C#
suites have not been run in this period; every C# change here is
inspection-verified only. That is the first job before any of this is deployed,
and it is deliberately not being reported as green.

---

## Needs a decision

- **The mail provider's key and secret are still committed to the repository.**
  Carried from four reports. Still the most urgent item on this list, with
  nothing ahead of it.
- **The working tree has not been committed since 11 September** — 146 changed or
  new files. The deploy script builds from the files on disk but tags the image
  with the last commit, so deploying uncommitted work produces two different
  images wearing one label. It now warns and marks the tag, but the real fix is
  to commit.
- **Which paid refine lanes to open, and what they cost.** The machinery is
  built and tested; the lanes that spend are closed. Deciding needs the
  voice-provider spend per video, which is still not instrumented.
- **A per-deployment ceiling on AI spend.** Each pharmacy's monthly allowance is
  enforced now, but nothing caps what the whole estate can spend in a day. Worth
  a ceiling before a large rollout; the per-call records are what one would be
  built from.
- **Should Stop be stronger than "within one group"?** Carried, still open.
- **The legal pages still need a solicitor.** Carried.
- **Should a pharmacy that skipped setup be allowed to send campaigns?** Carried.

## Known gaps

- The poster editor's own layout code still has no automated coverage; the brand
  and QR work there was verified by reading and by hand.
- Video thumbnails now travel as links, but a campaign carrying several of them
  is still the largest thing left in the message.
- The render service's lease behaviour is covered by domain tests and a
  simulation, not by two real workers racing.
- The refine classification runs before a video is chosen, so its cost lands
  outside the per-render ledger — small per call, and the one call a pharmacist
  can repeat without a render happening.

## Still to come

- Open the paid refine lanes, once pricing is decided
- Meter the refine classification
- Google Business Profile publishing (approved, not yet built)
- Google Ads campaign creation wizard
- Video on display screens

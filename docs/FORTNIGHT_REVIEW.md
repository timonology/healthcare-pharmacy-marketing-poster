# Two weeks of delivery — Marketing25

**13 to 26 September**

The through-line: **video became something a pharmacy can shape.** At the start
of the fortnight, hosted video could not narrate at all and ran nearly twice the
length it was sold as. By the end, a pharmacy can put its own photographs in a
video, frame each one, and change any shot of a finished video by tapping it.
This fortnight's work is on production, and a smoke test runs against it.

The previous fortnight was about scale and honesty. This one was about **the
gap between green and working**. Suites passed while production was broken,
checks passed while the thing they watched had failed, and a refine feature
reported success while changing nothing. The response was to make every check
prove itself: a test only counts once it has been seen to fail with its bug put
back.

---

## Headlines

- **Change a shot by tapping it.** *Ask for a change* shows every shot as a
  tile. Tap it, say what goes there, and it is re-cut. It is free, keeps the
  voice, and never guesses which shot was meant.
- **The pharmacy's own photographs in its videos.** Up to four, added at create
  or afterwards. Each can be dragged and zoomed, with a blurred fill instead of
  black bars, and what the thumbnail shows is what the video shows.
- **Hosted video could never narrate.** It does now. The free voice provider is
  refused from datacentre networks, and nothing chose the paid one.
- **Videos were 27 seconds, then 22.8, for a 15-second product.** The word
  budget is now derived from the ordered length *and* enforced after writing.
- **Posters designed by AI**, with the plan limits enforced for the first time.
  They had been on the pricing page from the start and checked nowhere.
- **`pnpm smoke`:** one real call through every paid door of a running
  deployment. It now also changes a video and proves the change happened.
- **Regression gate:** `pnpm test:prove` reintroduces 50 fixed bugs one at a
  time; all 50 are caught.
- **A security fix:** one pharmacy could name another's file as a video
  attachment, then download it inside a video and destroy it on delete.

---

## Video — from "cannot narrate" to "tap the shot"

### Getting a video made at all

- **Every hosted render died at the voice stage.** The provider was chosen from
  the voice id, not from whether a paid key was set. The default voice was free,
  and the free provider is not served to datacentre networks. The rule is now:
  a key is configured, so ElevenLabs narrates, with no silent fallback. The voice
  list and its price now come from the render service, which knows, instead of
  being guessed in the browser.
- **A render could freeze for ever** on four fields stored as `null` instead of
  absent. The database refused to append to them on both the success path and
  the failure path. A startup repair removes them.
- **Length.** The writer was told "140–160 words" for a 15-second clip and
  produced 26.7s. The budget is now derived from the ordered length. Production
  still produced 22.8s this week, because the budget was only *requested*. The
  writer now counts the spoken words and, over the limit, asks once for a
  tighter version that keeps the hook and the call to action. The tighter
  version is only accepted if its captions still match its words.

### The pharmacy's own photographs

Up to four photographs (the shopfront, the team, the logo, a published poster)
are pinned to particular moments: the opening first, the call to action next,
the rest spread through the middle. No image model is called, so there is no
charge. A photo that cannot be read gives up its moment to stock and the render
finishes, so one bad file never costs a video and the allowance it spent.

This fortnight added:

- **Framing:** drag to choose what is kept, zoom out to fit more in. The preview
  and the render use the same geometry, held together by a 467-row golden table
  and checked in real ffmpeg pixels.
- **Adding photos to a finished video**, free, keeping the voice.

Fixed along the way:

- Framing chosen at create was silently dropped.
- Upright phone photos rendered sideways.
- A fourth photo could vanish.
- A 20MB photo failed with no explanation.
- An 8.04MB photo was refused as "8MB".
- Deleting a video deleted the pharmacy's brand logo.

### Changing a finished video

"Ask for a change" arrived on 17 September: typed, previewed, and run only once
the pharmacist agrees. Every lane that spends money is deliberately closed. This
week it met real use, and the reports came in one after another. Each fix
exposed the next problem on the same video:

1. **Every typed change failed.** The classifier's import sat inside a comment
   block, so every call crashed, and the crash was reported as "we could not
   work out what to change".
2. **Refused changes were invisible.** The video reloaded unchanged. The card
   now says "Last change not made: …", and the page announces the outcome.
3. **Footage was matched to moments by list position.** That drifted as soon as
   one moment used a photo or borrowed a clip, so shots moved and photos
   vanished on re-cuts. Every clip record now names its moment.
4. **Duplicate footage.** Gaps were filled with clips already on screen, and
   repeats showed the same frames.
5. **"Your own photo" was refused as unchangeable** in a video that showed stock
   there. Photos added by a change that never ran stayed on the list, and each
   refine re-guessed their placement from it.
6. **The shots the refine knew about were not the shots on screen.** When the
   narration ran long, the editor appended filler clips that belonged to no
   moment, and "the last shot" was one of those.

The last one showed the real problem. A shot list rebuilt from drifting records,
then matched against a sentence by a model reading stock titles, was never going
to be reliable. **So the pharmacist now chooses the shot.** Every moment is a
tile with a still of the shot as rendered, so tapping a tile names a shot beyond
doubt. A typed request may name a shot only by position ("the last shot",
"shot 3"), read by code. The model that matched descriptions was removed rather
than left beside the new path. For a chosen shot, the search is the
pharmacist's words, then those words with the video's subject. If both miss, a
clip from the same video is reused. A refine never falls back to the generic
pharmacy list.

---

## Posters designed by AI

A plain-English brief comes back as a finished, saved poster with the
pharmacy's details and brand in it, opened in the editor. The limits (Free 3 a
month, Starter 50, Pro unlimited) had been advertised from the start and
enforced nowhere. The nearest countable thing missed every design generated and
discarded, which are exactly the ones that cost money. Each generation is now
recorded as it happens, and the limit and the count shown come from the same
query. Design only: AI *imagery* stays staff-only.

---

## Security and data

- **A supplied file key was trusted.** The attachment key arrives in the request
  and is used twice: downloaded into the render, and deleted with the video.
  Every key must now sit under the caller's own folders, and the refusal is
  identical whatever part of the key was wrong.
- **An earlier fix now has a test.** The "managing another pharmacy" label used
  to outlive the session it described, so signed-out users saw it. That was
  fixed before this fortnight; the web tests now guard it, along with clearing
  the delegated token on sign-out.

---

## Reliability of the tools themselves

- **The whole page froze** (it rendered but could not be clicked). Two copies of
  a Radix module each managed the page's pointer lock. Pinned, and a build check
  fails if a second copy returns.
- **The boot marker could not tell a new image from an old one**, and was
  believed over a rebuild more than once. It now reads the whole source tree.
- **A smoke check read silence as success.** A deployment without the photo
  feature passed the "photo was used" check. The smoke now sends a real photo
  *and* a broken file; a render that is really looking must mention the second.
- **Campaign emails lost the video thumbnail in Gmail**, which strips `data:`
  images. It now travels as a signed link, as the poster already did.
- **Duplicate opened a page with no way to save.** It now opens the composer.

---

## Quality and testing

| | 12 Sep | 26 Sep |
|---|---|---|
| .NET tests | 449 | **631**, all passing |
| ai-service test functions | ~99 | **234** |
| Web unit tests | 0 | **42** |
| Static checks on every build | 8 | **10** |
| Smoke self-test scenarios | — | **19** |
| Bugs proven caught (`test:prove`) | — | **50 of 50** |
| Manual QA cases | 107 | **147** |

The web app had no unit tests at all until this fortnight, so none of its fixes
were protected. They now run in plain Node with a purpose-built DOM, and the
first thing that DOM did was reproduce the "Maximum update depth exceeded"
crash seen on attaching a photo.

**`pnpm test:prove`** reintroduces each of 50 fixed bugs in turn and requires
its tests to fail. It first requires them to pass on the real code, so a broken
runner cannot report success. Reviewing it found exactly that: one run counted
a runner that could not start as "caught". `REGRESSION-TESTS.md` maps every
fixed bug to the test that guards it.

**What has actually been run.** On your machine: the .NET suite (631 passing)
and `pnpm smoke` against production (26 passed; one length warning, since
fixed). In the sandbox: the Python suites (all except `test_health.py`, which
needs FastAPI), the web tests and typecheck, the static checks, all 19 smoke
self-test scenarios, and `pnpm test:prove`. **Not yet run:** the new refine
section of the smoke against production, and the browser (Playwright) suite
against the shot picker.

---

## Where things stand

**Needs a decision**

- **Commit and push.** Production runs build `791cb3f-dirty`: the last commit is
  11 September, and 210 files have changed since. The only copies of this
  fortnight's work are a laptop and the running images. This is now the most
  urgent item.
- **The mail provider's key and secret are still committed to the repository**
  and unrotated. Carried since August.
- **Rewriting a finished video's words.** About a cent on a free voice.
  Recommendation: allow it and count it as one video from the allowance.
- **A ceiling on AI spend across the whole estate.** Per-pharmacy allowances
  are enforced; nothing caps a day's total. Carried.
- Carried unchanged: whether Stop should be stronger than "within one group";
  whether a pharmacy that skipped setup may send campaigns; the legal pages
  still need a solicitor.

**Next**

- Run the new smoke against production, then the browser suite against the
  shot picker.
- Brand logo on the video end card (text-only today; the raster conversion
  lives on the .NET side).
- Video on display screens.
- Google Business Profile publishing (approved, not yet built).

---

## Known gaps

- **Videos made before this fortnight** show numbered tiles, not stills, until
  their first change. Their shot records were written the old way, so matching
  shots on them is best effort.
- **The model can still ignore the word budget twice;** the video is then longer
  than ordered, and the smoke warns.
- **Voice-provider spend per video is still not measured precisely**, which is
  what the rewrite-pricing decision would lean on.
- The poster editor's own layout code, Stripe billing and the designer canvas
  still have no automated coverage.

---

# Two weeks of delivery — Marketing25

**30 August to 12 September**

The through-line: the product now survives contact with a real pharmacy. A
campaign can go to a thousand people. A poster can hang in a shop window. A
render that crashes can be recovered. And the things that were quietly reporting
success while doing nothing have been found and fixed.

The previous fortnight was about looking after the people who use the product —
recovering accounts, delivering invitations, working across several pharmacies.
This one is about **scale and honesty**: making the product do what it says at
the size it advertises, and making it stop claiming success when there isn't
any.

---

## Headlines

- **Campaigns work at the size we sell.** A thousand-recipient campaign went
  from a 244 MB upload that could not complete to 3.7 MB. Sending no longer
  blocks the request; it is queued and reported on as it goes.
- **Posters on a screen in the pharmacy.** A television or tablet on the wall
  loops published posters, paired once with a six-character code.
- **Stop now stops.** It previously reported success, ran to the last recipient
  and marked itself Sent.
- **The monthly allowance is no longer bypassable** by creating several
  campaigns at once.
- **Retry reaches the campaigns that need it** — those that reached some
  recipients and not others.
- **A crashed video render is recoverable**, without charging the pharmacy's
  allowance twice.
- **Booking link and QR code** on posters and shop-window screens.
- **A new administrator can now get into the admin console** — a lockout that
  would have hit the very first production deployment.

---

## Campaign sending

This was the largest and most consequential thread of the fortnight, and it
arrived as "error occurred while sending mail".

### What it was

Our mail provider carries a separate copy of any embedded image inside **each**
recipient's message. The campaign poster is embedded, and it was 244 KB. So the
upload for a campaign was `recipients × 244 KB` — 24 MB at a hundred people,
244 MB at a thousand, on a product that sells a thousand-recipient plan.

The failing log said `977,045 bytes` for **four messages**.

### What changed

The poster no longer travels inside the email. It is rendered once at a size
suited to email, stored, and the message carries a signed link.

| | per recipient | 1,000 recipients |
|---|---|---|
| Embedded at full quality (what failed) | 244 KB | 244 MB |
| Embedded, reduced | ~80 KB | 80 MB |
| **Linked** | **3.7 KB** | **3.7 MB** |

Three details worth knowing:

- **The link points at the image, not the poster.** Editing a poster next month
  must not change what an email sent today shows. An email that rewrites itself
  after the fact is not a record of what was sent.
- **Links last 180 days**, so a campaign stays readable in an inbox for about
  six months. The email also carries a "not showing?" row, because some clients
  block remote images by default.
- **There is a fallback, and it exists because we were caught by it.** A mail
  client does not fetch images through the reader's browser — Gmail fetches
  them through Google's servers. A link to an address reachable only from the
  office is a broken image for every recipient, and it looks perfect to whoever
  tested it on their own machine. The app now works out whether its public
  address is genuinely reachable from the internet, and embeds the poster with a
  loud warning if it isn't.

### Sending left the request path

Creating a campaign now queues it. A worker claims it, sends in groups, and
records per-recipient progress as it goes. Campaigns above 200 recipients ask
for confirmation; a single campaign is capped at 1,000. `docs/DECISION-campaigns-at-scale.md`
carries the measurements and the reasoning.

---

## Four faults that reported success

Grouped because they share a shape, and it is the worst shape a fault can have:
**the software said it worked and the pharmacist got the wrong outcome.** Nobody
reports what they never knew went wrong.

### Stop did not stop

A pharmacist pressing Stop saw it accepted. The campaign continued to the last
recipient and reported itself Sent.

The worker never re-read the campaign's status. Worse, its progress writes
replaced the entire record from a copy taken before the Stop, so the Stop wasn't
merely ignored — it was **erased**. Progress is now written field by field,
never touches status, and the worker checks after every group.

**The promise is now stated precisely:** Stop halts within one group, not
instantly. A group already handed to the provider will finish. Overstating this
in the interface would be a worse fault than the original.

### The monthly allowance could be walked past

Recipients were only counted once a campaign had finished. Five 500-recipient
campaigns created in the same minute on a 500-a-month plan would all go. Queued
and in-flight campaigns now count, as do the recipients a stopped campaign had
already reached.

### Retry was missing on the campaigns that needed it

The button showed only on campaigns marked Failed. A campaign that reached 700
of 1,000 is marked **Sent** — so the 300 who got nothing had no route, short of
a new campaign that would reach the first 700 twice.

### A deploy during a send marked campaigns Failed

Shutting the app down mid-campaign recorded the campaign as failed, in a state
the worker could not pick up again. An ordinary release on a Tuesday afternoon
would have stranded whatever was in flight.

---

## Posters on a screen in the pharmacy

A pharmacy can put a television or tablet on the wall and loop their published
posters.

**Installation** is a name, a location, a playlist with a dwell time on each
poster, one web address on the device and a six-character code typed once. No
app, no account on the device.

**Afterwards**, editing a playlist reaches every screen showing it within about
thirty seconds — nothing is pushed and nothing restarted. A head office can run
a campaign across every branch's screens from one edit. Unpublishing a poster
takes it off the wall, not just off the website. When the pharmacy's wifi drops,
the screen keeps looping what it last downloaded and shows a small amber dot
rather than an error a customer could read.

**The security position**, because a kiosk hangs in a room strangers walk
through: the device holds a credential that is a key to one screen's playlist
and nothing else — not the poster library, not the account. It receives only
what a passer-by could already read off the glass. Access is revocable from the
Screens page and stops the device on its next poll.

Only published posters can go on a screen. A screen in a shop window *is*
publication, so it follows the rule campaigns already follow rather than a new
one.

| Plan | Screens |
|---|---|
| Free | none — refused with an upgrade prompt |
| Starter | 1 |
| Pro | unlimited |

Setup instructions for pharmacy staff, including sleep timers and auto-launch,
are in `docs/DISPLAY_SCREENS.md`.

---

## Video generation — reliability

Video rendering is a long job in a separate service, and it had no recovery
story at all.

### A crashed render was permanent

If the render service restarted, deployed or ran out of memory, the video stayed
marked "Rendering" forever. The pharmacy's monthly video allowance had already
been charged. The customer had no way back and we had no way to find them.

A render now holds a **lease**: a claim with an expiry, renewed while the render
is demonstrably progressing. If the worker dies, the lease lapses and the video
becomes retryable — **without charging the allowance again**. A worker that is
merely slow keeps its claim, so two workers never render the same video, and a
worker that has lost its lease discards its result rather than overwriting a
newer one.

### Renaming a video destroyed its script

Two systems write to one video record. The app owns the name, status, ownership
and the lease. The render service owns the script, the generated title and
description, the tags, the segment timings and the cost record.

The app was writing the **whole record** on every change, built from a copy that
had never contained the render service's half. So every rename erased all of it,
silently, with the rename appearing to work perfectly.

Each side now writes only its own fields. A test reads the repository source to
prove it, in both directions: the app must not write anything the render service
owns, and must not *stop* writing something it does own — the second mistake
being the one a field-by-field write invites.

### Groundwork for refining a video

A finished video now keeps what a later refinement would need: the narration
audio, and references to the stock footage used. The narration is kept as bytes
because regenerating it costs real money on a premium voice; the footage is kept
as references because twelve HD clips is roughly 120 MB a video and the footage
is free to fetch again.

The refinement pass itself is designed and written up in
`docs/DECISION-video-brand-and-refine.md`, and is **not yet built**.

---

## Booking link and QR code

A pharmacy can store a booking web address on its profile. `{{booking link}}` on
a poster or screen prints it, and a QR slot draws a scannable code — so somebody
standing in front of a shop window can book from the pavement.

The address is validated on save: a bare domain gains `https://`, and anything
that is not a web address is refused rather than printed as nonsense on a
patient-facing poster.

**Two faults found while building it**, both in the seams:

- Several spellings of the token — `{{bookinglink}}`, `{{booking_url}}` — were
  recognised by one list and missing from another, so they printed as raw
  `{{...}}` on the poster. All six spellings now work, pinned by a test that
  walks every one.
- Completing onboarding silently wiped a booking link set earlier, because the
  setup form treated a field it does not ask about as a request to clear it.

The QR draws everywhere a poster does — editor, PDF export, share-by-email
image, screens. That sounds obvious and it wasn't: three of those four resolve
the poster independently, and the first attempt fixed only the one being worked
on.

---

## Fixed: a new administrator could not reach the admin console

A freshly created Marketing25 staff account was sent to the pharmacy setup form
and held there. Staff have no pharmacy, so there was nothing to complete and no
way past.

This deserves its own heading because of *when* it would have bitten: **the
first production deployment, on the first administrator, before anything else
could be done.** Staff accounts are now created already past setup.

---

## Quality and testing

- **449 API tests** — 327 covering rules in isolation, 122 against a real
  database.
- **123 browser tests** driving the real screens. Twenty administrator journeys
  were failing, and all twenty were the single lockout above — one product fault
  presenting as twenty test failures, which is why it read as a test-side
  problem for longer than it should have. The cause is fixed and the suite needs
  re-running to confirm.
- **8 automated checks on every build** — internal links resolve, browser and
  API agree on every route, downloads aren't mangled, the old brand hasn't
  returned, controls that look clickable do something, and every colour the
  interface asks for exists.
- **107 manual QA cases** in `docs/QA_TEST_SCRIPT.md`, with a PDF, alongside a
  full user guide in `docs/USER_GUIDE_FULL.md`.

**A caveat worth stating plainly:** the most recent changes have been written
and reviewed but the suites have not been run end to end since, and the QR work
adds a package that needs restoring first. Running them is the first job before
any of this deploys. It is not being reported as green.

### Why the tests did not catch the email faults

Asked directly, and it is worth recording the answer because it is structural
rather than careless:

- The mail sender is replaced by a stand-in in **every** test, so the code that
  actually talks to the provider had never run under test.
- Every campaign test sent to exactly **one** recipient — and the size fault
  does not exist below two.
- **No test ever rendered the email.** They checked that a send was attempted,
  not what arrived.
- Configuration was never under test, which is exactly where the missing-poster
  fault lived.
- The stand-in always succeeds, instantly. Timing and failure were assumed away.

Underneath all five: each test checked the thing that had just changed. None of
them asked *"what does the recipient actually receive?"* That question now has
tests of its own, and the reasoning is in `docs/TESTING.md`.

### Where the faults actually were

Every fault this fortnight was **at a seam**, not inside a function: two token
lists that had to agree by hand, two profile-writing paths that had to agree
about what silence means, four callers of one rendering routine that had to
agree about its arguments, two processes writing one database record. Nothing
was wrong in isolation. That is now the first place to look.

---

## Where things stand

**Needs a decision**

- **The mail provider's key and secret are committed to the repository.** The
  most urgent item here, carried from three reports. It needs rotating before
  the test environment starts sending.
- **Should Stop be stronger than "within one group"?** Making it instant means
  abandoning a request already with the provider, risking a partial send with no
  record. The current behaviour is safer, but it is a choice.
- **What should refining a video cost?** It should be decided from measured
  spend per video, and the voice provider's cost is not instrumented at all
  yet. Worth doing before the feature ships rather than after.
- **A pharmacy that downgrades keeps its screens.** The plan limit is checked
  when a screen is registered, not continuously. Blanking a shop window over a
  billing change seemed the worse outcome, but it is a commercial call that was
  made by default.
- **The legal pages still need a solicitor** — particularly the lawful basis for
  holding patient contact details and the list of companies processing data on
  our behalf. Carried.
- **AI generation limits are advertised but not enforced.** Carried.
- **Should a pharmacy that skipped setup be allowed to send campaigns?** Today
  it can, and the email carries no pharmacy name. Carried.

**Next**

- Refine with AI, on its own queue so a small change is not stuck behind a
  ten-minute render
- Brand details injected into video, as they already are into posters
- Google Business Profile publishing (approved, not yet built)
- Google Ads campaign creation wizard
- Enforcing address confirmation once enough accounts have confirmed
- A data processing agreement, which pharmacy customers may ask for before
  signing
- Video on display screens

---

## Known gaps

Being straight about what isn't covered:

- **The poster editor's layout code has no automated coverage.** The booking
  token and QR work there was verified by reading and by hand.
- **The voice provider's spend is not instrumented**, so the refine pricing
  decision has no numbers behind it yet.
- Stripe billing and the designer canvas still have no automated coverage.
- Campaign sending is exercised against a captured mail sender in most tests,
  which proves the message is built and addressed correctly, not that a
  provider accepts it.
- The test environment's demo accounts share one password. Set your own rather
  than using the repository default, which is known to anyone with access to
  the code.
- Nothing prevents a real patient address being typed into the test
  environment, which sends real email by design. The demo contacts use
  addresses that cannot receive mail, so the risk is a person, not the system.

# Testing

## Why this was rebuilt

Five bugs reached manual testing in a row while every suite stayed green. They
had one thing in common:

| What broke | Layer | Unit test? | API e2e? |
|---|---|---|---|
| `/patients` unreachable from the menu | web nav | no | no |
| `/pharmacies` unreachable from the menu | web nav | no | no |
| Every pharmacy told it was a branch | API → proxy → UI | no | no — the API was right |
| Template download rendered `null` | BFF proxy | no | no — hit the API directly |
| Poster PDF answered 404 | missing Next route | no | no — hit the API directly |

**All five were in the web layer or the seam between layers. None was in domain
logic — which is where all 133 tests lived.** The tests were concentrated
exactly where the bugs were not, so more of the same would not have caught a
single one.

The suites below are organised by where failures actually happen.

## What runs when

| Command | Needs | Time | Catches |
|---|---|---|---|
| `pnpm test` | nothing | seconds | everything static + unit |
| `pnpm test:api` | .NET | seconds | domain rules, authorisation matrix |
| `pnpm test:api:integration` | .NET + MongoDB | ~1 min | real HTTP, real queries, isolation |
| `pnpm test:browser` | full stack up | ~8 min | what a person actually clicks |
| `pnpm smoke` | a **running deployment** | ~3 min | one cheap call through every paid door |
| `pnpm smoke:selftest` | nothing | ~1 min | proves the smoke can still detect failure |
| `pnpm test:prove` | pytest + vitest installed | ~10 min | puts 50 fixed bugs back one at a time; each must turn its tests red |
| `./scripts/e2e/run.sh` | full stack up | ~1 min | cross-service wiring, email, lifecycles |

`pnpm test` deliberately needs nothing installed. The moment the everyday
command needs a database it becomes the command people skip, and a skipped
suite is worse than no suite because its name still appears in the output.

## The static checks

Cheap, run on every `pnpm test`, and each one exists because of a specific bug.

**`check-routes.mjs`** — every internal link points at a real page, *and* every
page is linked from somewhere. The second half is what catches an unreachable
page: `/patients` and `/pharmacies` both worked perfectly and no menu pointed at
them. Writing it also revealed the link scanner only matched `href=` (JSX) and
never `href:` (object literals) — so the two files that define every menu in the
app had never been scanned at all.

**`check-contract.mjs`** — the client, the BFF and the API agree on every route.
119 client calls checked against 56 Next routes and 137 .NET actions. Catches a
renamed endpoint, a missing verb export, a download link with no route.

**`check-proxies.mjs`** — the reply survives the trip back. A proxy must not
invent a value the API did not send (`result.data ?? true` was in all five
catch-alls), and every action returning a file must have a non-JSON route.

**`check-radix-layers.mjs`** — one copy of each module that keeps state about
the whole document. Radix's `DismissableLayer` sets
`document.body.style.pointerEvents` while a modal is open and restores it on
close, keeping both the saved value and the open-layer count in *module scope*.
Two installed copies means two bookkeepers writing the same property, and the
loser leaves the page rendering perfectly and completely unclickable until it is
reloaded. It happens by accident: Radix pins its internal dependencies to exact
versions, so two Radix packages from different release trains is not a conflict
pnpm can resolve. Counts **directories**, not versions — the same version under
two peer hashes is still two module instances, and counting versions would have
missed exactly that.

**`check-e2e.mjs`** — the shell suite's request helpers are used in the calling
shell. `body="$(req …)"` runs in a subshell, so the `HTTP_STATUS` it sets is
discarded; **25 status checks in `run.sh` had never worked**, comparing against
whatever the previous request left behind.

## Authorisation matrix

`apps/api/tests/Acme.Tests/Security/AuthorizationMatrixTests.cs`. Enumerates all
137 actions by reflection, so a new endpoint is covered the day it is written
rather than the day somebody remembers it.

It refuses:

- an endpoint with neither `[Authorize]` nor `[AllowAnonymous]` — open by
  omission rather than by decision
- anything under `/api/admin` without `AdminArea` or `SuperAdminOnly`
- an `[AllowAnonymous]` endpoint not on a reviewed list with a written reason
- a stale entry on that list, so it keeps meaning what it says

Writing it found `AuthController` had no attribute at all. It was anonymous —
correctly, since login cannot require being logged in — but by accident rather
than declaration, which in review is indistinguishable from a mistake. It now
says so explicitly.

## Integration tests

`pnpm test:api:integration`. Boots the real host against a throwaway database
(`acme_it_<timestamp>`, dropped afterwards). Real routing, real policies, real
Mongo driver — everything the unit tests replace with a fake is the part that
has actually broken.

- **`EveryEndpointTests`** — calls all 137 actions anonymously and asserts none
  answers; calls every `/api/admin` action as an ordinary pharmacy and asserts
  none answers; and calls the public ones to prove they *are* reachable. That
  last one is not a formality: an endpoint marked public that the pipeline
  rejects gives you a login page nobody can reach, and the "nothing leaks" test
  is structurally incapable of noticing.
- **`OwnershipIsolationTests`** — for each module, create as pharmacy A then
  read, list, modify and delete as pharmacy B. Ends by confirming **A still has
  the record**, because a bug that refuses everyone would satisfy every other
  assertion.

  Each module is a row in a table, so adding one is a line rather than a new
  test. Getting a row right means satisfying everything between the HTTP request
  and the record actually existing — which is more than the request DTO says.
  Campaigns took three passes:

  1. `posterId` is required (`NeedsPoster`)
  2. `Recipients`, `PatientIds` and `GroupIds` are each optional on the record,
     but the service refuses a campaign that resolves to nobody — optional
     individually, required together
  3. creation is gated on the subscription plan, so the owner has to be moved
     off Free first (`RequiresPaidPlan`)

  None of those is visible in `CreateCampaignRequest`. Each precondition is now
  a flag on the row rather than a patch, and the plan change goes through the
  real admin endpoint rather than reaching into the database behind the API.

  When a create fails the test **throws with the response body** rather than
  passing. That is what surfaced all three, one run at a time: a module whose
  fixture is wrong is a module that is not covered, and that has to be loud.

- **`AuthLifecycleTests`** — sessions: obtained, rotated, ended. Registering
  returns a token that works; a refresh token cannot be used twice; **replaying
  a rotated token revokes the whole chain**, including the legitimate session
  that replaced it; signing out kills the refresh token and does not touch
  anybody else's.

  It also pins the pipeline itself. `Signing_out_requires_a_session` exists
  because putting `[AllowAnonymous]` on `AuthController` to document that login
  is public silently unprotected logout — **`[AllowAnonymous]` overrides a
  method-level `[Authorize]`** — and nothing looked different, because the
  action returned 401 from its own body. Only a test at this level separates
  "the pipeline refused" from "the method happened to". The attribute is now
  applied per action, and `AuthorizationMatrixTests` keeps logout off the
  reviewed-anonymous list.

  Two token tests round it out: a valid token with one character of the
  signature changed, and an unsigned `alg:none` token claiming `SuperAdmin`.

- **`CampaignEmailDeliveryTests`** — the message the recipient actually gets.
  `CampaignEmailTests` in the fast suite covers the *builder*; this covers
  whether the running application reaches it with a poster, and whether the
  image survives rasterizer → blob store → signed link → an anonymous fetch.
  It asserts that the message carries **no** inline image, that the whole
  message stays under 32KB, that following the link with a client holding no
  credentials returns real JPEG or PNG bytes, that a token with one character
  changed returns nothing, that no PDF attachment remains, that a text part
  exists, and that the body stays under Gmail's ~102KB clip limit.

  The size assertion is the one that matters. The poster used to be an inline
  attachment, and Mailjet copies an inline attachment into every recipient's
  personalisation — 244KB each, measured, which made a four-recipient campaign
  a 977KB upload that the provider cut off. Re-embedding it is not a styling
  regression; it is a campaign that cannot be sent, and that assertion is the
  only thing in the suite that would say so.

  Two substitutions, both on `ApiFactory`. `CapturingEmailSender` keeps the
  `EmailMessage` instead of logging a line — otherwise every assertion about an
  attachment becomes an assertion about log formatting, and the real senders are
  already bypassed by `Messaging:UseMock`. `InMemoryPosterImageStore` replaces
  Azure, which is configured as `UseDevelopmentStorage=true` and so needs an
  Azurite emulator nobody is running. The rasterizer stays real.

  That second one is load-bearing. **`CampaignService` swallows a render failure
  on purpose** — a campaign whose poster cannot be produced still goes out with
  the text and video links, which is right for a recipient and useless for a
  test, because a missing emulator and a lost image look identical.

### Journeys (`tests/browser/journeys.spec.ts`)

`app.spec.ts` proves every page opens for the right roles, but it navigates by
URL after signing in once per role. **The login form was the only form the
suite ever filled.** Everything else — registration, signing out, creating a
poster, sending a campaign — was reached by `page.request.post(...)` or not at
all, so the API was covered and the controls that call it were not.

Eight journeys close that gap:

- registering through the signup form lands on `/onboarding`
- a half-finished signup **cannot reach `/campaigns`** — the middleware gate
- mismatched passwords and unaccepted terms are refused, **and no account is
  created behind the error**
- a wrong password is refused; signing out makes `/dashboard` bounce to `/login`
- "New blank poster" opens an editor with a mounted canvas
- a campaign sent from the dialog appears in the campaign list

Two of these deserve their reasons written down. *Signing out ends the session*
is covered at the API level by `AuthLifecycleTests`, but that test keeps passing
if the menu item is unwired — only a click proves the UI reaches the endpoint.
And *the terms have to be accepted* asserts the account does not exist
afterwards, because "still on /register" is also what a completely dead submit
button looks like.

The file raises its own timeout to 90s. The shared 30s budget fits a test that
opens one page; these sign in, submit, wait for a redirect and read a list, and
Playwright spends the same budget on the `beforeEach` hook.

- **`AccountEmailTests`** — the account mail and the flows behind it. Registering
  sends a welcome and a verification message; the verification link works and
  works **once**; requesting a reset sends a link that actually resets, after
  which the new password signs in and the old one does not.

  These tests **read the link out of the captured email and use it**, rather
  than asserting that a message was sent. "A reset email went out" proves less
  than it looks like — the URL inside can still be wrong, already expired, or
  pointing at another environment.

  Three of them exist for specific reasons rather than for coverage:

  - *Resetting signs the account out everywhere* — a second session is opened
    before the reset and must be dead afterwards. Otherwise whoever prompted
    the reset keeps access for up to thirty days and the reset achieves nothing.
  - *Requesting a second reset kills the first link* — without superseding,
    asking twice leaves two live links, and the older email is the one more
    likely to have been forwarded.
  - *An unknown address is answered the same way* — byte-for-byte the same
    status and body as a known one, and no mail sent. An endpoint that
    distinguishes the two sorts a breach dump into customers and non-customers.

- **`InvitationEmailTests`** and **`PosterShareEmailTests`** — the two paths that
  were actually broken, and the two that had no test looking at the message.

  The invitation tests pull the activation link out of the captured email and
  redeem it, and check that resending **supersedes** the previous link —
  otherwise resending, which is how an admin rescues an invitation that never
  arrived, would not revoke the old one. The poster-share tests assert the PDF
  is still attached and that its bytes start with `%PDF-`; that endpoint is now
  the only email in the product with an attachment, so an assumption that
  campaigns and shares behave alike would pass while the attachment vanished.

- **`EmailSenderTests`** (fast suite) — a source scan asserting that **no email
  names its own sender**, plus a ratchet requiring every email path to name a
  test.

  This one is unusual and worth explaining. Both senders resolve the sender
  message-first, so a non-empty `FromAddress` on the message overrides the
  configured, provider-verified one — and the rejection then happens at
  Mailjet, where nothing in the application can see it. `InvitationEmail` and
  the poster-share endpoint both hardcoded `no-reply@pharmacyposter.app` while
  the verified sender was `info@sonarinformatics.com`. Invitations were being
  created, tokens issued, and nothing delivered, with no exception anywhere.

  A behavioural test cannot catch that; the bug is a literal in a constructor
  argument. So the test reads the source, and checks the sender *constants* as
  well as the call sites — otherwise the literal just moves up one line.

  The second half is the more useful one. **Any file taking an `IEmailSender`
  must name the test that captures what it sends, and that file has to exist.**
  The invitation bug survived not because its test was weak but because there
  was none — the only invitation test in the suite parsed a CSV. Coverage was
  not thin there by decision; nobody had noticed it was absent. Now a new email
  path cannot ship without someone writing the name of a real test file next to
  it. A stale entry fails too, so the list cannot rot into an exemption.

- **`DisplayScreenTests`** — in-pharmacy screens. The kiosk endpoints are the
  only anonymous surface in the product that returns a pharmacy's own content,
  so the questions are not "does it work" but *what does a token get you, and
  what happens when it is taken away*:

  - a token reaches only its own pharmacy;
  - a poster on the account but **not on this screen's playlist** is refused —
    without that, a device in a public waiting room is a key to every published
    poster the pharmacy owns;
  - revoking a credential stops content on the next poll;
  - a pairing code is spent after one use, and an unknown code does not reveal
    whether it ever existed (the code space is small and the endpoint is
    anonymous, so a distinguishable refusal would let somebody enumerate live
    codes across every pharmacy);
  - a draft cannot go on a screen, and another pharmacy's playlist cannot be
    assigned to yours;
  - unpublishing a poster takes it off the wall, not just off the website;
  - the ETag answers `304` when nothing changed **and changes after an edit** —
    a tag that never moved would leave every screen showing last month's loop,
    which is the failure mode a one-directional test would miss;
  - the payload carries nothing identifying the account.

  Screens are a paid feature, so the fixture puts each pharmacy on Pro first —
  and `SetTierAsync` exists because the *cap* only exists on Starter and cannot
  be tested on Pro.

### Dead controls (`scripts/check-dead-ui.mjs`)

The login page carried `<Link href="#forgot">Forgot password?</Link>` for
months. It rendered, it was styled like every other link, and clicking it did
nothing — while the feature behind it did not exist.

`check-routes.mjs` could not have caught it. Every link pattern there requires a
leading slash, because its question is "do internal links resolve to real
routes"; a fragment is not a route, so `#forgot` was never a candidate. The gap
was not in that check — it was that **no check owned the question "does this
control go anywhere at all"**.

This one flags fragment links with no matching `id` in the same file, `href="#"`
and `href=""`, and empty `onClick` handlers. Verified against the real bug: it
reports `app/login/page.tsx:177 href="#forgot"` when the anchor is put back, and
does not fire on a fragment that has a genuine target.

It found two more on the first run, which are recorded in `KNOWN_DEAD` rather
than silently allowlisted: **the terms-of-service and privacy-policy links on
the signup form both go nowhere.** They are kept in their own list so the count
stays visible and shrinking.

A dead link is worse than a missing one. A missing link is invisible; a dead
link tells somebody a feature exists and then denies it — which on a password
reset means a person locked out of their account clicking the one thing that
looks like it would help.

### The database it uses

It inherits the API's **own** connection string from
`apps/api/src/Acme.WebApi/appsettings.json` and overrides only the database
name, so it reaches the same server with the same credentials the app already
uses. Set `MONGO_TEST_URL` to point it somewhere else entirely.

The first version replaced the whole connection string with a bare
`mongodb://localhost:27017`, discarding the credentials — every write then
failed with "requires authentication", and ten tests reported *"the server has
not been started"* while the real cause appeared once, as a `crit:` log line
above the failures.

The preflight was wrong in the same way, and worse: it pinged the `admin`
database, and **`ping` is allowed without authentication**. It passed happily
against a secured server and let the whole run proceed. It now writes and
deletes a document in the throwaway database — the permission the app actually
needs — and names the specific problem: bad credentials, or an account that
cannot create a database.

That last one is a real constraint: the suite creates `acme_it_<timestamp>` per
run, so the account needs rights beyond the single app database
(`readWriteAnyDatabase`, or point `MONGO_TEST_URL` at a server where it has
them).

### Do not override JWT settings from the test factory

`Program.cs` reads them **eagerly**, before `builder.Build()`:

```csharp
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>();
```

`WebApplicationFactory` applies its overrides *during* `Build()`, so that line
never sees them. Overriding `Jwt:Secret` in the factory therefore built the
token **validator** from `appsettings.json` while `JwtTokenService` — which
resolves `IOptions<JwtOptions>` lazily from the final configuration — signed
with the test secret. Register returned 201 and the very next request with that
token failed signature validation with `IDX10517`.

The rule, worth knowing before adding any override:

| How the app reads it | Overridable from the factory? |
|---|---|
| `Configuration["key"]` or `.Get<T>()` at startup | **No** — frozen before Build |
| `services.Configure<T>(section)` | Yes — read on resolve |

`Mongo`, `Messaging`, `Sonar` and `Stripe` are all the second kind, which is why
they work. `Cors:Origins` and `Jwt` are the first kind. Nothing under `Jwt`
needs overriding anyway: the host signs and validates with its own secret.

Requires MongoDB. It fails loudly with an actionable message rather than
skipping: xUnit 2.x has no runtime skip, and faking one would mean every test
passing on a machine with no database.

## Browser tests

```
pnpm test:browser
```

The command installs the Chromium binary first — idempotent, and fast once
cached. Doing it inside the script rather than as a documented prerequisite is
deliberate: the first run without it produces "3 failed, 64 did not run", which
reads like a broken suite rather than a missing download. Use `pnpm exec`, not
`npx`, if running Playwright by hand — `npx` can resolve a different version
than the lockfile pins, and then the browser it downloads is not the one the
test runner looks for.

Playwright starts the **web app** itself if it is not already running
(`reuseExistingServer`, so a dev server you started by hand is used as-is). It
does not start the **API**: that needs MongoDB, has its own lifecycle, and a
`dotnet run` failing inside a test harness gives a far worse message than the
global setup's. So the recurring friction — a forgotten `pnpm dev:web` — is
gone, and the rest stays explicit.

A global setup checks the web app and the API answer before any test runs, so a
stack that is down says so once, by name, instead of 67 times as connection
refusals. The half-up case matters more: web running with the API down renders
every page fine with empty lists, and the failures look like application bugs.

It **waits** rather than asking once. `next dev` compiles routes on demand, so
the first request to a cold server routinely takes over five seconds — one was
observed at 4.965s against a 5s one-shot timeout. A check sitting on that
boundary reports "not running" for an app that is up and merely warming up,
which is worse than the problem it was written to solve: confidently wrong
rather than merely unhelpful. It now polls for up to 90 seconds and separates
three states:

| What it sees | What it says |
|---|---|
| connection refused | not running — `pnpm dev:web` |
| accepts but never replies | something is listening; check the dev server for a compile error |
| answers | carry on |

This is the layer that matches how the bugs were found — by clicking.

Per role (guest, pharmacy, superadmin), for every page: the right roles get in
and the wrong ones do not; the page renders content rather than an empty shell
or an error boundary; and nothing it requests 404s or 405s. Plus two navigation
tests — every nav link opens the page it names, and every page is reachable from
the nav — and a download test asserting the **content-type**, since a swallowed
file still answers 200.

### Waiting for a page, and what "rendered" means

`networkidle` says the requests finished, not that React has rendered what came
back. The poster editor failed on exactly that gap: the assertion read
**"Loading poster…" — fifteen characters, precisely the length the failure
reported** — while the editor was still mounting. Every other page had been
passing on timing luck; there are four such placeholders in the app. Pages now
settle before anything is asserted.

The second half of that failure was the assertion itself. "More than 30
characters of text" is a reasonable proxy for "did this render" on a list or a
form, and the wrong question entirely for `/posters/:id`, which is a Konva
drawing surface with sixteen characters of chrome — `Layers` and `Properties`.
Counting characters there would either fail on a working page or get lowered
until it proved nothing.

So a page can declare what rendered means for it. The editor asserts the
**canvas element mounted**, which is stricter than any character count and is
what a user would actually notice.

### Fixtures are created, not assumed

`/posters/:id` and the poster PDF download both **skipped** on a database where
the test pharmacy owned no posters — and the PDF download is the regression test
for a bug that actually shipped. A skip is honest about not having run, but a
regression test that never runs is the same as not having one, and it says so in
grey text nobody reads. The suite now creates a poster when it needs one.

### The kiosk journeys

Two tests under `a screen on the wall` run in `browser.newContext()` — **a
browser that has never signed in**.

That is the whole point of them. Every other test in the suite runs inside an
authenticated context, which is precisely the wrong shape for this feature: a
television in a waiting room reaches a pharmacy's posters with no cookie, no
account and no session, and a test that paired inside the signed-in context
would pass even if the endpoints quietly started requiring a login.

One registers a screen and a playlist through the API, then pairs a fresh
context with the real six-character code and asserts the **poster image**
appears — not merely that the pairing form went away, which a blank black page
would also satisfy. The other checks an unpaired device is asked to pair rather
than being redirected to a sign-in form, and that a credential-less request
answers `400` rather than `401`.

Both also assert `/display` is not redirected. That is the runtime counterpart
to a static guard in `check-routes.mjs`: `/display` (the kiosk) and `/displays`
(the management page) differ by one character, the middleware guard is a prefix
match, and protecting the kiosk by accident would send every screen in every
pharmacy to a login page — silently, and visible only to whoever is standing in
the shop.

## What the browser suite found on its first real run

29 passed, 7 failed. Four of the failures were real:

**`/social` and `/admin` were never guarded.** Neither appeared in the
middleware's `PROTECTED` list *or* its `matcher`, so a signed-out visitor could
open the Social & Ads page outright. `/admin` relied entirely on a client-side
check that renders first and decides afterwards. No static check could see
this — the page, the route and the layout are each individually correct — and
no API test could either, because the API was never asked.

**`Badge` rendered a `<div>` inside a `<p>`.** `CardDescription` is a `<p>`, and
the templates gallery puts a `Badge` in one. Invalid HTML, and React reports it
as a hydration error at runtime. Now a `<span>`; the base style was already
`inline-flex`, so nothing moved.

The other three were defects in the tests themselves, which is worth recording
because they are the same failure mode in miniature:

- the console watcher counted the 401s a page gets while probing
  `/api/auth/me`, so every public page "failed"
- the "kept out" assertion demanded a redirect, but `/admin` legitimately keeps
  the URL and renders a refusal
- the seeded pharmacy account is `riverside@example.test`, not `pharmacy1@`

`check-routes.mjs` now also enforces that every non-public page is in
`PROTECTED` **and** in the `matcher`, so this class cannot come back silently.
Verified by removing each in turn.

## Campaign email

The poster is now drawn in the body rather than attached as a PDF, which makes
layout part of the product. Three layers check it:

- **`CampaignEmailTests`** (fast suite) — structure and safety: the poster is
  linked rather than embedded, a reader whose client blocks images still has a
  visible way to it, names cannot inject markup, the video sits below the
  poster, and there is always a plain-text part carrying the poster's address.
- **`GET /api/admin/diagnostics/campaign-email/preview`** — the real output of
  `CampaignEmail.BuildHtml`, viewable in a browser. The poster is a placeholder
  image of the same proportions, passed in as the URL exactly as a real send
  passes one; every table, width and alignment rule is the one that ships. It
  used to have to find and replace a `cid:` reference afterwards, because a cid
  resolves against a MIME part that does not exist outside a message.
- **Browser suite** — loads that preview and measures: the poster is centred to
  within 2px, does not exceed the card width, sits above the video, and the
  whole email fits a 375px screen without sideways scrolling. Asserting the
  outcome rather than the CSS means a rule can be rewritten freely, but a rule
  that stops working cannot pass.

`docs/samples/campaign-email.html` is a checked-in render for eyeballing
without running anything.

### An escaping test that failed on correct code

The injection test first asserted `NotContain("onerror=")` and failed against
perfectly escaped output — because **a word is not a vulnerability**. The string
survives inside

```html
alt="&quot;&gt;&lt;img src=x onerror=alert(2)&gt;"
```

where every delimiter is encoded and nothing can execute. What makes injected
text dangerous is a tag that *opens* or an attribute that *closes*, so the test
now checks for `<script`, `<img src=x` and `"onmouseover` — and was verified
both ways: it passes on the real escaped output, and catches all three vectors
when the encoding is removed.

An assertion that fails on correct code is worse than no assertion. It teaches
whoever hits it to loosen the test, and the next person inherits a security
check that has been weakened for a reason nobody remembers.

### Why the poster is a link, and not a data URL or an attachment

Three options, and each one was ruled out by measurement rather than taste.

**A base64 data URL in the HTML** breaks the message twice over. **Gmail clips
any message whose HTML exceeds roughly 102KB**, showing "[Message clipped]", and
a poster encoded inline runs to several hundred kilobytes — so the design would
be cut off in the client most recipients use.

And the worse half, learned later: **Gmail does not render `data:` URIs in
`img src` at all.** The video thumbnails used to travel this way, on the
reasoning that data URLs "work everywhere with zero fetch" — they work in Apple
Mail and Outlook desktop, which is why it survived testing, and showed nothing
in Gmail. Both poster and thumbnail are now signed links when the API is
publicly reachable; see `CampaignService.ChooseThumbnailSrcAsync` and
`VideoThumbnailSourceTests`.

**An inline `cid:` attachment** fixes the clipping — it travels as its own MIME
part and does not count against that budget — and was what shipped. It fails for
a different reason: Mailjet's Send v3.1 puts `InlinedAttachments` inside each
personalisation, so the poster is uploaded once per recipient. Measured on a
live failure, 977,045 bytes for four recipients. A thousand recipients is a
quarter of a gigabyte, and four was already enough for the provider to close the
connection mid-upload.

**A signed link** is what it is now. The message carries a URL, so its size no
longer moves with the audience: about 3.7KB per recipient whatever the design.
The cost is that a client which blocks remote images shows nothing until the
reader allows them, which is why the email also carries a visible "Not showing?"
link and puts the address in the plain-text part.

The thumbnails stay as data URLs for now. At 120px they are small enough not to
clip a message, and they dodge the image proxies that cannot reach a private API
host in development — but they are still copied per recipient, and five of them
is around 100KB each time. Same fix available when it matters.

### The gap that let a live campaign fail

A campaign went out, Mailjet closed the connection mid-upload, and the log said
only `Broken pipe`. The suite was green throughout. Worth writing down, because
the reasons are structural rather than a missing assertion:

1. **The sender is substituted in every integration test.** `ApiFactory` does
   `services.RemoveAll<IEmailSender>()` and installs a capturing fake, so
   `MailjetEmailSender` had never executed under test. Everything *up to* the
   sender was covered; the sender itself was a blind spot the shape of one
   class.
2. **Every campaign test sent to exactly one recipient.** The bug is that Send
   v3.1 carries `InlinedAttachments` inside each personalisation, so a poster is
   uploaded once *per recipient* — at one recipient it does not exist. A test
   fixture that never exercises plurality cannot see a per-item cost.
3. **`EmailSenderTests` skips the file.** It is a source scan, and it excludes
   `MailjetEmailSender.cs` by name at three separate points. The name suggested
   coverage that was never there.

`Acme.Tests/Messaging/MailjetBatchingTests.cs` closes it: the real sender
against a stub `HttpMessageHandler`, asserting on the requests it chooses to
build. No network and no account — the transport is recorded, not performed.

```bash
dotnet test apps/api/tests/Acme.Tests --filter "FullyQualifiedName~MailjetBatching"
```

It fixes the original defect in place: twelve recipients and a 2 MB poster used
to build **one 33.6 MB request** against a 15 MB limit; it now builds four of
8.4 MB.

**But note what these tests still do not cover, because it matters.** A later
production failure was a *single* recipient at 1.5 MB — well inside every budget
these tests assert on, and they would all have passed. Byte-budget batching was
a real defect and this file guards it; it was not the cause of that failure.
The tests assert on the requests the sender *chooses to build*, and the fault
lay in how the request was *transmitted* — chunked, with no `Content-Length`,
against a server that closed the connection rather than answering. A stub
`HttpMessageHandler` sits above the transport and so cannot see that class of
problem at all. Worth remembering before treating a green run here as proof
that sending works. The other four hold the boundaries — nobody is dropped or duplicated by
the split, a heavier attachment must mean smaller batches, fifty *small*
messages must still go in one call (a size rule that shrank every batch would be
its own regression), and a single message over the whole budget is attempted
alone rather than discarded silently.

One detail in that file is load-bearing: `UseMock = false`. It defaults to
**true**, and a mocked sender returns before building a single request — left at
the default, all five tests pass while asserting nothing.

## The smoke test

**Video refine (new).** After the render, the smoke changes the video the way a
pharmacist does, then checks:

- The shot list has a tile per shot, each with a still, loaded through the web app.
- A shot not in the video is refused.
- A typed request that names no shot is refused ("tap the shot"), not guessed.
- Tapping the last stock shot comes back Ready *with no "not made" note*.
- That shot's footage changed and the others did not.
- The length is identical (the narration was reused).
- The monthly allowance did not move.

Each of those is a FAIL, because each reached a pharmacist before this existed.
`pnpm smoke:selftest` has one broken stub scenario per check (`refine-*`), and
all of them must block. The step adds one classifier call and one re-cut,
about 1–2 minutes, to a production smoke.


Everything above runs against **source**. `pnpm smoke` runs against a **deployment**,
and that gap is where this project's worst failures lived: a container five days
stale passing every suite, a provider changing its contract behind a mock, a
`data:` URL that Gmail silently refuses. Full detail in [SMOKE.md](SMOKE.md);
the short version is that it signs in as its own seeded account, walks one call
through each paid door, and reads what a customer would read.

Run it after `pnpm ship:*`, before telling anyone the release is out.

## Testing the tests

`./scripts/e2e/selftest.sh` runs the e2e lifecycle against a fake backend twice:
once correct (expect zero failures) and once with four defects injected,
asserting each is caught by name. An assertion that has only ever seen a working
system is an assertion nobody knows is wired up.

Every static check above was verified the same way — by reintroducing the real
bug and confirming the check fails. Two were found wanting on the first attempt
and rewritten:

- the file-download rule scanned a fixed 600-character window and missed the
  poster PDF, whose `File(` sits 686 characters away past a doc comment. It
  passed while the bug it was written for was still present.
- the contract checker matched `/api/patients/${id}` against
  `/api/patients/bulk-add-to-group` and reported a 405 for a request that works.

Both are the same mistake in different clothes: a check that appears to pass
proves nothing until you have watched it fail.

`pnpm test:prove` does the same for the unit and component tests, as a
standing check rather than a one-off. It reintroduces 50 fixed bugs one at a
time and requires each to turn its tests red. See `REGRESSION-TESTS.md` for
which test guards which bug. Writing it found four mutations that did not
really recreate their bug. For example, the framer's update-depth crash needed
two faults together, and undoing only one of them was harmless. Each was
rewritten until it reproduced the real failure. None of the tests turned out
to be weak.

A review of the script then found the opposite hole. It counted any failing
run as "caught", so a runner that could not even start (a path with spaces,
unquoted) reported a clean result. Each bug's tests now have to pass on the
unmodified code before its mutation counts. Anything else is reported as
BASELINE and fails the run.

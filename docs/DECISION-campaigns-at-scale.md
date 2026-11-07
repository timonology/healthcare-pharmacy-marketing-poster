# Sending campaigns at 10–1000 recipients

A decision record. What breaks today, what to build, and what it costs.

Written because the send path has been patched four times against a symptom
(`Broken pipe`) that turned out to be a slow uplink, and the underlying question
— *does this work at a thousand recipients* — had not been asked.

---

## The answer, first

**No. It cannot work today, and two of the reasons are correctness rather than
speed.** A campaign to 1000 patients would, on current code, be able to send
700 of them, fail, and then send those 700 a second time when the pharmacist
presses Retry.

The recommendation is to move dispatch to a background worker, modelled on the
video pipeline this codebase already runs, and to make per-recipient state the
thing that decides who gets sent to. That removes the duplicate-send bug, the
unrecoverable-campaign bug and the request-timeout ceiling in one change,
because all three come from the same root: **the send has no memory of how far
it got.**

---

## What breaks, in order of seriousness

### 1. Retry sends to everybody again — S1, silent

`RetryAsync` dispatches to `campaign.Recipients`, the original list:

```csharp
await DispatchAndPersistAsync(campaign, user, poster, videoEntities,
                              campaign.Recipients, campaign.Channel, ct);
```

`DeliveryReport` already records `Sent` / `Failed` / `Pending` per recipient. It
is written and never read back.

At four recipients this is invisible: one request either works or does not, so
retry is correct by accident. At a thousand it is the normal case — a long send
is far likelier to fail partway — and the consequence is patients receiving the
same clinical marketing twice, from a pharmacy that has no way to tell it
happened.

### 2. A campaign that dies mid-send is stuck forever — S1

`MarkSending()` sets `CampaignStatus.Sending`. **Nothing anywhere reads that
status.** There is no worker, no lease, no recovery pass.

`RetryAsync` refuses anything that is not `Failed`:

```csharp
if (campaign.Status != CampaignStatus.Failed) return ...;
```

So a deploy, a crash or an app-service recycle during a send leaves the campaign
permanently in `Sending`: it cannot finish, and it cannot be retried. The only
exit is editing the database.

### 3. The send is synchronous inside the HTTP request

`POST /api/campaigns` does not return until every message has been handed to
Mailjet. Azure App Service terminates an idle request at **230 seconds**, and no
amount of client-side patience changes that.

Measured cost per recipient today is **244 KB** — Mailjet's Send v3.1 carries
`InlinedAttachments` inside each personalisation, so the poster is uploaded once
per recipient and the total is `recipients × poster`.

| Recipients | Uploaded, today | At 800px/q65 |
|---|---|---|
| 10 | 2 MB | 1 MB |
| 100 | 24 MB | 10 MB |
| 500 | 122 MB | 49 MB |
| 1000 | 244 MB | 99 MB |

| Uplink | 1000 recipients today | At 800px/q65 |
|---|---|---|
| 12 KB/s (the machine this was debugged on) | 5.5 hours | 2.2 hours |
| 1 MB/s | 4 minutes | 2 minutes |
| 10 MB/s (typical Azure egress) | 24 seconds | 10 seconds |

Even at 10 MB/s a large campaign is tens of seconds of a held HTTP connection,
a blocked request thread and a spinner with no progress. At 1 MB/s it is minutes
and within sight of the 230-second wall.

### 4. No cap on recipients per campaign

Only the monthly plan allowance is checked (Pro: 5,000/month). One click can
therefore be 5,000 recipients — 1.2 GB of upload on current settings.

### 5. The per-request timeout does not match the batch size

`client.Timeout` for Mailjet is 30 seconds. A 500 KB request measured **53
seconds** on a slow link. The budget and the timeout are set independently and
can contradict each other.

---

## What to build

**A background dispatch worker, with per-recipient state as the source of
truth.** The pattern already exists in this codebase for video generation:
a row in Mongo is the queue, a worker claims it atomically, progress is written
as it goes, and the UI polls. Reusing it means no new infrastructure and an
operational story the team already knows.

### Shape

1. `POST /api/campaigns` validates, checks the plan, persists the campaign as
   **`Queued`** with every recipient at `Pending`, and returns immediately.
2. `CampaignDispatchWorker` (a `BackgroundService`) claims one queued campaign
   at a time with an atomic `FindOneAndUpdate` and a **lease timestamp**.
3. It sends in batches, writing recipient results **after each batch**, not at
   the end.
4. **It only ever sends to recipients whose status is `Pending`.** This is the
   whole design. Retry, crash recovery and a duplicate worker all become safe
   without any of them being special-cased.
5. A heartbeat renews the lease. A campaign whose lease has expired returns to
   `Queued` and is picked up again — resuming where it stopped, because the
   sent recipients are already marked.
6. The campaigns list already polls; it shows `247 / 1000 sent` instead of a
   spinner.

### Why this and not the alternatives

| Option | Verdict |
|---|---|
| **Mongo-as-queue + worker** | **Recommended.** No new infrastructure, mirrors the video pipeline, survives restarts, resumable. |
| Azure Service Bus / Storage Queue | More robust at much higher volume, and the right destination eventually. Adds a dependency, a credential and a failure mode for a product sending thousands, not millions, of emails a month. Note it as the migration path. |
| Keep synchronous, cap recipients at ~50 | Contradicts the Pro plan, which sells 5,000 recipients a month. A cap that low is a product change, not an engineering one. |
| Fire-and-forget `Task.Run` | Worst of both. Not durable, dies with the process, and hides the same bugs behind a faster response. |

### What it did not fix, and what did

Total bytes. Mailjet duplicates the poster per recipient, so a 1000-recipient
campaign moved the same data whoever moved it. The worker made that acceptable
(nobody is watching a spinner) rather than smaller.

That turned out to be the whole problem, and shrinking the poster was never
going to solve it. A real failure, after the worker shipped:

```
Mailjet send failed for 4 message(s), 977,045 bytes.
The server closed the connection mid-upload.
```

**244 KB per recipient.** The byte-based chunking worked exactly as designed —
1,000,000 ÷ 244,261 = 4 messages per request — but 977 KB at the 8–12 KB/s this
connection sustains is around eighty seconds of uploading, and the provider
closes the connection first. Arithmetic that settles it:

| | per recipient | 1,000 recipients |
|---|---|---|
| Inline, 1200px q78 (what failed) | 244 KB | **244 MB** |
| Inline, 800px q65 | ~80 KB | 80 MB |
| Linked | **3.7 KB** | **3.7 MB** |

No quality setting reaches a thousand recipients. Shrinking the poster is a
mitigation for campaigns of tens.

**So the poster is linked, not embedded.** `/api/posters/email-image?token=…`
serves the render to an anonymous reader; the token is HMAC-signed with the JWT
secret and names one blob for 180 days. The pattern is the one already used for
video links in the same email, and for the same reason — an MP4 would not fit in
a message either.

Three details that are decisions rather than mechanics:

- **The token binds the blob key, not the poster id.** Blob keys are
  content-addressed, so an email keeps showing what was sent even after the
  poster is edited. Binding the id would mean editing a design silently rewrote
  every campaign already in people's inboxes.
- **The transcode moved to the endpoint.** Same call, but it now runs once per
  reader who looks instead of once per recipient either way, and on their
  bandwidth rather than the sender's. `PosterMaxWidthPx` dropped to 800 and
  `PosterJpegQuality` to 65 in the same change: nothing depends on those being
  generous any more, and they now govern what a patient downloads on mobile data.
- **The email carries a visible "Not showing?" link** under the image, and the
  plain-text part carries the address. This is the cost of linking: Outlook
  desktop blocks remote images from an unknown sender, and the poster is not
  decoration, it is the message. Gmail, Apple Mail and Outlook.com load remote
  images by default.

What is still copied per recipient: video thumbnails, which are base64 data URLs
in the HTML. Five of them is about 100 KB per recipient — an order of magnitude
below where the poster was, and fixable with exactly the same signed URL.

### The exception, found by sending one

A campaign sent from a development machine arrived with everything except the
design. Not intermittently — in Gmail. In Apple Mail on the same Mac, same
message, the poster was there.

**A mail client does not fetch images from the reader's browser.** Gmail fetches
them from Google's servers, through its image proxy. `Public:ApiBaseUrl` is
unset in this repo and falls back to `http://localhost:5050`, so the poster's
address pointed at the sending machine: reachable from Apple Mail running on it,
reachable from nobody else. Two clients, opposite results, nothing in the log.

The same trap had already been solved forty lines away in the same method — the
video thumbnails are data URLs, with a comment saying image proxies "can't reach
localhost / private API domains in dev or corporate networks". It was there to
be read.

So the send now classifies the address. Public — a real domain, a tunnel, a
routable IP — and the poster is linked. Loopback, a Docker service name, a
`.local` or `.internal` name, a private range — and the poster is embedded,
which is what the campaign did before it was made to scale and is still right
for the small local sends this covers. It logs a warning naming the address and
what to set, because the alternative is an email that silently has no design in
it.

Two consequences worth knowing:

- **`Public__ApiBaseUrl` was always required in production** — the video links
  use it too, and have been unreachable off the sending machine for as long as
  they have existed. It is on the production checklist. Linking the poster is
  what made a missing value *visible*, on the part of the email people look at.
- **A local send is not the email production sends.** To see the real shape from
  a development machine, point `Public__ApiBaseUrl` at a tunnel.
  `ApiFactory` sets a public-looking value on purpose, so the integration suite
  keeps testing the linked path rather than quietly switching to this one.

---

## What moving dispatch off the request thread broke

Found by sweeping every place that had assumed a campaign was finished by the
time `POST /api/campaigns` returned. All four are fixed; they are recorded here
because the pattern will recur with the next thing that becomes asynchronous.

**Stop stopped nothing — S1.** The worker read the campaign once, before
sending, and after every group of twenty wrote the whole document back from that
copy, which still said `Sending`. A pharmacist pressing Stop got a 200, saw the
badge change, and had it overwritten by the next group's write; the campaign ran
to the last recipient and reported itself `Sent`. Nothing in the API or the UI
showed otherwise. Fixed with `SaveProgressAsync`, which writes only the fields
the send produces and returns the status in the same operation, so the worker
learns about a stop it cannot erase. The guarantee is one group, not instant:
mail already with the provider cannot be recalled.

**The monthly allowance stopped holding — S1.** It was the sum of `SentCount`
over campaigns with a `SentAtUtc`, which was the whole truth while sending
happened inside the request. Neither field is written until a worker finishes, so
every check read zero and a Starter pharmacy could create five 500-recipient
campaigns in as many seconds. Queued and sending campaigns now contribute their
outstanding recipients.

**Retry had no button for the case it was built for — S2.** The API was changed
to retry a campaign that reached some of its list but not all of it. That
campaign is `Sent`, and the button was still gated on `Failed`, so the only way
to reach the missing 300 was to build a new campaign and work out by hand who
had already had it. The control now reads *Send to remaining N*.

**A deploy mid-send marked the campaign failed — S2.** `DispatchAndPersistAsync`
caught `OperationCanceledException` along with everything else and called
`MarkFailed`, directly contradicting the worker's own comment about leaving an
interrupted campaign to resume. `Failed` is not claimable, so it would sit until
somebody noticed. Cancellation is now rethrown.

Two smaller ones: `Queued` was missing from the campaign status filter, so
filtering hid the newest campaigns; and five integration tests asserted on the
mailbox immediately after the POST. Four of those failed honestly. The fifth —
the poster render cache — compared nought to nought and **passed**, having
quietly stopped testing anything.

---

## Alongside it

- **Cap recipients per campaign.** A product decision, but 5,000 in one click
  with no confirmation is worth a second thought.
- **Align `client.Timeout` with `MaxRequestBytes`** so a batch that fits the
  budget cannot exceed the timeout.
- **Send batches with a small degree of parallelism** (2–4). Latency per request
  dominates at low recipient counts; this is the cheapest win once dispatch is
  off the request thread. Respect Mailjet's rate limits.
- **Record `SentAtUtc` per recipient**, so a delivery report can answer "when"
  rather than only "whether".

---

## Testing this properly

The suite as it stood could not see any of it: every campaign integration test
sent to exactly one recipient, where a send either wholly works or wholly fails
and retry is correct by accident.

Done:

- `CampaignDispatchStateTests` — 1000 recipients, a partial send, then a retry,
  asserting **nobody receives twice**. Domain-level, no database. This is the
  test that would have caught the retry bug, and writing it found a second one:
  refusing to queue a `Sent` campaign would have made the whole case impossible.
- `CampaignStopTests` — Stop pressed while a campaign is sending, asserting it
  settles as `Stopped` with one group delivered and the report naming exactly
  who was reached.
- `CampaignPlanLimitTests` — the allowance counted against a campaign that is
  still in the queue.
- `CampaignEmailDeliveryTests` — that the message carries no inline image, that
  the whole message stays under 32 KB, that the link in it serves a real JPEG to
  a client holding no credentials, and that a token with one character changed
  gets nothing. The size assertion is the one that would catch somebody
  re-embedding the poster.

The last two hold the email sender open rather than racing it. Against the mock
a forty-recipient campaign finishes in under a millisecond, so a test that
created one and then tried to stop it would have been measuring how fast its own
HTTP calls were, and would have passed against the broken code.

Still wanted:

- A test that kills the worker mid-campaign and asserts it resumes rather than
  restarts. The lease makes this correct in principle; nothing proves it.
- A load check at 1000 recipients against the capturing sender, asserting
  bounded memory and a single poster render.
- Per-recipient truth from the provider. `IEmailSender` returns counts, not
  addresses, so within a group of twenty that partially fails the report is an
  approximation. Mailjet does return per-message status.

---

## Postscript — the video thumbnail, resolved

The note above ends "fixable with exactly the same signed URL", and it was. What
it did not know is that the data URLs were not merely *wasteful*, they were
**broken in the one client that matters**.

A campaign carrying a poster and a video arrived with the poster visible, the
watch link working, and a blank space where the still should be. **Gmail strips
`data:` from `img src` and renders nothing.** The comment in the code said data
URLs "work everywhere with zero fetch"; they work in Apple Mail and Outlook
desktop, which is precisely why it survived every test.

So the reasoning in this document was right and incomplete: the question was
never "is this too many bytes", it was the same question the poster had already
answered — *can a mail provider's fetcher reach this API?* One question, answered
twice, forty lines apart, differently.

What changed:

- the still is linked when the API is publicly reachable and embedded only when
  it is not, exactly as the poster is. `PosterTravelsAsALink` was renamed
  **`ImagesTravelAsLinks`**, because the poster-shaped name is part of why the
  thumbnail never got the same answer;
- the decision moved *into* `CampaignService.ChooseThumbnailSrcAsync`, and the
  embedded path is a delegate. The first attempt left the branch at the call
  site with a URL builder behind it, which meant the test could not fail if the
  branch were wrong. Two tests now assert the negatives: linking never reads the
  blob, and an unreachable API never mints a token;
- the still is signed for **180 days** (`PosterImageToken.EmailLinkValidity`)
  while the MP4 keeps its 48 hours. The clip is the valuable thing and is meant
  to lapse; an image that vanishes turns a fine email into a broken-looking one
  when somebody reopens it next month. The signature binds *which* asset it
  opens, so a long-lived thumbnail token cannot fetch the video.

Per-recipient bytes for a five-video campaign drop from ~100 KB to a few hundred
bytes of URL.

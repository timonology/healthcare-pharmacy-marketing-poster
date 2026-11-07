# In-pharmacy display screens

A screen on a pharmacy wall showing a loop of published posters and finished
videos. A television,
a tablet or a mini PC opens one URL, is paired once, and then runs untouched
for months.

---

## The shape

Three things, and they are deliberately separate:

| Thing | What it is | Where |
|---|---|---|
| **Screen** | One physical panel. Has a name, where it hangs, and optionally an address drawn along the bottom. | `/displays` |
| **Playlist** | An ordered set of published posters (with a dwell time each) and finished videos (played once each). Can be shown on several screens at once. | `/playlists` |
| **Kiosk** | The page the device itself opens. No account, no chrome. | `/display` |

Note the singular. **`/display` is the television; `/displays` is the page a
pharmacist manages them from.** They differ by one character and only one of
them is public.

A playlist is a first-class thing rather than a list hanging off a screen,
because the second screen is where the value is: a counter display and a
waiting-room display want one edit to reach both, and a head office running a
flu campaign wants one edit to reach every branch it works as.

---

## Plans

| Plan | Screens |
|---|---|
| Free | none — the feature is refused with an upgrade prompt |
| Starter | 1 |
| Pro | unlimited |

The limit is checked when a screen is **registered**. An account that downgrades
keeps its existing screens running; blanking a shop window over a billing
change would be a worse outcome than a pharmacy briefly having more screens
than their plan covers. Worth knowing this is a deliberate choice and not an
oversight.

---

## Setting one up

### 1. Publish the posters

A screen only shows **published** posters. A draft is refused when you try to
add it to a playlist, with a message saying so — the same rule campaigns use,
for the same reason: a screen in a shop window *is* publication.

### 2. Build a playlist

**Playlists** (reached from the Screens page).

- Name it something a colleague will recognise — *Winter vaccinations*, not
  *Playlist 2*.
- Add published posters, set seconds per poster, reorder with the arrows.
- Between 3 and 300 seconds each; 12 is the default and reads comfortably.
- Up to 50 items.
- **Add video** offers your finished videos from My Videos — *Ready* only. A
  video plays **once, muted**, for its own length, then the loop moves on, so it
  has no seconds box. Up to **10 videos** per playlist.
- The same item **twice in a row** is refused: on a wall that reads as the
  screen having frozen. The same poster at both ends of a long loop is fine.
- Nothing is sent until you press **Save**.

The Screens page shows a small **N videos** badge beside a screen whose
playlist has videos in it.

### 3. Register the screen

**Screens → Add screen**.

- **Name** — how you will tell it from the others. *Waiting room*.
- **Where it hangs** — for your own records; it is never displayed.
- **Address shown on the screen** — optional, drawn along the bottom of the
  display. Useful in a shop window, pointless inside the shop where people
  already know where they are.

Then pick the playlist from the **Showing** dropdown.

### 4. Pair the device

On the television or tablet, open:

```
https://<your-domain>/display
```

It shows a six-character code box. Read the code off the Screens page and type
it in.

The code:

- lasts **four hours**,
- works **once**,
- avoids characters people misread — no `O`/`0`, `I`/`1`/`L`, `S`/`5`, `Z`/`2`,
  `B`/`8`, `U`/`V`.

Press **New code** on the Screens page if it expires before the installer gets
to the device.

Once paired the screen starts looping within about thirty seconds.

### 5. Set the device up properly

Pairing is the software half. The rest is the hardware:

- **Turn off the device's sleep timer.** The kiosk asks the browser to keep the
  screen awake, but that is best effort — it is refused on some devices and
  released whenever the tab is hidden. The operating system setting is what
  actually keeps the panel on.
- **Set the browser to open `/display` on boot**, so a power cut does not need a
  visit. Most smart TVs and Android boxes have a kiosk or auto-launch mode.
- **Full screen.** The page draws edge to edge and hides the cursor, but browser
  chrome around it is up to the device.
- **Landscape** unless you have designed portrait posters. The loop scales
  posters to fit, so a portrait A4 on a landscape screen leaves black bars.

---

## Changing what a screen shows

Edit the playlist and press **Save**. Screens pick it up on their next poll,
within about thirty seconds. Nothing is pushed to the device and nothing needs
restarting.

Assigning a **different** playlist to a screen takes effect the same way.

Unpublishing a poster removes it from every screen showing it, on the next
poll. The playlist keeps the entry, shown struck through and labelled *Not
published — skipped*, so you can see why the loop got shorter rather than
wondering.

---

## Access, and taking it away

A screen is reached by a **credential**, and there are two kinds.

**Paired device** — the normal one. Created by typing a code at the device. The
credential lives in that browser's local storage; nothing secret is written
down, spoken aloud, or left in a browser's history.

**Direct link** — an escape hatch for hardware that cannot take a code: a
browser with no usable keyboard, or a screen somebody has to configure
remotely. Press **Direct link** on the Screens page and copy the URL. It is
shown **once** and never again.

Both appear under the screen with when they were created and when they were
last seen. **Revoke** stops one immediately — the device gets a 401 on its next
poll, forgets its token, and returns to the pairing screen.

Things worth knowing:

- Pairing a new device **replaces** the previous paired device. A screen is one
  panel; leaving the old credential live would mean a television the pharmacy
  has thrown away still pulls content.
- A direct link does **not** disturb a paired device, and vice versa. They are
  independent so that copying a link cannot silently unpair the screen already
  on the wall.
- Issuing a new direct link retires the previous one.
- The direct link carries its token in the URL **fragment** (`/display#token=…`).
  A fragment is never sent to a server, so the credential stays out of access
  logs. The kiosk stores it and strips it from the address bar immediately, so
  it is not left on screen to be photographed.

---

## What a screen can and cannot see

A kiosk hangs in a room strangers walk through and can be photographed at any
time. It receives only what a passer-by could read off the glass anyway:

- the screen's name,
- the address line, if one is configured,
- images of the published posters on **its own** playlist,
- a signed link to each Ready video on its own playlist (see below).

It does **not** receive the pharmacy's email, account id, its own screen id,
patient data, or anything about any other screen. A token is a key to one
screen's playlist — not to the pharmacy's poster library, and not to the
account.

**One identifier does appear**, and this used to say otherwise: each slide's
image path is `/api/display/slides/{poster id}`. The id is an opaque GUID for a
picture already on public display, so it reveals less than the poster beside it,
and no endpoint that accepts a poster id can be reached without signing in —
except this one, which requires the screen's own token and refuses any poster
that is not on that screen's playlist. It is stated here because a security note
that quietly overstates the guarantee is worse than one that does not make it.

**Videos carry their id too, in a signed link:**
`/api/display/video/{video id}?sig=…`. A `<video>` element cannot send a
header, so the credential has to be in the URL — which is why it is **not** the
screen token. It is an HMAC signature (the same one campaign emails use) bound
to that one video and a time window, and it opens nothing else: not another
video, not the thumbnail, not the screen. No blob key reaches the browser.

---

## Videos on a screen

- **Only Ready videos, checked on every poll.** A video that is deleted,
  fails, or is being re-cut drops off the screen on the next poll (within
  thirty seconds) and comes back when it is Ready again. The playlist keeps it,
  marked *Not ready — skipped*.
- **Played once, muted.** Browsers only autoplay muted video, and a shop is not
  somewhere to start playing sound unasked. There is no sound-on setting.
- **Letterboxed, never stretched.** A 9:16 video on a landscape panel is a
  column in the middle with black either side. The address strip is hidden
  while a video plays, because it would sit over the video's own captions.
- **Streamed, not stored.** A dozen MP4s would fill a cheap stick's storage, so
  nothing downloads a video ahead of play and posters are the only thing the
  kiosk keeps offline itself. The browser's ordinary HTTP cache **is** allowed
  to keep a video it has played (`private, max-age` of one signing window, with
  the API's ETag so ranged responses can be cached and revalidated) — without
  that, every loop of every screen downloaded every MP4 again. The link
  carries the video's version (`&v=`, its last-updated time), so a re-cut is a
  new URL and a cached old cut can never play. With no connection a screen
  loops its posters and plays a video only if the browser still has it.
- **A video that will not play is skipped** (for five minutes, then retried),
  and one that never starts within fifteen seconds counts as not playing. A
  slow stream that *is* playing is left alone: the loop moves on when the video
  ends, with a safety net of three times its length.
- **Old kiosk pages are sent posters only.** A kiosk tab left open through a
  deploy runs the old code, which would draw a video as a broken picture. The
  current kiosk says it can play video (`X-Display-Capabilities: video`); one
  that does not is never sent one. It picks videos up on its next reload.
- **Links rotate every six hours without restarting anything.** The signature
  is rounded to a six-hour window, so every poll inside the window gets the
  identical link and the loop does not restart; each link stays valid for at
  least six hours after it rotates, and a video already playing keeps the link
  it started with.
- **Seeking works (and Safari needs it).** The web route
  `/api/display/video/[videoId]` streams from the API's signed download and
  passes `Range` / `206` / `Content-Range` both ways, plus `If-Range`,
  `If-None-Match`, `ETag` and `Last-Modified`. Safari will not play a video
  from a server that ignores ranges. The route refuses an id that is not a
  plain id (`..` would otherwise walk the upstream path), stops the upstream
  transfer when the kiosk moves on, and answers `502` if the API is down.
- **Known cost, worth fixing next:** the API's video storage buffers the whole
  MP4 from Azure into memory for every request, ranged or not (a pre-existing
  choice made for email links). Browser caching keeps screens to roughly one
  fetch per video per six hours, but a seekable, ranged blob read would remove
  the cost entirely.

---

## Running on a bad connection

Pharmacy wifi drops. The kiosk keeps the last playlist it managed to fetch,
including the poster images, and keeps looping (videos are skipped while
offline — they are streamed, not cached). A small amber dot appears in the top
corner — deliberately small, so staff can tell at a glance while a customer
cannot read an error message over the counter.

A screen that has been unplugged and moved lights up with its cached loop
before it has even reached the network.

The **Last seen** column on the Screens page is the thing to check when a
display has gone dark. A screen polls every thirty seconds, so:

- **Live** — heard from within the last two minutes.
- **Last seen 3 h ago** — switched off, asleep, or off the network.
- **paired, not yet connected** — the code was claimed but the device has never
  called home. Usually a network problem at the device.

---

## Head office

A head office manages a branch's screens by **working as that branch** — the
existing switcher, no separate permission. Screens belong to the pharmacy that
was effective when they were created, so switch first, then add.

The switch is already recorded in the audit log, so branch screen changes are
attributable without any extra machinery.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| Device says the code is not valid | Codes last four hours and work once. Press **New code**. |
| Screen shows *Nothing assigned to this screen yet* | No playlist on the screen, or the playlist is empty. |
| A video never appears on the screen | It is not Ready (being re-cut, failed, deleted) — check for *Not ready — skipped* in the playlist. Or the screen is offline: videos stream and are skipped without a connection. Or the kiosk tab predates videos — reload it once. |
| Posters play but the video is skipped | Open the kiosk tab's console: a skipped video logs `[display] skipping video "…" for now:` and why (would not load, autoplay refused, never started). "Would not load" → check `/api/display/video/…` in the Network tab (403 = link rejected, 404 = file missing). A skipped video is retried after five minutes. |
| Can't add a video | Only Ready videos are offered, up to 10 per playlist. |
| Video plays with no sound | By design: screens are always muted. |
| Screen is blank / black | Playlist has only unpublished posters. Check for struck-through entries. |
| Loop got shorter on its own | A poster was unpublished or deleted. It stays visible in the playlist editor, struck through. |
| Amber dot in the corner | Running on its cached playlist — no connection. |
| Screen went back to the pairing box | Its credential was revoked, or the screen was deleted. |
| "Screens are not part of the Free plan" | Upgrade, or use Starter for one screen. |
| Poster is missing from the loop but published | The render failed. Check the API log for `Could not render poster`. |

---

## How it works, briefly

- **Polling, not sockets.** Every thirty seconds with an `If-None-Match`; an
  unchanged playlist answers `304` with no body. No sticky sessions, no
  websocket infrastructure, and a screen that has been showing the same loop
  for a month still reports itself alive.
- **Pre-rendered images.** Slides are the same cached PNGs the campaign emails
  use, so a screen and an email cannot disagree about what a poster looks like.
  The kiosk never runs the designer.
- **Tokens are random, not signed**, and only their SHA-256 is stored. A signed
  token cannot be revoked without a revocation list, at which point the database
  lookup is happening anyway and the signature has bought nothing.
- **The image is fetched, not `<img src>`.** The token travels in a header, so
  it never lands in an access log or a `Referer`.
- **A missing token answers `400`, a bad one `401`.** The difference matters: a
  screen treats `401` as "your credential is finished" and erases it, so a proxy
  that stripped a header must not cost a working display its pairing.
- **Videos stream from a signed same-origin path**, not the screen token — see
  *Videos on a screen*.
- **Burn-in.** The image drifts a few pixels each slide. Invisible to a person,
  and enough to stop a static logo etching itself into a panel over a year.

---

## Testing

See `TESTING.md` for the suites as a whole. What covers this feature:

### Domain tests — `Acme.Tests/Displays/DisplayDomainTests.cs`

No database, no HTTP. The rules that hold regardless of who called:

```bash
dotnet test apps/api/tests/Acme.Tests --filter "FullyQualifiedName~Displays"
```

The ones worth knowing about:

- **Renaming a playlist must not move its version.** The kiosk's ETag is built
  from that version, so a rename that bumped it would make every screen drop
  its cached images and re-download them because somebody fixed a typo.
- **Changing only a duration must move it** — durations are what the screen runs
  its timer from.
- **The pairing alphabet excludes the characters installers mistype.** Draws 200
  codes, not one, and separately asserts the codes are not all identical.
- Pairing a replacement retires the old credential; a direct link does not.
- A poll does not move `UpdatedAtUtc`.

### Integration tests — `Acme.IntegrationTests/DisplayScreenTests.cs`

Real Mongo, real HTTP pipeline. Needs a database:

```bash
dotnet test apps/api/tests/Acme.IntegrationTests --filter "FullyQualifiedName~DisplayScreenTests"
```

These are security questions rather than happy paths:

- a token reaches **only its own pharmacy**;
- a poster on the account but **not on this screen** is refused — otherwise a
  device in a public waiting room is a key to the whole poster library;
- revoking stops content immediately;
- a spent code is spent, and an unknown code does not reveal whether it existed;
- a draft cannot be put on a screen, and another pharmacy's playlist cannot be
  assigned to your screen;
- unpublishing takes a poster off the wall, not just off the website;
- the ETag answers `304` unchanged **and** changes after an edit — a tag that
  never moved would leave screens showing last month's loop;
- the payload carries nothing identifying the account.

Videos on screens:

- a Ready video of mine plays from a signed link, for its own length, and the
  link answers a `Range` request with `206`, the right bytes and a strong
  `ETag`; it carries the video's version; it will not open a different video;
- a kiosk that does not declare video is sent posters only;
- Pending, Rendering, Failed, and Ready-without-a-file videos cannot be added;
  another pharmacy's video cannot be added (and the refusal does not say whose
  it is);
- a video that goes Rendering or Failed leaves the screen on the next poll
  (the ETag moves), and returns when it is Ready again — under a new link, so
  a cached old cut cannot play;
- a screen is sent only the videos on its own playlist;
- ten videos save, eleven are refused.

Unit tests (`Acme.Tests/Displays/DisplayVideoTests.cs`) pin the same rules
alone: showable, the capability header, the signing window (identical inside a
window, rotates at the edge, old link still valid), the signature's scope, and
the cap. Web tests: `src/lib/kiosk-playlist.test.ts`,
`src/lib/playlist-items.test.ts`, and
`src/app/api/display/video/[videoId]/route.test.ts` (Range and validator
pass-through, 304, `..` refused, 502 when the API is down, no token or cookie
forwarded).

The smoke run puts the Video step's finished video on its screen, pairs as a
device, and asks the signed link for the first kilobyte
(`SMOKE_DISPLAY_VIDEO=0` to skip). See `docs/SMOKE.md`.

### Browser tests — `tests/browser/journeys.spec.ts`

```bash
pnpm test:browser -- --grep "a screen on the wall"
```

Both run in `browser.newContext()` — **a browser that has never signed in**.
Every other test in that suite runs authenticated, which is the wrong shape
here: pairing inside a signed-in context would pass even if the endpoints
quietly started requiring a login.

One pairs with a real code and asserts the poster image appears; the other
checks an unpaired device is asked to pair rather than being redirected to a
sign-in form.

### Testing it by hand

1. Sign in as a Pro pharmacy (`riverside@example.test` on a seeded database).
2. Publish a poster, build a playlist, register a screen.
3. Open `/display` **in a private window** — that is what makes it a device
   rather than your own session.
4. Type the code. The loop should start within a few seconds.
5. Edit the playlist and wait thirty seconds without touching the device.
6. Revoke the credential from the Screens page; the device should return to the
   pairing box on its next poll.
7. Kill the device's network. It should keep looping, with an amber dot.
8. Add a finished video to the playlist. On the next poll it plays once, muted,
   letterboxed on a landscape screen, then the loop moves on. Start a change to
   that video (refine): it leaves the screen within thirty seconds and returns
   when the change finishes.

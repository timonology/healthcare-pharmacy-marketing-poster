# Marketing25 — QA test script

A manual pass over everything a pharmacy can do. Work top to bottom; each case
is self-contained, so you can stop and resume.

**Before you start**, read the *For QA* section of `TEST-ENVIRONMENT.md` — how
to sign in, which demo account is which, and how to report what you find. This
document is only the *what to try*.

---

## How to use this

Each case has an **ID**, what to **do**, and what **should** happen. Record
`PASS`, `FAIL` or `N/A` and a note. A case that cannot be run — feature not
deployed, no data — is `N/A`, not `PASS`.

**Only ever mark PASS on what you actually saw.** A case you skipped and
assumed fine is the one that ships broken.

| Column | Meaning |
|---|---|
| **Env** | `TEST` only, `PROD` only, or `BOTH` |
| **Sev** | **S1** data loss, wrong pharmacy's data, or nobody can work. **S2** a main task is blocked. **S3** wrong but workable. **S4** cosmetic. |

### Reporting

Three things make a report actionable: what you did, what you expected, what
happened. Screenshot the **whole window** — the URL and any banner are usually
the useful part.

**Report immediately, stop testing, and say so loudly** if you ever see data
belonging to a pharmacy you are not signed in as. That is S1 and it outranks
finishing the script.

---

## Testing against production — read this first

Most of this script is safe on production. These are not.

| Do not | Why |
|---|---|
| Send a campaign to anyone but yourself | Real patients receive real email. Add your own address as a manual recipient; never use a pharmacy's real list. |
| Import a contact list | It lands in a real pharmacy's audience and there is no bulk undo. |
| Delete anything you did not create in the last five minutes | No undo, and posters are referenced by campaigns. |
| Use a real patient's name, address or phone as test data | Even on TEST. It is not protected the way production is. |
| Test plan limits by upgrading or downgrading a live account | It bills them. |
| Deactivate an account | Locks out a real pharmacy. |

Cases marked **TEST** are the ones that need a throwaway account or a plan
change. On production, mark them `N/A — production`.

**Production also sends real email.** Anything that triggers a message —
signup, invitations, password reset, campaigns — will arrive in a real inbox.
Use an address you own.

---

## 1. Getting in

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 1.1 | BOTH | S1 | Sign in with a valid pharmacy account | Lands on the dashboard, pharmacy name in the header |
| 1.2 | BOTH | S2 | Sign in with a wrong password | Refused with a message; **no hint as to whether the address exists** |
| 1.3 | TEST | S2 | Sign in as `elmtree@example.test` (deactivated) | Refused. A deactivated account must not get in |
| 1.4 | BOTH | S3 | On every password box, press the show/hide control | Reveals and re-hides. Check sign-in, sign-up, reset, and invitation activation |
| 1.5 | BOTH | S1 | Sign out | Returns to sign-in **with the signed-in sidebar gone**. Press Back — you must not see the app again |
| 1.6 | BOTH | S2 | While signed out, open `/dashboard` directly | Sent to sign-in, not shown the page |

### Password reset

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 1.7 | BOTH | S2 | "Forgot password?", submit your own address | A reset email arrives. The link opens a set-password page |
| 1.8 | BOTH | S2 | Set a new password, then sign in with it | Works |
| 1.9 | BOTH | S1 | Before resetting, sign in on a second browser. Reset. Return to the second browser and click about | **You are signed out there too.** Resetting revokes every session — that is the point of it |
| 1.10 | BOTH | S3 | Check your inbox after 1.8 | A separate email confirms the password changed. This is the only warning a real owner gets if it was not them |
| 1.11 | BOTH | S2 | Request a reset for an address that does not exist | The page says the same thing as for a real one. It must not confirm who has an account |

---

## 2. Signing up and onboarding

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 2.1 | TEST | S1 | Register a new account | Welcome email arrives; you land in onboarding |
| 2.2 | BOTH | S3 | On sign-up, enter two different passwords | Refused, and **no account is created** — try signing in with it |
| 2.3 | BOTH | S3 | On sign-up, follow the Terms and Privacy links | Both open real pages, not a 404 |
| 2.4 | TEST | S2 | In onboarding, press **Skip for now** | Reaches the app. The dashboard says setup is unfinished |
| 2.5 | TEST | S2 | After skipping, sign out and back in | Returns you to setup. The skip lasts one visit only |
| 2.6 | TEST | S2 | After skipping, press **Finish setup** on the prompt | Opens **onboarding**, not the dashboard |
| 2.7 | TEST | S3 | Complete onboarding, then open `/onboarding` directly | Sent away — a finished account should not be able to re-enter |

---

## 3. Brand kit

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 3.1 | TEST | S2 | On a **brand-new** account, go to Brand Kit and upload a logo **without saving first** | Uploads. It must not demand a save first |
| 3.2 | BOTH | S3 | Save colours, name, address, licence number | Persist across a reload |
| 3.3 | BOTH | S3 | Leave the licence number blank and save | Refused, and the message **names the missing field** |
| 3.4 | BOTH | S3 | Upload an **SVG** logo, then publish a poster and send yourself a campaign | The poster still appears in the email. An SVG logo used to drop it entirely |

---

## 4. Posters

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 4.1 | BOTH | S2 | Templates → **Use template** | Editor opens with your brand already applied |
| 4.2 | BOTH | S2 | Edit text, move and resize something, wait, reload | Changes survived — it autosaves |
| 4.3 | BOTH | S3 | Press **Apply pharmacy info** | Name, address and phone fill the placeholders |
| 4.4 | BOTH | S3 | Export to PDF from the ⋯ menu | A real PDF downloads. **Not** a blank tab, and not the word `null` |
| 4.5 | TEST | S3 | Export as a **Free** account | The poster carries a watermark. On Starter and Pro it must not |
| 4.6 | BOTH | S2 | Leave a poster as **Draft**, then try to build a campaign with it | Refused. Draft means not for patients |
| 4.7 | BOTH | S2 | Publish a poster, **Archive** it, then try to send it | Also refused |
| 4.8 | TEST | S3 | As a **Free** account, create a 6th poster | Refused — Free is 5. Starter is 30, Pro unlimited |
| 4.9 | BOTH | S2 | Set a **Booking link** on your **Brand Kit**, then put `{{booking_url}}` on a poster and export it | The address prints. No `{{` anywhere on the page |
| 4.10 | BOTH | S1 | On a poster that carries a booking QR, export it and **scan the code with a phone** | It opens your booking page. Scanning is the only real test — a QR that looks fine and goes nowhere is indistinguishable by eye |
| 4.11 | BOTH | S2 | Clear the booking link on your Brand Kit, then export that poster again | The code is gone, leaving a gap. It must not print a square that scans to nothing |
| 4.12 | BOTH | S2 | Change your booking link, then re-export a poster you made before the change | The new address. Posters follow the brand kit, so an old one never points at a dead page |
| 4.13 | BOTH | S2 | Open **Profile** and look at the booking link | It shows the link you set in Brand Kit, with a link across to change it. There must be no second box to type it into here — one value, one place that writes it |
| 4.14 | BOTH | S1 | With no booking link set, open a poster and press the **QR** button in the editor toolbar | It takes you to Brand Kit rather than doing nothing. A disabled button with a tooltip is a dead end: the tooltip may never show, and following it means knowing where Brand Kit is |
| 4.15 | BOTH | S3 | Put that same poster on a display screen and look at the wall | The same code as the printed one — screens and exports share one render |

---

## 5. Contacts

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 5.1 | TEST | S2 | Add a contact by hand | Appears in the list |
| 5.2 | TEST | S2 | Import a small spreadsheet | A line-by-line report: added, skipped, or rejected **with the row number and the reason** |
| 5.3 | TEST | S2 | Import a file containing the same person twice — once matching on email, once on phone | Both caught as duplicates. Matching is on email **or** phone, not just email |
| 5.4 | TEST | S3 | As Free (50 contacts), import a file that would exceed the limit | It says which rows did not fit. It must not silently drop them |
| 5.5 | BOTH | S2 | Build a list of 60+ contacts, then **Export** | The file contains **all** of them — not just the page on screen |
| 5.6 | BOTH | S1 | Add a contact whose name starts with `=`, `+`, `-` or `@`. Export, open in Excel | The cell shows the text. It must **not** run as a formula. A leading tab in the cell is the guard working, not corruption |
| 5.7 | BOTH | S3 | Group contacts, then filter by group | Counts match what the list shows |

---

## 6. Campaigns

Use **your own email address** as a manual recipient throughout.

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 6.1 | BOTH | S1 | Build a campaign with a published poster and send it to yourself | Arrives. The **poster is in the body of the email**, not an attachment |
| 6.1a | TEST | S1 | In that email, right-click the poster and copy its address, then open it in a private browser window | It loads the poster. On a deployed environment the design must be a **link**, not an embedded image — that is what keeps a large campaign sendable, so an embedded one is S1. Locally it will be embedded on purpose; see 6.1d |
| 6.1b | TEST | S2 | Open the same email with images turned off, or in Outlook on a work machine | A **Not showing?** link under the space where the poster is. Following it shows the design |
| 6.1c | TEST | S3 | Change one character in the copied poster address and load it | Refused. It must not serve an image for an address that was not signed |
| 6.1d | BOTH | S1 | Check the API log for the campaign you just sent | On a deployed environment: no warning about `Public:ApiBaseUrl`. If one appears, the poster is being copied into every recipient's message and a large campaign will time out — set `Public__ApiBaseUrl` and send again before testing anything else in this section |
| 6.2 | BOTH | S2 | Look at the From line and the footer | Your **pharmacy's name**, not "Marketing25" and not a blank |
| 6.3 | BOTH | S2 | Open it on a phone | No sideways scrolling; the poster fits the width |
| 6.4 | BOTH | S3 | View it with images blocked | Still readable — there is a plain-text part |
| 6.5 | BOTH | S2 | Attach up to 5 videos and send | Thumbnails show; the links work for 48 hours |
| 6.6 | BOTH | S1 | Send to **8 or more** recipients at once | All of them arrive. This is the case that used to fail with a large poster |
| 6.7 | BOTH | S2 | Open the campaign's delivery report | Per-recipient success or failure with timestamps |
| 6.8 | BOTH | S3 | On a failed campaign, press **Retry** | Retries. Not offered on a stopped campaign, or on one everybody received |
| 6.9 | TEST | S2 | As a **Free** account, try to send | Refused with an upgrade prompt — Free has 0 recipients |
| 6.10 | BOTH | S3 | Enter a malformed address as a recipient | Refused before sending, naming the bad one |
| 6.11 | BOTH | S1 | Send to 250 recipients (use a spare domain, not real patients) | A confirmation step first, saying the number. Then **Queued**, then **Sending** with the count climbing, then **Sent** |
| 6.12 | BOTH | S1 | During 6.11, close the tab and reopen the campaigns page | The send carried on. It is not stuck at the number it was on when you closed it |
| 6.13 | BOTH | S1 | Start another 250 and press **Stop** while it says Sending | Stops. The count may rise by up to 20 more, then stays put. The status stays **Stopped** — refresh twice and check it has not turned into Sent |
| 6.14 | BOTH | S1 | Open the stopped campaign's delivery report | Everyone reached is listed as sent, by address; the rest are still pending. Nobody appears twice |
| 6.15 | BOTH | S2 | Find a campaign that reached some but not all of its list | A **Send to remaining N** button, with N the number missed. Pressing it emails only those N |
| 6.16 | TEST | S1 | On **Starter** (500/month), send 400, then immediately try to send 200 more without waiting for the first to finish | The second is refused, naming the 500 limit. Queued recipients count against the allowance |
| 6.17 | BOTH | S3 | Try to send more than 1,000 recipients in one campaign | Refused, with the cap named and a suggestion to split it |

---

## 7. In-pharmacy screens

Full walkthrough in `DISPLAY_SCREENS.md`. Requires Starter (1 screen) or Pro.

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 7.1 | TEST | S2 | As a **Free** account, open Screens and try to **add** one | Refused: *"In-pharmacy screens are not part of the Free plan."* Starter gets 1, Pro unlimited. Note the page itself opens — the refusal only comes when you add. If that reads as a dead end rather than an upgrade prompt, report it as S3 |
| 7.2 | BOTH | S2 | Add a poster that is still a **draft** to a playlist | Refused, saying it must be published first |
| 7.3 | BOTH | S3 | Put the same poster **twice in a row** in a playlist | Refused — on a wall that reads as the screen having frozen |
| 7.4 | BOTH | S3 | Set a dwell time of 1 second, then 400 | Both refused. The range is 3–300 seconds |
| 7.5 | BOTH | S2 | Register a screen, then open `/display` **in a private window** | The pairing box, **not** a sign-in form. A normal tab shares your session and proves nothing |
| 7.6 | BOTH | S1 | Type the six-character code | Pairs, and the loop starts within a few seconds |
| 7.7 | BOTH | S1 | **Pair a second screen** on the same account, right after the first | Also pairs. The second screen paired in a database used to fail with a server error |
| 7.8 | BOTH | S2 | Type the same code again on another device | Refused. Codes work once |
| 7.9 | BOTH | S3 | Wait for a code to expire, or press **New code** | The old one stops working. Codes last four hours |
| 7.10 | BOTH | S2 | Edit the playlist and save. Do not touch the device | It picks up the change within about 30 seconds, unprompted |
| 7.11 | BOTH | S2 | Unpublish a poster that is on a screen | It leaves the loop on the next poll, and shows struck through in the editor as *Not published — skipped* |
| 7.12 | BOTH | S1 | **Revoke** the credential from the Screens page | The device returns to the pairing box on its next poll |
| 7.13 | BOTH | S3 | Disconnect the device's network | It keeps looping from cache, with a small amber dot |
| 7.14 | BOTH | S3 | Check the **Last seen** column | Says *Live* for a running screen; a time for one switched off |
| 7.15 | BOTH | S2 | Press **Direct link**, open it on a device | Pairs without a code. Shown **once** and never again |
| 7.16 | BOTH | S2 | Register a screen, then look at it on the Screens page **straight away** | **Both** buttons are there — the pairing code control *and* **Direct link**. Direct link used to be hidden until the code expired, i.e. hidden for the whole installation |
| 7.17 | BOTH | S2 | In a playlist press **Add video** | Only your **Ready** videos are listed — nothing still making, failed, or being changed. A video row shows its length and *plays once*, with no seconds box |
| 7.18 | BOTH | S2 | Save a playlist with a poster and a video, on a **landscape** screen | The video plays **once, muted**, as a centred column with black either side — **not stretched, not cropped** — then the loop moves on. The address strip is hidden while it plays. The Screens page shows a *1 video* badge |
| 7.19 | BOTH | S2 | Same, on an **iPad or Mac in Safari** | The video plays. (Safari refuses video from a server that ignores ranges — a black slot here is an S1) |
| 7.20 | BOTH | S2 | Start a change to that video (refine) while it is on a screen | It leaves the screen within ~30 s, shows *Not ready — skipped* in the playlist, and comes back when the change finishes |
| 7.21 | TEST | S3 | Try to add an 11th video to one playlist | **Add video** is disabled with *Up to 10 videos per playlist* |
| 7.22 | BOTH | S3 | Disconnect the device's network with a video in the loop | Posters keep looping from cache; the video either plays from the browser's cache or is skipped. Never a broken or frozen frame |

---

## 8. Branches and head office

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 8.1 | TEST | S2 | Sign in as `riverside@example.test` and open **Branches** | Its three branches are listed |
| 8.2 | BOTH | S3 | Read the page | It says **branch** / **branches** throughout. Not "sub-pharmacy" |
| 8.3 | BOTH | S1 | Switch to a branch | The header names it **in amber** — you are not working as yourself |
| 8.4 | BOTH | S1 | Create a poster while switched, then switch back | It belongs to the **branch**, not to you |
| 8.5 | BOTH | S1 | Sign out **while switched to a branch** | Fully signed out. Not still authenticated as the branch |
| 8.6 | BOTH | S1 | Open a branch you do not manage, by editing the URL | Refused |
| 8.7 | BOTH | S3 | Sign in as a pharmacy with no branches | No branch tools offered |
| 8.8 | BOTH | S2 | Request access to another pharmacy by email address | Creates a request. It grants **nothing** until an admin approves |

---

## 9. Videos

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 9.1 | TEST | S3 | Generate a video from a topic | Progress updates; a 15-second vertical clip in 30–60 seconds |
| 9.2 | BOTH | S3 | Try each of the five free voices | All work |
| 9.3 | TEST | S3 | As **Free**, try to generate | Refused — Free is 0 per month. Starter 5, Pro 30 |
| 9.4 | BOTH | S3 | On a failed render, press retry | Re-runs and does **not** charge the monthly quota again |
| 9.5 | BOTH | S4 | Download a finished video | Plays, and is 9:16 |
| 9.6 | BOTH | S3 | Attach one photograph on the create dialog, then generate | The photo appears in the finished video — the opening shot. The rest is stock footage. The estimate does **not** go up |
| 9.7 | BOTH | S3 | Try to attach a photo over 8MB | Refused **immediately**, before any upload, naming the file and its size. Not "could not be attached" |
| 9.8 | BOTH | S4 | Try to attach a PDF, an SVG or an iPhone `.heic` | Refused: JPEG, PNG or WebP only. SVG is refused here even though the brand-kit logo accepts one |
| 9.9 | BOTH | S4 | Attach 5 photos | The fifth is refused; four is the limit |
| 9.10 | BOTH | S3 | On a finished video, *Ask for a change* → *Choose pictures* → add one | Re-cuts with it in. The words and the voice are unchanged, it costs nothing, and it does **not** use another video from the allowance |
| 9.11 | BOTH | S3 | Repeat 9.10 on a video that already has photos | They are **added**, not replaced. Refused past four in total, saying how much room is left |
| 9.12 | BOTH | S4 | Open *Ask for a change* on a video made with **AI imagery** | The *add pictures* control is not offered. Adding one would mean regenerating every other picture, which the plan pays for |
| 9.14 | BOTH | S3 | Attach a **landscape** photo. Drag it left and right, then drag up and down | Moves across only. Dragging up/down does nothing, and there is no *Down* slider |
| 9.15 | BOTH | S2 | Same photo: set **Zoom** to 60%, generate | Much more of the width is visible in the thumbnail, with a blurred copy of the photo above and below — no black bars, no stretching. The finished video's shot matches the thumbnail |
| 9.16 | BOTH | S3 | Attach a phone photo taken **upright** (portrait) | Upright in the thumbnail **and** in the finished video. It used to render on its side |
| 9.17 | BOTH | S3 | Ctrl + scroll (or pinch on a trackpad or phone) over a photo | Zooms, between 50% and 150%. An ordinary scroll over it still scrolls the dialog |
| 9.18 | BOTH | S3 | Repeat 9.15 via *Ask for a change* → *Choose pictures* | Same controls, same result |
| 9.19 | BOTH | S4 | Open a video made before zoom existed, *Ask for a change* on the words only | Its photos look exactly as they did. Old videos are not reframed |
| 9.20 | BOTH | S2 | Attach four photos on a laptop (or shrink the window to ~700px tall) | The dialog scrolls, and *Generate* can be reached. Each Zoom slider is wide enough to drag |
| 9.21 | BOTH | S3 | On a phone, attach a landscape photo, then swipe up over it | The dialog scrolls. A sideways drag moves the photo |
| 9.22 | BOTH | S1 | On a finished video, *Ask for a change*: "make the captions bigger" | A plan is shown saying the captions will change, free. **Not** "We could not work out what to change" |
| 9.23 | BOTH | S3 | Ask for "make it about flu instead" | Told it needs the words rewritten and that this is not open yet, with the suggestion to make a new video. Not a generic failure |
| 9.24 | BOTH | S3 | Watch a video whose script says the same sentence at the start and the end | That caption appears once |
| 9.25 | BOTH | S1 | Ask for a change that cannot be made (e.g. "add music") | When it finishes, a message says the change wasn't made and why, and the card shows "Last change not made: …". It must not just reload the same video in silence |
| 9.26 | BOTH | S1 | On a **new** video with one of your photos, ask "make the captions bigger" | Bigger captions. Your photo is still there, at the same moment, and every other shot is where it was |
| 9.27 | BOTH | S2 | On a new video, name a shot by what you can see ("the person washing their hands") and ask for something else there | That shot changes. The other shots do not |
| 9.28 | BOTH | S3 | Ask to replace one of your own photos by describing it | Told to remove it from the video's pictures instead. Not a silent re-cut |
| 9.29 | BOTH | S2 | Generate three videos on a narrow topic (few stock matches) and watch each | No shot appears twice back to back. If a clip must repeat, it shows a different part of the clip, not the same frames |
| 9.30 | BOTH | S2 | On a video with a repeated shot, ask "remove the duplicate video" | The later repeat is replaced with something that fits what is being said; the first stays; nothing else changes |
| 9.31 | BOTH | S1 | New video with two photos. Ask "change the last shot to a pharmacist giving a flu jab" | The last moment becomes footage of that. The first photo and the middle shots are unchanged. Ask a second change: the replaced photo does not come back |
| 9.32 | BOTH | S2 | Add a picture to a finished video in a way that fails (e.g. stop the ai-service mid-render), then ask a shot change | The video stays Ready. The added picture is not treated as part of the video |
| 9.35 | BOTH | S1 | New video. *Ask for a change* | Every shot is a tile with a still, a time and its caption line; the count matches the shots you see when it plays. Your photo is labelled |
| 9.36 | BOTH | S1 | Tap the last tile, type "a person stepping on a scale", *Change this shot and re-cut* | Only the last shot changes, to that. No preview, no refusal about what the shot "shows". Free: the allowance does not move |
| 9.37 | BOTH | S2 | Tap a tile and leave the box empty | That shot changes to something on the video's own topic |
| 9.38 | BOTH | S2 | Type "replace the one with medicine with a salad" (no position) | Refused with "Tap the shot you want to change…". Nothing re-cut |
| 9.39 | BOTH | S2 | Type "change the last shot to people walking" | The last shot changes |
| 9.40 | BOTH | S3 | Open *Ask for a change* on a video made before this release | Tiles are numbered, not stills, and still work. After one change, stills appear |
| 9.41 | BOTH | S3 | Change a shot, then reopen *Ask for a change* straight away | The changed tile shows the new still, not the old one |
| 9.33 | BOTH | S1 | New video on any topic. Count the shots while it plays, then ask "change the last shot to <something>" | The shot count equals the number of caption lines: no extra clips after the last line. The last shot changes, whatever it showed |
| 9.34 | BOTH | S2 | Ask "change the second shot to …" | The second shot changes; no refusal about what it "shows" |
| 9.13 | BOTH | S2 | Delete a video that had the **brand logo** pinned into it | The video goes. The logo is still on the brand kit and on every poster using it |

---

## 10. Plans and billing

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 10.1 | BOTH | S3 | Open Pricing | Free £0, Starter £7/mo, Pro £25/mo |
| 10.2 | BOTH | S3 | Check the usage counters, then open Design with AI | Counters show posters, videos and recipients used this month; Design with AI shows the AI designs left this month |
| 10.3 | TEST | S2 | Upgrade a throwaway account | Limits lift immediately |
| 10.4 | TEST | S3 | Downgrade an account that has screens registered | Existing screens keep running. That is deliberate — blanking a shop window over a billing change would be worse |
| 10.5 | BOTH | S2 | As a head office, switch into a branch and open Plan & Billing | The branch's plan is shown with a note that only its own account holder can change it. No "Choose" buttons, no top-up packs, no "Manage billing" link |
| 10.6 | BOTH | S2 | Switch back to your own pharmacy and open Plan & Billing | Your own plan buttons and "Manage billing" are back |

---

## 10a. Social accounts

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 10a.1 | BOTH | S1 | **After any change of web address** (a new domain, `www`, a new environment): connect one Facebook page from Social & Ads, signing in for real | Facebook returns to the app and the page appears as connected. "URL Blocked" means the address's `/social/callback/meta` is missing from Facebook Login → Valid OAuth Redirect URIs — the one thing `pnpm smoke` cannot read |
| 10a.2 | BOTH | S2 | Open Social & Ads as a pharmacy that was already connected | Still connected, no reconnect asked for. Otherwise the encryption key changed (`pnpm smoke` readiness: "social tokens") |
| 10a.3 | BOTH | S2 | Connect **LinkedIn**, sign in, keep your profile ticked, **Connect** | The profile is listed as *LinkedIn profile*. A blank 400 page straight after signing in is LinkedIn's own sign-in page: press Connect again |
| 10a.4 | BOTH | S2 | **Share to social** on a published poster → tick the LinkedIn profile → **Publish now** | Posts → History shows **Published** with a `linkedin.com/feed/update/…` link that opens the post. Delete it on LinkedIn afterwards — LinkedIn posts are public |
| 10a.5 | BOTH | S3 | Same with a Ready video to LinkedIn | Published within a few minutes (LinkedIn processes the video first) |
| 10a.6 | BOTH | S3 | A caption with brackets, an @, an underscore and a `#hashtag`, e.g. "Open (till 9pm) @ the counter #FluJab" | Appears on LinkedIn exactly as typed; the hashtag is a link |
| 10a.7 | BOTH | S3 | Connections tab, LinkedIn account | No expiry note until 14 days before the 60-day sign-in ends; then *The sign-in expires on …* with **Reconnect**. Reconnect keeps the same connection (no duplicate) |
| 10a.8 | TEST | S2 | Connect **YouTube** with a test-user Google account that owns a channel (API set to `Social__Google__YouTubePrivacy=unlisted`) | The channel is listed. YouTube is not offered when sharing a poster |
| 10a.9 | TEST | S2 | Share a Ready video to YouTube with a short first caption line | **Published** with a `youtu.be/…` link; the first line is the title. Private until Google audits the project |
| 10a.10 | BOTH | S2 | Disconnect an account, then share a poster | The account is gone from the list and from the share dialog; scheduled posts to it fail with "disconnected" |
| 10a.11 | BOTH | S3 | Press **Cancel** on the platform's consent page | Back in the app: "You cancelled the connection. Nothing was changed." |
| 10a.12 | BOTH | S2 | Connect **TikTok** as a sandbox target user whose TikTok account is set to private | The account is listed as *TikTok*. A TikTok error page instead usually means the redirect URI or client key in the TikTok app is wrong |
| 10a.13 | BOTH | S2 | **Share to social** on a Ready video → tick TikTok | A TikTok box shows the account's nickname; **Who can see this post** has nothing chosen; Comment, Duet and Stitch are off (greyed if switched off in TikTok); Publish is disabled until an audience is chosen. TikTok is not offered for a poster |
| 10a.14 | BOTH | S2 | Choose **Only me** → **Publish now** | **Published** after TikTok processes it (no link for a private post); the video is in the TikTok app |
| 10a.15 | BOTH | S3 | Turn on **Disclose commercial content** → **Branded content** with **Only me** | Refused: branded content cannot be private. "Only me" is greyed; the consent line mentions the Branded Content Policy |
| 10a.16 | BOTH | S3 | Tick two TikTok accounts on one post | "Choose one TikTok account per post" and Publish is disabled |
| 10a.17 | BOTH | S3 | Try to add TikTok to a recurring post | Not offered (and refused by the API): TikTok wants each post chosen by a person |
| 10a.18 | BOTH | S2 | Connect **X**, sign in, **Authorize app** | The account is listed as *Name (@handle)*. An X error page instead usually means the callback URI in the X app is not `…/social/callback/x`, or the app is not a Web App |
| 10a.19 | BOTH | S2 | **Share to social** on a published poster → tick X → **Publish now** | An *X: n / 280* line appears under the caption. **Published** with an `x.com/i/web/status/…` link showing the poster. Delete it on X afterwards |
| 10a.20 | BOTH | S3 | Same with a Ready video to X | Published within a few minutes (X processes the video first) |
| 10a.21 | BOTH | S2 | With X ticked, a caption over 280, or a short one with ten links | The X line turns red ("too long for X") and Publish is disabled. Untick X: Publish is allowed again |
| 10a.22 | BOTH | S3 | Publish the exact same caption to X twice | The second fails with "the same text is already on this account" |
| 10a.23 | BOTH | S3 | Try to add X to a recurring post | Not offered (and refused by the API): X refuses repeated text, and a series repeats its caption |

## 10b. Recurring posts

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 10b.1 | BOTH | S2 | As **Free**, open Social & Ads → Recurring | "Not included in your plan" with See plans. No way to create one |
| 10b.2 | BOTH | S2 | As **Starter**, create a weekly series (poster, Facebook or LinkedIn, Mon and Thu 09:00) | Review shows the next dates in your time zone; after saving, the list and 14-day calendar show them |
| 10b.3 | BOTH | S2 | As Starter with 2 active series, try a third | Refused with the plan limit; Pause one and it is allowed |
| 10b.4 | TEST | S2 | A daily series a few minutes from now | Posts at the time; Recent shows a tick; the pharmacy gets the "posted" email (unless set to problems only) |
| 10b.5 | BOTH | S2 | **Pause**, then **Resume** | Nothing posts while paused; history is kept; resumes from the next future date (nothing sent late) |
| 10b.6 | BOTH | S3 | **Edit** the time of an active series | Only future posts move; past results unchanged |
| 10b.7 | TEST | S2 | Disconnect one of two accounts on a series | The other account still gets each post; disconnecting the last pauses the series and emails why |
| 10b.8 | BOTH | S3 | **Make recurring** from a poster's or video's menu | The stepper opens with that poster or video chosen |
| 10b.9 | BOTH | S3 | **End** a series | It shows Ended and cannot be resumed; posts already sent stay |

## 10c. Lists: sort and date

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 10c.1 | BOTH | S3 | On My Posters choose **Oldest first** and **Last 7 days** | The list changes; the address bar carries `sort=oldest&from=…&to=…`; reloading or sharing the link keeps the view |
| 10c.2 | BOTH | S3 | Choose a **Custom** range with nothing in it | *No results in this date range* with a button that clears the filter |
| 10c.3 | BOTH | S4 | Repeat on Templates, Videos, Campaigns, Contacts and the admin users, pharmacies and audit tables | Same toolbar and behaviour everywhere |

---

## 11. Things that must never happen

Run these last, deliberately. Every one is S1.

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 11.1 | BOTH | S1 | Copy a poster URL from pharmacy A. Sign in as B. Open it | Refused |
| 11.2 | BOTH | S1 | Same for a campaign, a patient, a playlist and a screen | All refused |
| 11.3 | BOTH | S1 | As a pharmacy, open `/admin` | Refused. A signed-in pharmacy is not an administrator |
| 11.4 | BOTH | S1 | Paste a kiosk display URL into a normal browser and change the poster id in it | Refused. A screen reaches only its own playlist |
| 11.5 | BOTH | S1 | On a screen playing a video, copy the video's address (devtools → Network → `/api/display/video/…`) and swap in the id of another of your videos, keeping `sig` | Refused (403). The link opens that one video only, and contains neither the screen token nor a storage key |
| 11.5 | BOTH | S1 | Anywhere in the app, look for another pharmacy's name or contact in a list, a count or a dropdown | Never appears |
| 11.6 | TEST | S1 | Create a video with `images: [{ blobKey: "posters/<another pharmacy's id>/<poster>.png" }]` posted by hand | Refused. The key is sent by the caller, and it is used twice — the worker downloads it, and deleting the video deletes it — so a trusted key would render their picture into your video and destroy their file when you tidied up. Every key must sit under your own `videos/{you}/attachments/`, `posters/{you}/` or `brand/logos/{you}/` |
| 11.7 | TEST | S1 | Same, on `POST /api/videos/{id}/pictures` for a finished video | Refused identically. Both doors take a blob key from the request body, so both check it |

---

## 12. The whole window

Quick sweep, on every page you visited.

| ID | Env | Sev | Do | Should |
|---|---|---|---|---|
| 12.1 | BOTH | S3 | Open the account menu and the pharmacy switcher | Solid panels. Not see-through with text showing through |
| 12.2 | BOTH | S3 | Scroll a long page with a banner showing | The banner and the page header do not overlap |
| 12.3 | BOTH | S2 | Click every link in the sidebar and the footer | Every one reaches a real page |
| 12.4 | BOTH | S3 | Watch for spinners | None that never resolve |
| 12.5 | BOTH | S3 | Resize to a phone width | Nothing scrolls sideways; nothing is cut off |
| 12.6 | BOTH | S4 | Read the product name everywhere | **Marketing25** throughout — including poster templates, email footers and page titles |

---

## Sign-off

| | |
|---|---|
| Tester | |
| Date | |
| Environment | TEST / PROD |
| Build or deploy | |
| Cases run | of 191 |
| S1 open | |
| S2 open | |
| Safe to release? | YES / NO |

An unrun case is not a pass. If the count is short, say which ones and why.

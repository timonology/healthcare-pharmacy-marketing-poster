# Admin guide

Everything the admin area does, who can do it, and the rules the system
enforces regardless of what the UI shows.

Poster template authoring has its own guide: [TEMPLATE_AUTHORING.md](./TEMPLATE_AUTHORING.md).

## Getting in

Sign in normally. If your account is staff, a **Switch to admin** link appears
in the sidebar; the admin area lives at `/admin` and has its own navigation.
**Switch back to app** returns you to the pharmacy-facing product.

The first SuperAdmin is created on an empty database from the `SuperAdmin__*`
settings. It is skipped once any SuperAdmin exists and never resets an existing
password.

## Demo accounts (development only)

A fresh database seeds 15 accounts so the admin console has something to work
with: 1 SuperAdmin (from `SuperAdmin__*`), 2 Admins, and 15 pharmacies — twelve independents plus Riverside's three branches.

Password for every seeded account: **`TestPass123!`**

| | |
|---|---|
| Admins | `nora.admin@example.test`, `sam.admin@example.test` |
| Pharmacies | `riverside@example.test`, `oakfield@example.test`, `harbour@example.test`, … |

They are deliberately varied so every filter has data on both sides — 5 Free /
4 Starter / 3 Pro, 2 deactivated, 2 still onboarding, 3 that have never signed
in, 6 unassigned and 6 split across the two account managers, 5 holding video
credits.

**Guarded twice.** It returns immediately outside the Development environment,
and again if `DevData:SeedUsers` is `false`. A seeder that creates
known-credential accounts must not be one config mistake away from production.

Idempotent by email: an account that already exists is left completely alone,
so restarting never overwrites changes you made while testing. To get a clean
set back, delete the accounts (or the database) and restart.

## Roles

| | PharmacyUser | Admin | SuperAdmin |
|---|---|---|---|
| Use the product (posters, campaigns, videos) | yes | yes | yes |
| See the admin area | — | yes | yes |
| View all accounts, pharmacies, subscriptions | — | yes | yes |
| Activate / deactivate a pharmacy | — | yes | yes |
| Change a pharmacy's plan, grant video credits | — | yes | yes |
| Assign a pharmacy to an account manager | — | yes | yes |
| Create, publish and delete poster templates | — | yes | yes |
| View the audit log | — | linked pharmacies + own actions | everything |
| Create or edit administrators | — | — | yes |
| Promote or demote any account's role | — | — | yes |

**Admins are not scoped to a subset of pharmacies.** Every administrator sees
and can act on every pharmacy account. `ManagedByAdminId` records who owns the
relationship — it is organisational metadata for reporting and workload, *not*
a permission boundary. Do not rely on it to restrict access.

**Admins cannot act on other staff.** An Admin may manage any pharmacy account
but cannot touch an Admin or SuperAdmin. Only a SuperAdmin can.

### Rules the system enforces

These are checked server-side and cannot be bypassed from the UI:

- You cannot change your own role.
- You cannot deactivate your own account.
- You cannot demote the last active SuperAdmin — the platform must always have
  at least one.
- An Admin who still has pharmacies assigned cannot be demoted until those are
  reassigned, so accounts are never orphaned.
- Pharmacies cannot be assigned to a deactivated administrator.
- Granted video credits must be between 1 and 1000.

## Overview

Account composition, plan mix, and an estimated revenue figure.

**Total accounts = Pharmacies + Staff.** The Pharmacies page lists only the
pharmacy figure, so if most of your accounts are staff that page will show far
fewer rows than the total — that is the filter working, not data missing.

**Revenue is an estimate.** It prices every paid account at list rate and
ignores annual plans, promo codes, proration, refunds and failed payments.
Stripe is the source of truth for actual revenue; this is a health signal.

## Users

Every account on the platform, staff included. Search by name, email or
pharmacy; filter by role, status, plan and onboarding. Sort by name, plan or
last sign-in.

**Click an account name** to open its full record — contact, plan, credits,
billing state, onboarding, account manager, sign-in history — with every action
that applies to it in one place: change plan, grant credits, assign a manager,
change role (SuperAdmin), activate or deactivate.

Deactivating blocks sign-in immediately. It does not delete anything: posters,
campaigns and videos are retained, and reactivating restores access.

### Bulk actions

Tick rows to activate, deactivate or reassign several accounts at once.

Bulk runs are **one request per account**, deliberately. Every rule above is
enforced per request, so a bulk action can never do something an individual
action would refuse — selecting a staff account as an Admin, or your own
account, simply gets refused for that row while the rest proceed.

Nothing aborts on the first failure. Stopping partway through twenty accounts
would leave you unsure which had changed; instead every row is attempted and
the refusals are listed afterwards with the reason for each.

Selection is per page and clears when you page or change a filter — a bulk
action should only ever hit rows you can actually see.

## Pharmacies

Every account that **has a pharmacy**, whatever platform role it holds. Staff
accounts with no pharmacy of their own are under **Users** instead. The count
in the panel title matches the **Pharmacies** card on the Overview.

Each row shows the pharmacy, contact, plan, assigned administrator, onboarding
status and join date. Reassigning an account manager here is bookkeeping: it
changes who is responsible, not who has access.

One account is one pharmacy. There is no concept of several logins sharing a
pharmacy — `PharmacyName` is a field on the account.

**Adding pharmacies (super admins).** **Add or import pharmacies**, at the top
of this list, opens **Invite & import**: invite one pharmacy, or import a CSV
(name and email columns; download the template there). Each pharmacy is created
and emailed an activation link. Nothing is created until you have seen the
preview of what the file contained. Ordinary admins do not see this — creating
accounts and sending sign-in emails is limited to super admins.

### Why "has a pharmacy" and not "role is PharmacyUser"

These are two different questions and it matters which one this page asks.

Role answers *what may this person do on the platform*. Having a pharmacy
answers *what is this account*. They start out aligned, because everyone who
registers is a PharmacyUser with a pharmacy. They come apart the moment you
promote a pharmacy owner to Admin so they can help run things: their role
becomes Admin, while their pharmacy, its patients and its campaigns carry on
exactly as before.

This page used to filter by role, so a promoted owner's pharmacy disappeared
from the only page that lists customers — while still appearing under Users,
which made it look like the Pharmacies page was broken. It now filters on the
pharmacy itself, and any row whose holder also has a staff role is marked with
a role badge.

Two consequences worth knowing:

- **The Overview's "Pharmacies" card counts pharmacies, not PharmacyUsers.**
  When the two differ, the card says how many owners also hold a staff role.
- **The role breakdown still sums to the total.** `pharmacyUsers + admins +
  superAdmins == totalUsers` is a statement about roles and remains true.
  `pharmacyAccounts` is a separate business count and is greater than or equal
  to `pharmacyUsers`. If you are reconciling numbers, use the one that matches
  the question you are asking.

## Supporting a pharmacy

**View data** on any pharmacy row opens that account's workspace at
`/admin/pharmacies/{id}` — patients, groups, posters, campaigns and videos, in
tabs. The tab is in the URL, so you can paste a link to the exact delivery
report you are describing into a ticket.

This view is **read-only**, with one exception noted below. Changing a plan,
status or account manager still happens through the account drawer on the
pharmacies list, where the confirmations and guard rails live. Looking and
changing are deliberately two different gestures.

### Patient contact details are hidden

Patients belong to the pharmacy, not to us. Their emails and phone numbers
arrive at your browser already masked — `j•••••@gmail.com`, `•••••123`. The
real values are not in the page, not in the network response, and not
recoverable from what you can see. The same masking applies to the recipient
column of a delivery report, which is a patient contact list wearing a
different hat.

**Reveal** on a patient row asks why, then shows the real details and writes an
entry to the audit log naming you, the pharmacy, the patient and the reason you
typed. A reason shorter than five characters is refused.

Read that as it is meant: the reveal exists because support sometimes genuinely
needs it — a bounced address, a patient insisting they never received anything
— and the log exists because a pharmacy is entitled to ask what we did with
their list, and "an administrator looked, we don't know who or why" is not an
answer we should have to give. Reveal when you need to. Say why. Don't browse.

### Delivery reports

Open a campaign to see what happened to each recipient, with the provider's
reason for every failure. Failures sort to the top. This answers most
"they never got it" queries without leaving the page.

Campaigns whose poster has since been deleted show **(poster deleted)** rather
than a blank — the campaign record is intact, the artwork is gone.

### Re-sending a failed campaign

A campaign in the **Failed** state can be re-sent from its dialog. Nothing else
can — you cannot compose or send anything new on a pharmacy's behalf, only
re-attempt a message they wrote and already tried to send. The plan's monthly
recipient allowance is re-checked first, so a re-send cannot push an account
past a limit it would have hit itself.

It is recorded in the audit log against your name. Messages go to real patients
from the pharmacy's name, so tell the pharmacy you have done it.

### Group counts

Group membership is recounted from the patients themselves each time this page
loads, rather than read from the stored counter. If the number here disagrees
with what the pharmacy sees on their own screen, the number here is the one a
campaign would actually send to.

## Subscriptions

Plan distribution and per-account billing state. Overriding a tier here changes
what the product unlocks **without touching Stripe** — useful for comping an
account or fixing a failed webhook, but it will not create or cancel a real
subscription.

If the account has a live Stripe subscription, the override is recorded with an
explicit warning, because **the next Stripe webhook will overwrite it**. A
manual tier on a Stripe-billed account is temporary by nature. For lasting
billing changes, make them in Stripe and let the webhook update the account.

## Analytics

Platform-wide activity over a rolling 7, 30 or 90 day window: posters created,
videos generated and campaigns sent, plus the most active pharmacies.

Two things worth knowing about how the numbers are built:

- **Campaigns are counted on the day they were sent**, not created. A campaign
  drafted in January and sent in March is March's activity, and unsent drafts
  never appear.
- **Days with no activity are included.** The database only returns days that
  have documents; plotting that directly would draw a straight line from one
  active day to the next, which reads as steady activity across a gap where
  there was none.

The window ends today and starts at midnight UTC, so the first bucket is a
whole day rather than a partial one that looks like a slump.

"Most active pharmacies" ranks by combined output. Every account is ranked, not
a merge of per-category top tens — an account placing just outside the cut in
each category can still have the highest combined total, and merging truncated
lists would hide it. An account deleted while its output remains shows as
"(deleted account)" rather than being dropped, so the table still reconciles
with the chart above it.

This is separate from the pharmacy-facing `/analytics` page, which is scoped to
a single account and gated to the Pro tier.

## Templates

Covered in [TEMPLATE_AUTHORING.md](./TEMPLATE_AUTHORING.md). In short: the
gallery is shared platform-wide, anything published is visible to every
pharmacy immediately, and deleting removes it for everyone.

## Audit log

Answers "who did what", including what they opened. Two collections in the
application's own database, one shape:

| Collection | Holds | Kept |
|---|---|---|
| `audit_events` | Every change (posters, templates, videos, campaigns, patients and groups, brand kit, subscription, screens and playlists, admin actions) and every security event (sign-in, sign-out, failed sign-in, password reset, anything refused) | Indefinitely |
| `activity_events` | Views from an allow-list: opening a poster, template, video, patient, group or branch; the dashboard (at most once an hour per person); admin console pages | 90 days (TTL index) |

### The three tabs

The console (Admin → Audit log) never shows one undifferentiated list.

- **Security audit** opens on staff actions, sign-in events and anything
  refused (401/403). *All changes by people* widens it to every pharmacy
  edit and every failure;
  *System* shows seeders and the smoke-test account, which are kept out of the
  default view so a deploy check does not bury real events.
- **Staff activity** — what administrators changed (*Changes*) and opened
  (*Views*), including while managing a pharmacy.
- **Pharmacy activity** — the same for pharmacy users.

Filter by date, actor (email or id), pharmacy id, action prefix (`poster.`,
`admin.impersonate`), entity type and entity id. **Export CSV** downloads the
current filters, newest first, up to 10,000 rows; the export is itself audited
(`admin.audit.export`). Cells that a spreadsheet would treat as a formula are
neutralised.

### Who sees what

| | SuperAdmin | Admin | Pharmacy user |
|---|---|---|---|
| Security audit | every tenant | linked pharmacies + own actions | — |
| Staff / pharmacy activity | every tenant | linked pharmacies + own actions | — |

"Linked" means pharmacies assigned to that Admin (`ManagedByAdminId`). This is
the one place that assignment limits what an Admin sees: elsewhere it is
organisational metadata, but another administrator's work on another
portfolio is not theirs to review. The rule is applied in the database query
from the caller's token, so paging and totals are honest and no query string
widens it.

The legacy staff-only list (`GET /api/admin/audit`) is now SuperAdmin-only and
will be removed after one release; its history was copied into
`audit_events` on first boot (`meta.source = admin_audit`).

### Reading a row

| Field | Meaning |
|---|---|
| `actorId`, `actorEmail`, `actorRole` | The person. An admin managing a pharmacy is the admin, not the pharmacy. |
| `actorKind` | `pharmacy`, `admin` or `system` (seeders, the smoke account, the Stripe webhook). |
| `pharmacyId` | The tenant they acted as: their own pharmacy, the one being managed, or empty for staff work outside one. |
| `action` | Dotted name. Prefixes: `poster.` `video.` `campaign.` `patient.` `brand.` `display.` `template.` `subscription.` `pharmacy.` `profile.` `social.` `auth.` versus `admin.`. |
| `result` | `ok`, `denied` (401/403 — refused, including by a plan limit) or `failed` (anything else that did not work). |
| `ip`, `requestId` | The browser's address (forwarded by the web app) and the id the API returns in `X-Request-Id`, so a support report can be matched to its row. |
| `meta` | Small extras: counts (`recipientCount`, `patientCount`), a failure `reason`, the HTTP `status` when not ok. |

**Manage-as.** Staff entering a pharmacy without a membership is recorded as
`admin.impersonate.start` / `.stop`, actor = the admin, `pharmacyId` = the
shop, and everything done inside is filed the same way. A head office working
as its own branch is pharmacy work (`auth.act_as.start` / `.stop`).

### What is never stored

Passwords (including wrong ones), tokens, card details, and lists of
recipients or patients — only how many. This is enforced when the event is
created, not left to each caller: sensitive keys are dropped, token- and
card-shaped values are redacted, and a value holding more than one email
address is replaced with `[list redacted]`.

### What is deliberately not logged

Health checks, static files, token refresh, list polls (the videos page
refreshes every two seconds), and the display screens' 30-second content
poll. A screen is logged when it pairs (`display.pair`) and when its content
request is refused (`display.content_denied`). Any other GET is logged only if
it is on the allow-list above.

### For developers

Every POST/PUT/PATCH/DELETE action carries `[Audit("area.verb", "entity")]`
or `[NoAudit("reason")]`; `scripts/check-audit.mjs` fails `pnpm test`
otherwise. Views opt in with `[ActivityLog(...)]`. The middleware writes the
row after the response is decided, so a request refused by authorization is
still recorded as `denied`; services add what only they know (the target
pharmacy, a count) through `IAuditScope`. Repeat views by the same person of
the same thing, in the same pharmacy, within 60 seconds are one row (an hour
for the dashboard).

- **There is no delete.** An audit trail that can be edited is not an audit
  trail. Retention on `audit_events` changes only if legal says so.

## Settings (super admins only)

`/admin/settings`. The tab is hidden from ordinary administrators, and the API
refuses them regardless of what the browser shows.

### Video generation model

Which Anthropic model writes the script and research for every marketing video.
Three options, cheapest first:

| Model | Relative cost | When it's worth it |
|---|---|---|
| Haiku 4.5 | 1x | The default. Fine for 15 seconds of scripted narration. |
| Sonnet 5 | ~3x | Noticeably better writing and research. |
| Opus 5 | ~5x | Rarely worth it here — the script is short. |

The screen shows **where the value came from**: `set here` when a super admin
has chosen one, otherwise `configuration` (the deployed `ANTHROPIC_MODEL`) or
`built-in default`. That matters because "why is it using that model?" is
otherwise a question only a developer can answer.

**Reset** drops the override and returns to whatever the environment supplies.
It's a separate button because after a few months nobody remembers which option
matches the deploy.

Three things worth knowing:

- **It takes effect on the next video the worker picks up.** Videos already
  rendering finish on the model they started with — switching mid-pipeline
  would mix two models' output in one script.
- **Only models on the allow-list can be selected.** The value goes straight to
  the Anthropic API, and a typo would fail every video for every pharmacy with
  an error nobody would trace back to this screen. Adding a model is a code
  change, deliberately.
- **Cost scales with every video on the platform.** Moving from Haiku to Sonnet
  roughly triples the per-video model cost on every plan. Check
  `Cost_and_Revenue_Model.xlsx` before switching.

Every change is written to the audit log as **Changed a platform setting**, with
the old and new values.

## Configuration

| Setting | Where | Purpose |
|---|---|---|
| `SuperAdmin__Enabled` / `Email` / `Password` | API | Bootstrap the first SuperAdmin |
| `AiService__BaseUrl` | API | Reach ai-service for template and image generation |
| `PEXELS_API_KEY` | ai-service | Stock photography |
| `REPLICATE_API_TOKEN` | ai-service | AI image generation (optional — falls back to stock) |
| `TEMPLATE_DESIGN_MODEL` | ai-service | Model used to design templates |

**The JWT secret must match across services.** The API forwards your token to
ai-service, which verifies it independently. Both log a fingerprint at startup:

```
JWT config: secret_fingerprint=1523cfc5123a issuer=acme-api audience=acme-clients
```

If those two lines disagree, every admin AI feature returns 401. In development
the API reads `JWT_SECRET` from the repo-root `.env` — the same file ai-service
uses — so a stale `dotnet user-secrets set Jwt:Secret` no longer silently wins.

## Troubleshooting

**"I asked for a password reset and nothing arrived"** — the page cannot say
why (it answers the same for every address, so nobody can use it to find out
who is a customer), so check in this order:

1. **Has the account ever been activated?** An invited or imported pharmacy has
   no password yet. Asking for a reset now sends its **activation link**
   instead (it used to send nothing). Or resend it from **Invite & import**.
2. **Was it refused as too frequent?** The page now says so. If many people
   are refused at once, the API log shows `Rate limit account-email refused …
   from <address>` with the same address every time: the web app and API
   client-address keys differ (`CLIENT_IP_SIGNING_KEY` / `Edge__ClientIpKey`),
   so everyone shares one address. `pnpm smoke` checks this.
3. **Did the provider refuse it?** The API log says `Password reset email to …
   was not accepted` with Mailjet's reason. As a super admin,
   `GET /api/admin/diagnostics/email` lists configuration problems and
   `POST /api/admin/diagnostics/email/test` with `{"to": "…"}` sends a test
   (there is no screen for these yet).
4. Otherwise it was accepted for delivery — check spam, and Mailjet's own log.

**"The design service isn't configured"** — `AiService__BaseUrl` is unset on the
API. `http://ai-service:8000` under docker-compose, `http://localhost:8000`
otherwise.

**401 from ai-service** — compare the two startup fingerprints above.

**Changes to ai-service have no effect** — it runs from a built image with no
source mount. `docker compose up -d --build ai-service`; restarting alone
re-runs the old code.

**Filtering by role shows far fewer accounts than exist** — fixed, but worth
knowing if you see it on an old deployment. `role`, `tier` and `isActive` were
added after accounts already existed. They carry `[BsonDefaultValue]`, which
makes an old document *read back* with the default — so the unfiltered list
looked correct — but a query runs in the database, where a document lacking a
field does not match an equality test against that field's default. Filtering
by role therefore returned only accounts created after roles shipped, and the
dashboard's pharmacy count was wrong the same way.

`UserFieldBackfill` now writes the defaults in at startup, and the queries also
match absent fields so results are right even before it runs. The backfill is
idempotent and logs how many documents it repaired.

**A template edit reverted after deploy** — this was the old behaviour, where
the seeder overwrote built-in templates on every boot. It is now insert-only.
The trade-off is that editing a built-in template in code no longer updates an
environment that has already seeded it.

## In-pharmacy display screens

Pharmacies can run screens showing a loop of their published posters. Support
questions usually land on one of these:

- **"Screens are not part of the Free plan."** Free gets none, Starter one, Pro
  unlimited. The limit is checked when a screen is *registered*, so an account
  that downgrades keeps the screens it already has — deliberately, because
  blanking a shop window over a billing change is worse than briefly having more
  screens than the plan covers.
- **"The pairing code doesn't work."** Codes last four hours and work once.
  There is a **New code** button on the pharmacy's Screens page.
- **"A screen has gone dark."** The Screens page shows *Last seen* per screen.
  Anything past a couple of minutes means the device is off, asleep, or off the
  network — screens poll every thirty seconds.
- **"A poster vanished from the loop."** It was unpublished or deleted. It stays
  visible in the playlist editor, struck through, rather than disappearing.

Staff have no separate view of a pharmacy's screens. A head office manages its
branches' screens by working as the branch, which the audit log already records.

Full detail in `DISPLAY_SCREENS.md`.

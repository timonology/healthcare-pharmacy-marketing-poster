# Authoring poster templates from the admin area

> For roles, permissions, user management and the audit log, see the
> [Admin guide](./ADMIN_GUIDE.md).

You no longer need a developer to add a template. This describes how it works,
how to get good results, and the two things to watch out for.

## The workflow

**Admin → Content → Templates → Design with AI**

1. Write a brief: what the poster advertises, who it's for, the mood.
2. Generate. Takes 30–90 seconds — it designs, then checks its own work against
   the print rules and fixes anything that fails.
3. Preview appears on the right, with the progress captions while it works.
4. Then one of three things:

| Button | What it does |
|---|---|
| **Publish to gallery** | Visible to every pharmacy immediately. |
| **Refine with AI** | Asks what should change, then designs **again** from your brief plus that note, around the same photograph. |
| **Edit in designer** | Opens the real poster editor over the page — properties, layers, the lot — and your edits come back with you. |

**Refine with AI produces a new layout, not an edit of the one on screen.** The
generator builds designs from a brief; nothing revises an existing canvas. So
the note is appended to your brief and the whole poster is designed again. If
the layout is right and only a detail is wrong, use **Edit in designer** — that
is the one that keeps what you have.

Nothing is saved anywhere until you press Publish. Closing the designer hands
the design back to this page; it does not write a poster, and a rename in the
editor's title field carries across to the template name.

**Duplicate** on any existing template opens a copy in this same composer, ready
to name, edit and publish. That is often the faster route to a good result: the
built-in designs already encode layouts that work, so restyling one is less
effort than describing a new one.

> **Fixed, September 2026.** Refine used to open `/canvas` — a scratch page with
> Add rect / Add circle / Add text and no way to save — and Duplicate went to the
> same place, where a copy could only be lost. Both now go through the composer.

## Writing a brief that works

Every design comes out in the **house style** — the same look as the built-in
`sys-house-*` set: medical navy, blue, white and teal, big Inter headlines
(60px and up), ticks drawn as lines, a contact card with the pharmacy's name,
phone, address and booking QR, and a footer bar. You supply the subject and the
tone; the designer writes the copy and picks one of four layouts:

| Layout | Looks like | Good for |
|---|---|---|
| `circle-portrait` | Circle photo on a teal ring, ticked list beside it | People and trust: "trusted local pharmacy" |
| `diagonal-split` | Photo cut by a teal diagonal, service chips below | Announcements and seasonal campaigns: a new service, flu season |
| `wave-footer` | Wide photo dissolving into two teal waves | Warm, short messages: "meet the team" |
| `checklist` | Tall photo panel fading into a sky page, ticked checklist | Advice: symptoms, eligibility, what to bring |

Name a layout in the brief to choose it: *"walk-in flu clinic, diagonal
split"*, *"trusted local pharmacy, circle portrait"*, *"hay fever advice,
checklist bar"*. Otherwise the designer picks.

**"Bold" is a tone, not a layout.** A brief that says "bold" gets one of the
four layouts with larger type (headline about 15% larger, list a little
larger); it never gets a different page. An older request naming `bold` as the
layout is treated the same way.

- Weak: *"a poster about flu jabs"*
- Strong: *"Walk-in flu clinic every Saturday through November, bold, aimed at
  over-65s, no appointment needed"*

Contact details are always tokens (`{{pharmacy_name}}`, `{{phone}}`,
`{{address}}`), filled from each pharmacy's own details — a template never
carries an invented pharmacy name, phone number or address, and the designer is
refused if it writes one.

## Photographs

The brief has a **Photograph** control with four modes.

**None** — a typographic, geometric design in the house style (a large plus
motif where the photo would be).

**Stock** (the default) — search real photography and click the one you want,
or pick nothing and the designer finds a photo to suit the brief. Short queries are automatically
widened with "pharmacy healthcare", because "vaccination" alone returns mostly
laboratory macro shots rather than people in clinics.

**Upload** — pick an image from your machine. It's downscaled in the browser,
uploaded immediately (so you see the thumbnail and any rejection right away),
and the poster is designed around it. PNG, JPEG or WebP, up to 12MB.

**AI** — describe the photograph and press **Preview image** to see it before a
layout is built around it (each preview is a separate image-model charge).
Shown only when an image model is configured (`REPLICATE_API_TOKEN`); otherwise
the option is hidden and the panel says so. An AI image that cannot be made is
an error — it is never quietly swapped for a stock photo.

Whatever the source, the photo is **cover-cropped to its layout's slot and
baked**: circle portraits become transparent PNGs, fades are baked into the
JPEG. Nothing is ever stretched.

### Stock vs. AI

Stock is the better default for this product, and not only because it's free.

Pharmacy imagery is the hardest category for image models — hands, faces,
medical devices and text on medicine packaging are exactly where they still
fail, and a poster with six-fingered hands or invented drug packaging is
unusable. There is also an authenticity dimension: an AI-fabricated
"pharmacist" on a poster displayed in a real pharmacy is a different
proposition from an AI image on a blog. If you go that route it's worth
checking how UK advertising standards treat it; a real photograph avoids the
question entirely.

Reach for AI when stock genuinely can't cover the scene.

Stock photos come from Pexels, using the same `PEXELS_API_KEY` the video
pipeline already uses for B-roll — no new account. The photos endpoint is
separate from the videos one, same key.

**Chosen photos are copied into your own blob storage, not hot-linked.** A
canvas stores the URL forever and the PDF exporter re-fetches it at print time,
so depending on a third-party CDN would mean every poster using that photo
breaks the day the provider moves or removes it.

The picker sends a photo **ID**, never a URL. An endpoint that downloads
whatever URL a client hands it is a server-side request forgery vector — inside
a container network that URL could name the cloud metadata endpoint or an
internal admin port. The ID is resolved through the Pexels API, so the only URL
ever fetched is one Pexels itself returned.

**Describe the image either way.** The designer never sees the picture — it only
gets your words. "Our pharmacist at the counter" produces markedly better
placement and cropping than an unlabelled upload, because it changes where the
layout is chosen and where the photo is cropped.

For generated images: environments, hands and objects work well; faces and fine
clinical detail are where image models still look wrong. Avoid naming real
people or brands.

### Why images can't be stretched

Both the canvas renderer and the PDF exporter draw an image into exactly the
width and height the design specifies — neither letterboxes nor cover-crops. So
the designer does the crop itself: every photo is cut to the exact proportions
of its slot (centred, a little high, where faces are) before it is stored, and
drawn at that size. The validator still rejects any supplied image drawn more
than 12% away from its true ratio, as a backstop.

The schema has no clipping and no gradients, so circles and fades are pixels in
the file, not shapes on the canvas: a circle photo is a transparent PNG sitting
on a teal ring (the ring stays recolourable), and a fade is baked into the JPEG
towards the exact page colour beside it. Translucent rectangles stacked to fake
a gradient are refused by the house check.

## What it will not do

The model writes copy and chooses a layout; it never draws coordinates. The
layout is drawn by the same code as the built-in house set
(`apps/ai-service/app/chains/house_style.py`, using
`app/tools/house_templates.py`), and checked the same way. A design that breaks
any of these goes back to the model as a plain-English problem ("the headline
does not fit: at most 2 lines of about 18 characters") and is rewritten, up to
three attempts:

- **Wording the template tests reject**: "your pharmacy", prices and
  percentages, offers, reviews or ratings, medicine brands, ✓/★ glyphs, invented
  phone numbers, postcodes, web addresses or pharmacy names.
- **Text that wraps differently** in the editor and the PDF, or runs into the
  next line with a long pharmacy name or address.
- **Contrast**, the logo corner left clear, exactly one booking QR, each contact
  token exactly once, and only house-palette colours.
- **A4 only**, 1240×1754.

If it fails three times it tells you the problems it could not resolve —
usually a brief asking for more content than fits.

## Pharmacies get the same tool

**Posters → Design with AI** runs the same designer for a pharmacy, with the
same four photo choices (Stock is the default, so a pharmacy that does not
upload still gets a photograph). The differences are the ones a paying
customer's path needs:

- **One generation is one AI design** against the plan's allowance (Free 3,
  Starter 50, Pro unlimited), whatever the photo. Stock and uploads add nothing;
  an AI photo is made inside the same generation — there is no separate
  "make a picture" for a pharmacy, so nothing can be charged twice.
- **Brand kit applied after generation**: name, phone and address fill the
  tokens; the logo is placed in the corner the house style keeps clear; the
  booking QR draws from the pharmacy's booking link — and draws nothing, not a
  grey box, when there is none.
- **Uploads are checked on pick** — JPEG, PNG or WebP by their bytes, up to
  12MB — so a bad file is refused before the pharmacist presses Generate.
- **Stock search is rate-limited** to 20 a minute per person: the Pexels key is
  shared by every pharmacy.
- **A photo that is not what was asked for is said out loud.** If no stock
  photo matches, stock is unavailable, or the chosen layout has no room for the
  list beside a photo, the design still arrives — with a note shown before the
  poster opens (and under the preview in the admin console), never silently.

## Two things to know

**The gallery is shared.** There is no per-pharmacy template list. Anything you
publish, everyone sees, and deleting one removes it for everyone. Only Admins
and SuperAdmins can create, edit or delete templates; every generation is
recorded in the audit log.

**Built-in templates are now editable.** The seeder used to rewrite all 24 on
every restart, which silently reverted admin edits on each deploy. It now only
inserts templates that are missing. The trade-off: changing a built-in
template's code no longer updates an environment that has already seeded it —
change it through the admin UI instead.

## The house-style set (built-in)

Twenty-two templates in one pharmacy house style — medical blue and
white, large headlines, a circle, arch or diagonal photo, a teal wave or footer
bar, plus-sign accents. Ids `sys-house-*`, A4 (1240×1754).

| Set | Templates | Category |
|---|---|---|
| First (29 Sep) | Trusted circle, Diagonal care, Team wave, Advice checklist, Book with us, Local pharmacy | General |
| Second (30 Sep) | Know your numbers (blood pressure), Diabetes risk check, Weight management | Awareness |
| | Flu vaccination, Winter vaccinations, Travel clinic | Vaccination |
| Third (1 Oct) | Shingles vaccination, Pneumonia vaccination | Vaccination |
| | Stop smoking support, Contraception advice | Awareness |
| | Pharmacy First, Prescription delivery | Promotion |
| | Antibiotics awareness, Return unused medicines (photo-free) | Safety |
| | Hay fever help | Seasonal |
| | Repeat prescriptions | General |

Every gallery category now has at least one house template. The two Safety
posters are photo-free by design — their natural photos are medicine packaging,
which carries brand names — and a test fails if any *other* template ships
without its photo. Pharmacy First lists the seven NHS conditions and says the
service is available in England; edit it out where that does not apply.

There is no Travel category, so Travel clinic is filed under Vaccination (travel
vaccines are its core). The older built-in *Travel Health Clinic* is under
Promotion.

Each carries `{{pharmacy_name}}`, `{{phone}}` and `{{address}}` once, and one
`pharmacy-booking-qr` slot. No prices or percentages, no reviews or ratings, no
medicine brands (the check also blocks weight-loss, antimalarial and vaccine
brand names). Weight management speaks about support, food and activity — it
does not mention medication, and its photo is people walking, not a tape
measure.

They are seeded **insert-only**, like every built-in template: an existing
`sys-*` template is never rewritten.

### First-time setup: build the photographs

The photos are six vetted Pexels images (Pexels License — free for commercial
use; photographers credited in `HouseTemplates/CREDITS.md`). They are fetched
and baked once, then committed, so no environment needs Pexels or the internet
to seed them. From the repository root:

```bash
docker compose run --rm --no-deps -v "$PWD:/repo" -w /repo/apps/ai-service \
  ai-service python -m app.tools.house_template_assets
```

Photos already built are skipped, so re-running after a new set is added only
fetches the new ones and leaves committed files untouched. `--force` rebuilds
everything; `--only <name> ...` builds just those.

This writes twenty photos into
`apps/api/src/Acme.Infrastructure/Persistence/HouseTemplates/assets/`. Commit
them and rebuild the API. On startup the seeder uploads each photo to template
storage (`/api/assets/templates/{guid}`) and then inserts its template.

**Until the photos are built, the six are held back**, with a log line naming
the missing file. They are never inserted with a hole where the photo should be
— insert-only seeding would never come back to fix it. If blob storage is
unreachable at startup the same happens, and they are tried again next start.

### Changing a design

The canvases are generated by `apps/ai-service/app/tools/house_templates.py`,
not hand-written: the waves are computed polygons, and every line of text is
checked before it ships. After editing it:

```bash
cd apps/ai-service
python -m app.tools.house_templates --preview /tmp/house   # writes templates.json + PNG previews
```

It refuses to write if any check fails:

- **Wrapping** — each line must fit both the PDF's rule (0.55 × font size per
  character) and the real font. Capitals are wider than the PDF's estimate and
  narrow letters narrower, so a line can wrap in one renderer and not the other;
  line breaks are written explicitly with `\n` so both agree.
- **Contrast** — 4.5:1 for normal text, 3:1 for headlines, against whatever
  shape is actually behind the text.
- **House rules** — tokens exactly once, one booking QR, no %, reviews, ratings,
  brands, or glyphs the PDF font may lack (a tick is drawn as a line).
- **The logo corner** — bottom right is left clear, because *Apply pharmacy
  info* places the pharmacy's logo there on a template with no logo slot.
- **Overlaps** — no text may run into the text below it, even with a long
  pharmacy name or address.
- **Waves over photos** — a wave laid over the foot of a photo must be backed
  by the photo everywhere, or bare page shows between them. (The travel photo's
  suitcase stands on its bottom edge, so that design ends the photo on a
  straight rule instead.)

A test fails if `templates.json` differs from what the generator produces.
Remember that a change reaches only environments that have **not** seeded that
template yet; edit an existing one through the admin UI.

### The marketing site shows these templates

The home page hero ("Trusted circle") and its six template cards (one per
category) are snapshots of house templates, so the most public page never
depends on the API and never shows a design that is not in the gallery. After
changing the house set, refresh them:

```bash
python3 scripts/marketing/build-house-previews.py
```

It writes `apps/web/src/components/landing/house-previews.generated.ts` and
resized WebP copies of the photographs under `apps/web/public/marketing/house/`
(about 100 KB in total). The booking QR and logo are left out: a visitor has
no brand kit. `house-previews.test.ts` fails if a snapshot drifts from
`templates.json`. To feature a different template, edit `HERO` / `SHOWCASE`
at the top of the script.

The public gallery (`/templates`) lists the house set first (`MongoTemplateRepository.ListAsync`),
each card drawn from the template's own canvas, never a coloured placeholder.

### Poster cards (My Posters, dashboard)

A pharmacy's own posters are shown as `GET /api/posters/{id}/card`: a 600px
JPEG from the same renderer as the PDF and PNG, so what the card shows is what
prints. It is keyed by a hash of the resolved canvas (logo, details, booking
QR, watermark), stored beside the full render, and served `no-cache` with an
ETag, so an edit or a brand-kit change shows on the next view and an unchanged
card costs a 304. Existing posters need no backfill: each card is rendered the
first time it is looked at (at most two renders at once per API instance). If
a card cannot be rendered, the page draws the in-browser preview instead of a
broken image. Bump `CardVersion` in `PosterImageService` to rebuild every card
after changing their size or encoding.

### What the renderers can do, and what that decided

| Want | Done as | Why |
|---|---|---|
| Circle-cropped photo | Transparent PNG, circle baked in; the ring is a circle shape behind it | The schema has no clipping. A JPEG circle has square corners in one background colour |
| Arch photo (rounded top) | Transparent PNG, arch baked in | Same reason |
| Photo fade | Baked into the JPEG, towards the colour beside it | No gradients in the schema; stacked rects would be a fake gradient |
| Diagonal, waves | Closed `line` shapes (filled polygons) | Recolourable. The editor now fills them like the PDF and preview always did |
| Bold headline | Size only | The schema has no font weight |
| Inter | Named in the canvas; drawn in Arial-metric fonts | The web app does not load Inter, and the PDF forces Liberation Sans |
| Tick | Line shape | Skia draws nothing for a glyph the font lacks |

Known limitation, shared with every template: a logo is stretched to its box
(`FitUnproportionally` in the PDF, and the editor), so a square logo in a 2:1
slot distorts.

> **Fixed alongside, September 2026.** The editor drew a closed line as an
> outline while the PDF and gallery filled it, so AI-designed diagonal splits
> looked different while editing. And the PDF drew a grey box for an image
> whose source was deliberately empty — the booking QR of a pharmacy with no
> booking link — instead of nothing.

## Tuning the output

The look is code, not prompt. `apps/ai-service/app/chains/house_style.py`:

| Name | What it controls |
|---|---|
| `ARCHETYPES` | The five layouts, each with its photo slot (size, circle, fades) and item limit |
| `HOUSE_COLOURS` | The palette a design may use — anything else fails the house check |
| `Plan.LIMITS` | Copy lengths per field |
| `_Composer.<layout>` | How each layout is drawn, from the house-template builders |

A new layout is a new `Archetype` plus a `_Composer` method; it is then offered
to the model automatically, and `tests/test_house_style.py` runs it through the
house checks with and without a photo.

`TEMPLATE_DESIGN_MODEL` (default `claude-sonnet-5`) sets the model. Its job is
now copy and art direction, so a smaller model works too; the stronger one
writes noticeably better headlines.

## Configuration

The API needs to reach ai-service over HTTP for this (video rendering still goes
through Mongo):

```
AiService__BaseUrl=http://ai-service:8000
AiService__TimeoutSeconds=210
```

Already set in `docker-compose.yml`. On Azure, set it on the API app service.
Without it, the Generate button returns "AI template generation is not
configured".

Stock photo search needs `PEXELS_API_KEY` on **ai-service** — already set if
video B-roll works.

AI generation additionally needs `REPLICATE_API_TOKEN` on **ai-service**.
Without it, None, Stock and Upload still work; only the AI mode reports that
image generation is not configured.

Note that ai-service runs from a built image with no source bind-mount, so
after any change to it you need `docker compose up -d --build ai-service` —
restarting alone re-runs the old code.

### The JWT secret must match on both sides

The API forwards your access token to ai-service, which verifies it
independently. That means **`JWT_SECRET` on ai-service must equal the API's
`Jwt:Secret`**, or every call fails with 401 "Token signature is not valid".

Under docker-compose both read the same repo-root `.env`, so they agree. Running
the API outside compose, it takes its secret from
`appsettings.Development.json` instead — those two are now aligned, but if you
rotate `JWT_SECRET` you must change it in both places.

Both services log a fingerprint of the secret at startup:

```
JWT config: secret_fingerprint=1523cfc5123a issuer=acme-api audience=acme-clients
```

If those two lines disagree, that is the problem. The fingerprint is a SHA-256
prefix, so it's safe in logs.

### Where images are stored

Uploaded and generated images go to blob storage under `templates/assets/` and
are referenced by a permanent relative URL, `/api/assets/templates/{guid}.{ext}`.

Relative, not absolute, because a canvas outlives the host it was authored on —
the same template renders from localhost, staging and production, and the PDF
exporter reads it server-side too. Each origin serves that path itself: Next.js
proxies it for the browser, and the exporter maps it straight to a blob key
rather than making the API issue an HTTP request to itself.

These are **not** SAS URLs, unlike brand-kit logos. A template image is baked
into a document that lives forever and is served to every pharmacy; a URL that
expired would break the design for everyone at once, weeks after anyone touched
it. Access control is the unguessable GUID, which is the right trade for
decoration on deliberately public marketing designs.

## Safety net

`apps/ai-service/app/chains/template_validator.py` holds the print rules, and
`apps/ai-service/tests/test_template_validator.py` pins them — including a test
that runs every hand-authored template through the validator. That test exists
because rules that are too strict are not harmless: each false positive costs a
repair round spent fighting the house style instead of fixing a real defect.

The contrast floor (2.2:1) is a **legibility** check, not a WCAG conformance
check. It was calibrated against the existing gallery, where a 3.0:1 floor
rejected 8 of 24 designs.

# The test environment

A full, separate copy of the platform for QA and pilot users. Same code as
production, its own database, its own storage, its own URL.

```
  production                          test
  ─────────────                       ─────────────
  pharmposter-web                     pharmtest-web
  pharmposter-api                     pharmtest-api
  pharmposter-cosmos   ← no link →    pharmtest-cosmos
  pharmposterstore                    pharmteststore
  rg-pharmacyposter                   rg-pharmacyposter-test
```

Nothing is shared. There is no path from a mistake in test to a record in
production — different Cosmos account, different storage account, different
resource group, different JWT signing secret.

---

## For QA: what you need to know

### Getting in

The URL is `https://pharmtest-web.azurewebsites.net` (or whatever
`NAME_PREFIX` you deployed with). It is a normal public site behind the normal
login — no VPN, no extra password.

Sign in with any of the demo pharmacies. They all share one password, which
your team lead will give you; it is **not** written in this file on purpose.

| Account | What it is good for |
|---|---|
| `riverside@example.test` | **Start here.** Pro plan, fully onboarded, has patients and credits. Everything is unlocked. It is also the **head office** for the three `riverside-*` branches, so Branches, branch detail and switching all have something to show. |
| `riverside-didsbury@example.test`, `riverside-salford@example.test`, `riverside-stockport@example.test` | Riverside's branches. Deliberately unalike: different plans, and Stockport has never signed in. Sign in as Riverside to see them; opening one is written to the audit log. |

| `oakfield@example.test` | Starter plan — for checking what Starter can and cannot do |
| `meadowbrook@example.test` | Free plan — the upgrade prompts and locked features |
| `harbour@example.test` | Pro with 40 credits — the highest allowance, for volume limits |
| `newbridge@example.test` | Has **not** finished onboarding — lands you in the signup flow |
| `elmtree@example.test` | **Deactivated** — should refuse to sign in |

Fifteen exist in total — twelve independents plus Riverside's three branches;
the admin console lists them all. The spread is
deliberate: different tiers, some deactivated, some mid-onboarding, some that
have never signed in, so filters and bulk actions have something real to act on.

**Trying the in-pharmacy screens.** Riverside is on Pro, so it can run them.
Publish a poster, build a playlist under **Playlists**, register a screen under
**Screens**, then open `/display` **in a private window** — a normal tab shares
your session, and the whole point of the kiosk is that it has none. Type the
six-character code. Full walkthrough in `DISPLAY_SCREENS.md`.


Two platform **Admin** accounts also exist — `nora.admin@example.test` and
`sam.admin@example.test` — on the same shared password. They are the account
managers the demo pharmacies are assigned to, and they are the right way to
test the admin console without using the SuperAdmin login.

**No parent/child pharmacy hierarchy is seeded.** If you are testing the
hierarchy or "manage as" features, you have to create the relationship yourself
first — nothing arrives pre-linked.

### The one thing that behaves differently from production

**Campaign emails really send.** This environment is wired to a live Mailjet
account, so a campaign you send from here arrives in a real inbox.

That has two consequences worth holding onto:

- The demo pharmacies use `@example.test` addresses, which do not exist. Sending
  a campaign to the seeded patients will **bounce**, and that is correct
  behaviour, not a bug. To see an email actually arrive, add your own address as
  a manual recipient on the campaign.
- Do not put a real patient's address into this system. Ever. It is a test
  environment; the data in it is not protected the way production's is.

Everything else — the poster editor, the video agent, plans, the admin console
— behaves as production does.

### What to report, and how

A bug report that is actionable has three things: what you did, what you
expected, what happened. Screenshots of the whole window beat cropped ones,
because the URL and any error banner are usually the useful part.

Worth reporting even if it feels minor:

- a page that shows a spinner and never resolves
- a button that does nothing
- a download that opens a blank tab or shows the word `null`
- an email that arrives looking broken — squashed poster, missing image,
  sideways scrolling on a phone
- anything that lets you see another pharmacy's data

That last one is the highest-priority category. If you can see a record that
does not belong to the pharmacy you signed in as, say so immediately.

### Please do not

- add real patient data, real NHS numbers, or a real person's contact details
- reuse a password here that you use anywhere else
- assume something is fixed because it works here — test and production are
  deployed separately

---

## For the team: running it

### First deploy

Two steps, and they stay separate on purpose: one provisions infrastructure,
the other ships code.

```bash
cp infra/azure/test.env.example infra/azure/test.env
# fill in the blanks — see the comments in that file

./scripts/deploy-test-env.sh            # dry run, changes nothing
./scripts/deploy-test-env.sh --apply    # creates Cosmos, storage, app services

pnpm ship:test                          # builds, pushes :test, restarts
```

Between those two commands the app services will fail to pull an image. That is
expected — they are pointed at `:test` tags on Docker Hub that do not exist
until the first `pnpm ship:test`.

The dry run prints exactly what it will create and stops. It also refuses,
before touching Azure, on the mistakes that are expensive:

- a name prefix matching production's (which would deploy over production)
- a SuperAdmin password under 16 characters (the seeder would silently refuse
  it and you would get no admin account)
- `SEND_REAL_EMAIL=true` with missing Mailjet credentials

and warns when the test sender address matches production's.

Cosmos takes about ten minutes to provision. The whole run is ~15 minutes.

### Redeploying after a code change

The infrastructure only needs creating once. After that, shipping code is:

```bash
pnpm ship:test
```

That builds all three images, pushes them to Docker Hub as `:test` **and** as
the git SHA, points the app services at `:test`, and restarts them.

| Command | Tag | Goes to |
|---|---|---|
| `pnpm ship:dev` | `:dev` | the shared dev environment |
| `pnpm ship:test` | `:test` | this environment |
| `pnpm ship:prod` | `:prod` | production — asks you to type a confirmation |

Useful flags, all passed straight through:

```bash
pnpm ship:test --pin           # pin to the immutable SHA tag instead of :test
pnpm ship:test --no-restart    # build and push only, leave Azure alone
SKIP_AI=1 pnpm ship:test       # skip the ~1.5GB ai-service build
```

Re-running `./scripts/deploy-test-env.sh --apply` is only needed when
infrastructure or app settings change — it is idempotent and safe, just slower.

### When a push fails

```
failed to copy: ... write tcp 192.168.65.3:...->192.168.65.1:3128:
use of closed network connection
```

A dropped upload, not a broken build. `192.168.65.1:3128` is Docker Desktop's
route to an HTTP proxy on your host, and proxies frequently cut long uploads —
the API layer alone is ~86MB and the ai-service image is over a gigabyte.

**Just re-run it.** Every layer that finished is already on Docker Hub and
reports `Layer already exists`, so a retry resumes rather than restarts. Pushes
now retry four times on their own with increasing backoff, so this should be
rare; `PUSH_RETRIES=8 pnpm ship:test` if your link is particularly unreliable.

If it keeps happening, check Docker Desktop → Settings → Resources → Proxies.
If you are not deliberately behind a corporate proxy, turning that off removes
the thing that is severing the connection.

`SKIP_AI=1 pnpm ship:test` skips the largest image while you are iterating on
the other two.

### Architecture

Azure App Service for Linux runs **amd64**. On an Apple Silicon Mac a plain
`docker build` produces **arm64**, which pushes perfectly and then fails to
start — the symptom is a container that never comes up, with nothing wrong in
the application logs.

The ship script warns when it detects this. To cross-build:

```bash
PLATFORM=linux/amd64 pnpm ship:test
```

The API and web images compile natively on your Mac (`--platform=$BUILDPLATFORM`
on their build stages) and only the final runtime layer is amd64, so those two
no longer run under emulation — that was what made a ship take the best part
of an hour. The ai-service image still installs its Python packages emulated;
turn on **Docker Desktop → Settings → General → "Use Rosetta for x86_64/amd64
emulation on Apple Silicon"** to make that several times faster.

If the ship stops at "Building API image" with no output at all, Docker
Desktop itself is stuck. The script now checks for that first and stops after
20 seconds with what to do (quit Docker Desktop fully and start it again).
Each build step prints how long it took, so a slow ship says which part was
slow.

### Checking it came up

```bash
az webapp log tail -g rg-pharmacyposter-test -n pharmtest-api
```

Two lines matter:

- `Seeded 12 demo pharmacy account(s) and 2 admin(s).` — working.
- `Refusing to seed demo accounts: this database already holds …` — **stop.**
  The API is pointed at a database that already contains pharmacies it did not
  create, which almost always means the connection string is production's.
  Check `Mongo__ConnectionString` on the app service before doing anything else.

That second message is a guardrail, not an error to work around. It exists
because the realistic accident with parallel environments is not someone
enabling demo data in production — it is a test app service left pointing at
the production connection string, which looks like a perfectly working
deployment right up until the seeder scatters known-password accounts across
real pharmacies.

### Resetting the data

The seeder is idempotent by email: it leaves existing accounts completely
alone, so restarting never undoes testing. To get a genuinely clean slate,
delete the Cosmos database and restart the API:

```bash
az cosmosdb mongodb database delete \
  -g rg-pharmacyposter-test -a pharmtest-cosmos -n acme --yes
az webapp restart -g rg-pharmacyposter-test -n pharmtest-api
```

It will be recreated and reseeded on boot.

### Turning it off

The App Service plan is the only meaningful cost while idle. Stopping the three
apps keeps the data and stops most of the bill:

```bash
for a in web api ai; do
  az webapp stop -g rg-pharmacyposter-test -n pharmtest-$a
done
```

To remove it entirely: `az group delete -n rg-pharmacyposter-test --yes`.

---

## How this differs from production, precisely

| | Production | Test |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Staging` |
| Demo accounts | never | 12, seeded on boot |
| Diagnostics endpoints | 404 | **404** — same as production |
| Developer exception page | off | **off** — same as production |
| Email | live | live, separate sender |
| Database | `pharmposter-cosmos` | `pharmtest-cosmos` |
| JWT secret | production's | its own |

`Staging` is deliberately not `Development`. Development would have been the
easy way to get demo data, and it also turns on the diagnostics endpoints —
including one that hands out a JWT — and replaces error pages with stack
traces. On a site anyone can reach, that is not a reasonable trade. So
`DevUserSeeder` now recognises Staging with an explicit `DevData:SeedUsers=true`,
and the flag is inert everywhere else. Production cannot seed demo accounts
even if the flag is set.

The JWT secret being different is not incidental either. A shared signing
secret means a token minted by test is accepted by production.

---

## Known sharp edges

**The demo password is shared.** Fifteen accounts, one password, on a public
URL. Set `DEMO_PASSWORD` in `test.env` to something of your own rather than
leaving the repo default, which anyone with repository access knows.

**Sender reputation is per-domain.** If test and production send from the same
domain, bounces and spam complaints from QA traffic count against production's
deliverability. Test sends to `@example.test` addresses bounce by design, so
this is not hypothetical — use a distinct subdomain such as
`no-reply@test.sonarinformatics.com`.

**Nothing stops a real address being entered.** The decision was real,
unrestricted sending, which means the only thing between this environment and
an email to a real patient is the person typing. If that stops feeling
comfortable — particularly if anyone ever imports a patient list here — the
fix is a send-time allowlist in `CampaignService`, and it is perhaps an hour of
work.

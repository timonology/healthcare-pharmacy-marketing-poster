# End-to-end smoke test

```bash
./scripts/e2e/run.sh
```

Runs against a **live stack** and walks the paths a real user takes, in order.
Every check exists because that thing broke at least once and reached manual
testing.

## Running it

Start everything first:

```bash
docker compose up -d --build  # mongo, azurite, ai-service, web
# --build matters for ai-service and web: both bake their source into the
# image, so without it the suite can pass or fail against code you are not
# looking at.
cd apps/api/src/Acme.WebApi && dotnet run
```

Then:

| | |
|---|---|
| `./scripts/e2e/run.sh` | everything |
| `./scripts/e2e/run.sh --no-ai` | skip the paid generation steps |
| `./scripts/e2e/run.sh --keep` | leave the template and poster it creates |

Exit code is 0 when everything passed, 1 otherwise — so it drops straight into
a pre-commit hook or CI.

Overridable via environment: `API_URL`, `AI_URL`, `WEB_URL`,
`SUPERADMIN_EMAIL`, `SUPERADMIN_PASSWORD`.

## What it checks, and what each one caught

| Check | The failure it catches |
|---|---|
| All three services answer | ai-service dying on an import error looks identical to "not started" |
| JWT fingerprints match | API and ai-service signing with different secrets — every call 401s, and it reads like a login bug |
| ai-service routes exist | the router is mounted under `/api`, so `/canvas/…` silently 404s |
| Account counts reconcile | `pharmacies + admins + superadmins == total` |
| Role filter agrees with stats | documents written before the `role` field existed are invisible to an equality filter |
| Template writes reject anonymous callers | these shipped ungated — any signed-in pharmacy could rewrite the shared gallery |
| **Patient contact details are masked on the wire** | a support endpoint returning real addresses. The UI would render them under a column headed "masked" and look entirely correct |
| Reveal refuses an empty reason | the audit trail quietly becoming "someone looked, at some point, for no stated reason" |
| Support routes reject anonymous callers | a new controller that forgot the admin-area policy |
| Built-in templates seeded | an empty gallery after a volume reset |
| Image is produced and served back | Pexels/DNS reachability, blob upload, and the asset route — three things that failed independently |
| **Generated design uses the supplied photograph** | a canvas that quietly omits the image. Renders "successfully" with no picture |
| **All template images resolve for print** | an asset URL that doesn't map back to a blob key |
| **PDF contains an embedded image** | the renderer skips images it can't fetch, so a broken poster still emails |
| Web proxies GET and exposes POST | a Next.js route file missing a method returns 405 and never reaches the API |

The three in bold are the ones that would have caught the missing-image bug —
at three different layers, so whichever one breaks, the message points at it.

## Costs

The generation steps make one Pexels call (free) and one Claude call (a few
pence). `--no-ai` skips both and still runs everything else, falling back to a
seeded photographic template for the PDF checks.

## Reading a failure

Failures are collected and printed together at the end rather than aborting on
the first — the second failure is often the one that explains the first.

```
  FAIL  JWT secrets match
        api=6b964a3dd5ed ai-service=1523cfc5123a — every cross-service call will 401
```

Each message names the observed values and the consequence, so it should be
actionable without opening the code.

## What it does not cover

Honest limits — it is a smoke test, not a full suite:

- **No browser.** Rendering, the designer canvas, drag-and-drop and dark mode
  are untested. It exercises the API and BFF layers only.
- **Campaign sending is not triggered**, to avoid emailing on every run. It
  exports the same PDF a campaign attaches and asserts the image is in it,
  which is where that bug actually lived.
- **Stripe, videos and patient import** are untouched.
- **One design per run.** Generation is non-deterministic; a pass means the
  pipeline works, not that every future design will.

## Companion suites

The offline suites — no live stack needed. `pnpm` resolves the root
`package.json` from any subdirectory, so these work wherever you are:

```bash
pnpm test                           # all of them
pnpm test:api                       # just .NET
```

From the repo root, `./scripts/test.sh [api|ai|web]` does the same thing.

That covers the .NET tests (domain rules, URL resolution including the Unix
`file://` regression, and patient-contact masking), the ai-service print rules,
and TypeScript types across web + shared-types.

**Use the script rather than running the tools by hand.** `dotnet test` inside
`apps/api/src/Acme.WebApi` resolves to `Acme.WebApi.csproj`, which has no test
SDK — so it prints a restore line, runs nothing, and exits 0. It is
indistinguishable from a clean pass. The script always targets `Acme.sln`
explicitly and reports a skipped suite as SKIP rather than letting it look
green.

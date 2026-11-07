#!/usr/bin/env bash
#
# Runs every test suite in the repo, from wherever you happen to be standing.
#
# This exists because of a genuinely nasty trap: `dotnet test` inside
# apps/api/src/Acme.WebApi finds Acme.WebApi.csproj, which has no test SDK,
# does nothing, and exits 0. No output, no error, no tests. It looks exactly
# like a clean run.
#
# So: never `cd` somewhere and run a bare test command. Run this instead.
#
#   ./scripts/test.sh              # everything   (from the repo root)
#   ./scripts/test.sh api          # .NET only
#   ./scripts/test.sh ai           # Python only
#   ./scripts/test.sh web          # TypeScript only
#
# The script itself does not care what the working directory is — it resolves
# the repo root from its own path. But `./scripts/test.sh` is a relative path,
# so *typing* it only works from the root. From anywhere else use pnpm, which
# walks up to the root package.json for you:
#
#   pnpm test                      # any directory in the repo
#   pnpm test:api                  # or test:ai / test:web
#
# Exit code is 0 only if every suite that ran passed. A suite whose toolchain
# is missing is reported as SKIPPED and does not mask a failure elsewhere —
# but it is also never silently counted as a pass.
#
set -uo pipefail

# Repo root from this script's own location, so the working directory is
# irrelevant. This is the whole point of the file.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ -t 1 ]; then
  R=$'\e[31m'; G=$'\e[32m'; Y=$'\e[33m'; B=$'\e[1m'; D=$'\e[2m'; N=$'\e[0m'
else
  R=; G=; Y=; B=; D=; N=
fi

WHICH="${1:-all}"
case "$WHICH" in
  all|api|api:integration|ai|web) ;;
  -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
  *) echo "unknown suite: $WHICH (expected api, api:integration, ai, web, or nothing)" >&2; exit 2 ;;
esac

declare -a RESULTS=()
FAILED=0

banner() { printf '\n%s── %s ─────────────────────────────%s\n' "$B" "$1" "$N"; }
record_pass() { RESULTS+=("${G}PASS${N}  $1"); }
record_fail() { RESULTS+=("${R}FAIL${N}  $1"); FAILED=$((FAILED+1)); }
record_skip() { RESULTS+=("${Y}SKIP${N}  $1 ${D}($2)${N}"); }

# Runs one suite. Set SUITE_HINT beforehand to attach a "try this" line that is
# printed only on failure — a non-zero exit here is often a missing toolchain
# rather than a broken test, and the difference is not obvious from the raw
# output of whatever tool just gave up.
run_suite() {
  local label="$1" dir="$2"; shift 2
  banner "$label"
  ( cd "$dir" && "$@" )
  local code=$?
  if [ "$code" -eq 0 ]; then
    record_pass "$label"
  else
    record_fail "$label (exit $code)"
    [ -n "${SUITE_HINT:-}" ] && RESULTS+=("      ${D}${SUITE_HINT}${N}")
  fi
  SUITE_HINT=""
}

# ── .NET ────────────────────────────────────────────────────────────────
if [ "$WHICH" = all ] || [ "$WHICH" = api ]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    record_skip ".NET tests" "dotnet not on PATH"
  else
    # The unit project explicitly, NOT the solution.
    #
    # The solution now also contains Acme.IntegrationTests, which boots the web
    # host and needs a MongoDB. Running the solution here would make the fast
    # everyday suite depend on a database being up — and a suite that fails for
    # reasons unrelated to the code is a suite people stop believing.
    #
    # Naming the project directly still avoids the original trap: `dotnet test`
    # pointed at a directory resolves to whichever single project is nearby,
    # which is how it once ran nothing at all and exited 0.
    run_suite ".NET unit tests" "$ROOT/apps/api" \
      dotnet test tests/Acme.Tests/Acme.Tests.csproj --nologo --verbosity minimal
  fi
fi

# ── .NET integration (needs MongoDB, opt-in) ────────────────────────────
if [ "$WHICH" = api:integration ]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    record_skip ".NET integration tests" "dotnet not on PATH"
  else
    printf '  %sMongoDB:%s %s\n' "$D" "$N" "${MONGO_TEST_URL:-mongodb://localhost:27017}"

    # Interrupting a test run leaves the build servers alive — MSBuild worker
    # nodes and a Roslyn compiler server, which persist by design so the next
    # build is fast. They keep a handle on
    # tests/Acme.IntegrationTests/bin/.../Acme.IntegrationTests.dll, and the
    # next build then blocks writing it.
    #
    # It looks nothing like a lock. The four src projects compile and print
    # normally, the line for the test project never appears, and there is no
    # error — so the obvious reading is "a test is hanging", which sends you
    # looking at MongoDB. Shutting the servers down first costs a second or two
    # and removes the failure mode.
    #
    # `|| true` because this is housekeeping: a machine with no servers running
    # returns non-zero and that is not a reason to refuse to run the tests.
    dotnet build-server shutdown >/dev/null 2>&1 || true

    SUITE_HINT="if the build stopped after Acme.WebApi and printed nothing else, it is a stale file lock, not a test: dotnet build-server shutdown && pkill -f testhost; then re-run"
    run_suite ".NET integration tests" "$ROOT/apps/api" \
      dotnet test tests/Acme.IntegrationTests/Acme.IntegrationTests.csproj \
        --nologo --verbosity minimal
  fi
fi

# ── Python ──────────────────────────────────────────────────────────────
if [ "$WHICH" = all ] || [ "$WHICH" = ai ]; then
  AI="$ROOT/apps/ai-service"
  # Preference order matches how the project is actually set up: the existing
  # .venv first, then uv (which owns uv.lock), then a global pytest.
  #
  # The uv arm needs `--extra dev`. pytest lives in
  # [project.optional-dependencies].dev, which uv does NOT install by default —
  # a bare `uv run pytest` finds an environment without it and dies with
  # "Failed to spawn: pytest", which reads like a broken test suite rather than
  # a missing dependency. With the flag, uv resolves and installs the extra
  # before running.
  if [ ! -d "$AI" ]; then
    record_skip "ai-service tests" "apps/ai-service not found"

  elif [ -x "$AI/.venv/bin/pytest" ]; then
    run_suite "ai-service tests" "$AI" ./.venv/bin/pytest -q

  elif command -v uv >/dev/null 2>&1; then
    # Probe before running, and sync only if the probe fails.
    #
    # Retrying on *any* non-zero exit would mean a genuine test failure
    # triggers a pointless re-sync and prints the whole failing run twice. A
    # cheap import check separates "not installed yet" from "tests are red",
    # which are the two things this suite must never confuse.
    if ! ( cd "$AI" && uv run --extra dev python -c "import pytest" ) >/dev/null 2>&1; then
      banner "ai-service setup"
      printf '  %sinstalling the dev extra (pytest) — first run only%s\n' "$D" "$N"
      if ! ( cd "$AI" && uv sync --extra dev ); then
        record_skip "ai-service tests" \
          "uv sync --extra dev failed — check network, then retry"
        AI_SETUP_FAILED=1
      fi
    fi

    if [ -z "${AI_SETUP_FAILED:-}" ]; then
      SUITE_HINT="run it directly for detail: cd apps/ai-service && uv run --extra dev pytest"
      run_suite "ai-service tests" "$AI" uv run --extra dev pytest -q
    fi

  elif command -v pytest >/dev/null 2>&1; then
    run_suite "ai-service tests" "$AI" pytest -q

  else
    record_skip "ai-service tests" \
      "no pytest and no uv — install uv, then: cd apps/ai-service && uv sync --extra dev"
  fi
fi

# ── TypeScript ──────────────────────────────────────────────────────────
if [ "$WHICH" = all ] || [ "$WHICH" = web ]; then
  WEB="$ROOT/apps/web"
  if [ -x "$WEB/node_modules/.bin/tsc" ]; then
    run_suite "web typecheck" "$WEB" ./node_modules/.bin/tsc --noEmit
  else
    record_skip "web typecheck" "node_modules missing — run pnpm install"
  fi

  # Dead internal links render Next's 404 entirely on the client — no server
  # request, nothing in any log, just a button that goes nowhere. Typechecking
  # cannot catch it because a route is a string.
  if command -v node >/dev/null 2>&1; then
    run_suite "web route links" "$ROOT" node scripts/check-routes.mjs
    run_suite "client/api contract" "$ROOT" node scripts/check-contract.mjs
    run_suite "web bff proxies" "$ROOT" node scripts/check-proxies.mjs
    run_suite "e2e helper usage" "$ROOT" node scripts/check-e2e.mjs
    run_suite "dead ui controls" "$ROOT" node scripts/check-dead-ui.mjs
    run_suite "brand naming" "$ROOT" node scripts/check-brand.mjs
    run_suite "theme colours" "$ROOT" node scripts/check-theme.mjs
    run_suite "app shell + password fields" "$ROOT" node scripts/check-shell.mjs
    # A mutating endpoint with no [Audit]/[NoAudit] would go unrecorded silently.
    run_suite "api audit coverage" "$ROOT" node scripts/check-audit.mjs
    # Duplicated copies of Radix's document-scoped modules. The symptom is a
    # page that renders perfectly and cannot be clicked, so it is reported as
    # "it froze" rather than as anything a stack trace would show.
    run_suite "radix layer singletons" "$ROOT" node scripts/check-radix-layers.mjs
    run_suite "react versions" "$ROOT" node scripts/check-react-versions.mjs
    run_suite "photo framing geometry" "$ROOT" node scripts/check-framing.mjs
    run_suite "e2e self-test" "$ROOT" ./scripts/e2e/selftest.sh
  else
    record_skip "web route links" "node not on PATH"
  fi

  # Only start vitest when there is something for it to run. Booting it
  # against an empty suite costs several seconds and, on some platforms, fails
  # on a rollup native binary — a red result that means nothing about this
  # codebase is worse than an honest skip.
  web_tests="$(find "$WEB/src" \
    \( -name '*.test.ts' -o -name '*.test.tsx' \
       -o -name '*.spec.ts' -o -name '*.spec.tsx' \) \
    -print -quit 2>/dev/null)"

  if [ -z "$web_tests" ]; then
    record_skip "web unit tests" "no test files under apps/web/src yet"
  elif [ -x "$WEB/node_modules/.bin/vitest" ]; then
    run_suite "web unit tests" "$WEB" ./node_modules/.bin/vitest run
  else
    record_skip "web unit tests" "vitest not installed — run pnpm install"
  fi
fi

# ── summary ─────────────────────────────────────────────────────────────
printf '\n%s─────────────────────────────────────────%s\n' "$B" "$N"
for line in "${RESULTS[@]}"; do printf '  %b\n' "$line"; done
printf '\n'

if [ "$FAILED" -gt 0 ]; then
  printf '%s%d suite(s) failed.%s\n' "$R" "$FAILED" "$N"
  exit 1
fi

printf '%sAll suites that ran passed.%s\n' "$G" "$N"
printf '%sThe end-to-end smoke test is separate and needs a live stack: ./scripts/e2e/run.sh%s\n' "$D" "$N"

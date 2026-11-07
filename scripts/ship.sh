#!/usr/bin/env bash
# ------------------------------------------------------------------
# Build, push and deploy one environment.
#
#   pnpm ship:dev     →  :dev   tag  →  the shared dev environment
#   pnpm ship:test    →  :test  tag  →  the QA environment
#   pnpm ship:prod    →  :prod  tag  →  production
#
# Each one builds all three images, pushes them to Docker Hub under both
# the environment tag and the git SHA, points that environment's App
# Services at the tag, and restarts them. One command, deployed.
#
# Usage:
#   ./scripts/ship.sh test               # full deploy
#   ./scripts/ship.sh prod --yes         # skip the production confirmation
#   ./scripts/ship.sh test --pin         # pin to the immutable SHA tag
#   ./scripts/ship.sh test --no-restart  # build and push only
#   SKIP_AI=1 ./scripts/ship.sh test     # skip the slow ai-service build
#
# Targets are resolved from infra/azure/ship-targets.env when it exists,
# falling back to the defaults below. Everything is a thin wrapper around
# scripts/deploy.sh, which does the actual work.
# ------------------------------------------------------------------
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
warn() { printf '\033[33m  ! %s\033[0m\n' "$1"; }
die()  { printf '\033[31mERROR: %s\033[0m\n' "$1" >&2; exit 1; }

ENVIRONMENT="${1:-}"
shift || true

case "$ENVIRONMENT" in
  dev|test|prod) ;;
  ""|-h|--help)
    grep '^#' "$0" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  *) die "Unknown environment '$ENVIRONMENT'. Expected dev, test or prod." ;;
esac

# ── defaults ────────────────────────────────────────────────────────────
#
# Production is what `pnpm ship` has always targeted. Test follows the
# prefix in infra/azure/test.env. Dev has no defaults on purpose — it is
# a real deployed environment and guessing its names could restart the
# wrong app.

PROD_RG="${PROD_RG:-rg-sonar-pharmacyposter}"
PROD_API_APP="${PROD_API_APP:-sonarmarketingbackend}"
PROD_WEB_APP="${PROD_WEB_APP:-sonar-marketing-auth}"
PROD_AI_APP="${PROD_AI_APP:-sonar-marketing-ai}"

TEST_RG="${TEST_RG:-rg-pharmacyposter-test}"
TEST_API_APP="${TEST_API_APP:-pharmtest-api}"
TEST_WEB_APP="${TEST_WEB_APP:-pharmtest-web}"
TEST_AI_APP="${TEST_AI_APP:-pharmtest-ai}"

DEV_RG="${DEV_RG:-}"
DEV_API_APP="${DEV_API_APP:-}"
DEV_WEB_APP="${DEV_WEB_APP:-}"
DEV_AI_APP="${DEV_AI_APP:-}"

TARGETS_FILE="${TARGETS_FILE:-$ROOT/infra/azure/ship-targets.env}"
if [[ -f "$TARGETS_FILE" ]]; then
  # shellcheck disable=SC1090
  set -a; source "$TARGETS_FILE"; set +a
fi

# The test environment's app names follow NAME_PREFIX, so read it from the
# same file the provisioning script uses rather than keeping two copies.
TEST_ENV_FILE="$ROOT/infra/azure/test.env"
if [[ "$ENVIRONMENT" == "test" && -f "$TEST_ENV_FILE" ]]; then
  PREFIX="$(grep -E '^NAME_PREFIX=' "$TEST_ENV_FILE" | tail -1 | cut -d= -f2- | tr -d '"'"'"' ')"
  RG_FROM_FILE="$(grep -E '^RESOURCE_GROUP=' "$TEST_ENV_FILE" | tail -1 | cut -d= -f2- | tr -d '"'"'"' ')"
  [[ -n "$PREFIX" ]] && {
    TEST_API_APP="${PREFIX}-api"
    TEST_WEB_APP="${PREFIX}-web"
    TEST_AI_APP="${PREFIX}-ai"
  }
  [[ -n "$RG_FROM_FILE" ]] && TEST_RG="$RG_FROM_FILE"
fi

case "$ENVIRONMENT" in
  prod) RG="$PROD_RG"; API_APP="$PROD_API_APP"; WEB_APP="$PROD_WEB_APP"; AI_APP="$PROD_AI_APP" ;;
  test) RG="$TEST_RG"; API_APP="$TEST_API_APP"; WEB_APP="$TEST_WEB_APP"; AI_APP="$TEST_AI_APP" ;;
  dev)  RG="$DEV_RG";  API_APP="$DEV_API_APP";  WEB_APP="$DEV_WEB_APP";  AI_APP="$DEV_AI_APP" ;;
esac

# ── args ────────────────────────────────────────────────────────────────
DO_RESTART=1
ASSUME_YES="${CONFIRM:-0}"
PASSTHROUGH=()

for arg in "$@"; do
  case "$arg" in
    --no-restart) DO_RESTART=0 ;;
    --yes|-y)     ASSUME_YES=1 ;;
    --pin)        PASSTHROUGH+=("--pin") ;;
    --bump)       PASSTHROUGH+=("--bump") ;;
    *)            PASSTHROUGH+=("$arg") ;;
  esac
done

if [[ "$DO_RESTART" -eq 1 ]] && [[ ! " ${PASSTHROUGH[*]:-} " =~ (--pin|--bump) ]]; then
  PASSTHROUGH+=("--restart")
fi

# ── refuse the mistakes that matter ─────────────────────────────────────

if [[ "$DO_RESTART" -eq 1 && -z "$RG" ]]; then
  die "No Azure target configured for '$ENVIRONMENT'.

       Create $TARGETS_FILE with:
         ${ENVIRONMENT^^}_RG=<resource group>
         ${ENVIRONMENT^^}_API_APP=<api app service name>
         ${ENVIRONMENT^^}_WEB_APP=<web app service name>
         ${ENVIRONMENT^^}_AI_APP=<ai app service name>

       Or run with --no-restart to build and push the images only."
fi

# The whole point of separate tags is separate environments. If two of them
# resolve to the same app service, the tags are decorative.
if [[ "$ENVIRONMENT" != "prod" && "$RG" == "$PROD_RG" && -n "$RG" ]]; then
  die "'$ENVIRONMENT' resolves to the production resource group ($RG).
       That would deploy $ENVIRONMENT images onto production. Fix the target
       in $TARGETS_FILE."
fi

if [[ "$ENVIRONMENT" != "prod" && "$API_APP" == "$PROD_API_APP" && -n "$API_APP" ]]; then
  die "'$ENVIRONMENT' resolves to the production API app ($API_APP).
       Fix the target in $TARGETS_FILE."
fi

command -v docker >/dev/null || die "Docker not found."

# ── plan ────────────────────────────────────────────────────────────────

bold "Shipping to ${ENVIRONMENT}"
cat <<SUMMARY

  tag              :${ENVIRONMENT}   (plus the git SHA)
  images           infosonarinformatics/sonarmarketingbackend:${ENVIRONMENT}
                   infosonarinformatics/sonar-marketing-auth:${ENVIRONMENT}
                   infosonarinformatics/sonar-marketing-ai:${ENVIRONMENT}
SUMMARY

if [[ "$DO_RESTART" -eq 1 ]]; then
  cat <<SUMMARY
  resource group   ${RG}
  app services     ${API_APP}, ${WEB_APP}, ${AI_APP}
  after push       repoint at :${ENVIRONMENT}, then restart

SUMMARY
else
  printf '  after push       nothing (--no-restart)\n\n'
fi

if [[ "$ENVIRONMENT" == "prod" && "$ASSUME_YES" != "1" ]]; then
  warn "This deploys to PRODUCTION and restarts it. Users will see a brief blip."
  read -r -p "  Type 'deploy production' to continue: " reply
  [[ "$reply" == "deploy production" ]] || die "Cancelled — nothing was built or pushed."
  echo
fi

# ── go ──────────────────────────────────────────────────────────────────
#
# deploy.sh does the building, pushing and Azure calls. Everything below
# is configuration handed to it.

export MOVING_TAG="$ENVIRONMENT"
export RG API_APP WEB_APP AI_APP

exec bash "$ROOT/scripts/deploy.sh" ${PASSTHROUGH[@]+"${PASSTHROUGH[@]}"}

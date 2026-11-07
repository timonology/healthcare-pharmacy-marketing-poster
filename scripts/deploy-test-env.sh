#!/usr/bin/env bash
#
# Provision and deploy the QA/test environment.
#
# The same Bicep template production uses, with a different name prefix, into a
# different resource group. Nothing is shared: separate Cosmos account,
# separate storage account, separate app services.
#
#   ./scripts/deploy-test-env.sh              # plan only, changes nothing
#   ./scripts/deploy-test-env.sh --apply      # actually deploy
#
# Configuration lives in infra/azure/test.env — copy test.env.example and fill
# it in. Secrets are read from there rather than passed on the command line, so
# they do not end up in your shell history.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${ENV_FILE:-$ROOT/infra/azure/test.env}"
APPLY=false
[[ "${1:-}" == "--apply" ]] && APPLY=true

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
warn() { printf '\033[33m  ! %s\033[0m\n' "$1"; }
die()  { printf '\033[31mERROR: %s\033[0m\n' "$1" >&2; exit 1; }

# ── configuration ───────────────────────────────────────────────────────

[[ -f "$ENV_FILE" ]] || die "No config at $ENV_FILE
       Copy infra/azure/test.env.example to test.env and fill it in."

# shellcheck disable=SC1090
set -a; source "$ENV_FILE"; set +a

: "${RESOURCE_GROUP:?set RESOURCE_GROUP in $ENV_FILE}"
: "${LOCATION:?set LOCATION in $ENV_FILE}"
: "${NAME_PREFIX:?set NAME_PREFIX in $ENV_FILE}"
: "${JWT_SECRET:?set JWT_SECRET in $ENV_FILE (openssl rand -base64 48)}"
: "${SUPERADMIN_PASSWORD:?set SUPERADMIN_PASSWORD in $ENV_FILE (16+ chars)}"

SEED_DEMO="${SEED_DEMO:-true}"
SEND_REAL_EMAIL="${SEND_REAL_EMAIL:-false}"
DEMO_PASSWORD="${DEMO_PASSWORD:-}"
MAILJET_API_KEY="${MAILJET_API_KEY:-}"
MAILJET_SECRET_KEY="${MAILJET_SECRET_KEY:-}"
MAIL_FROM_ADDRESS="${MAIL_FROM_ADDRESS:-}"
MAIL_FROM_NAME="${MAIL_FROM_NAME:-Sonar Marketing (TEST)}"
APP_SERVICE_SKU="${APP_SERVICE_SKU:-B1}"

# Docker Hub namespace the :test images live under. Must match scripts/ship.sh,
# which is what actually builds and pushes them.
REGISTRY="${REGISTRY:-infosonarinformatics}"

# ── refuse the mistakes that matter ─────────────────────────────────────

# The prefix is what keeps every resource name distinct. Reusing production's
# would target production's resources with this template.
[[ -n "${PROD_NAME_PREFIX:-}" && "$NAME_PREFIX" == "$PROD_NAME_PREFIX" ]] &&
  die "NAME_PREFIX is the same as PROD_NAME_PREFIX ('$NAME_PREFIX').
       This would deploy over production. Pick a different prefix."

[[ ${#NAME_PREFIX} -le 11 ]] ||
  die "NAME_PREFIX must be 11 characters or fewer (Bicep enforces this)."

[[ "$NAME_PREFIX" =~ ^[a-z0-9]+$ ]] ||
  die "NAME_PREFIX must be lowercase letters and digits only."

[[ ${#SUPERADMIN_PASSWORD} -ge 16 ]] ||
  die "SUPERADMIN_PASSWORD must be at least 16 characters — SuperAdminSeeder
       refuses anything shorter outside Development and you would get no
       admin account at all."

if [[ "$SEND_REAL_EMAIL" == "true" ]]; then
  [[ -n "$MAILJET_API_KEY" && -n "$MAILJET_SECRET_KEY" && -n "$MAIL_FROM_ADDRESS" ]] ||
    die "SEND_REAL_EMAIL=true needs MAILJET_API_KEY, MAILJET_SECRET_KEY and
       MAIL_FROM_ADDRESS."

  # Sender reputation is per-domain. Test bounces and spam complaints land on
  # whatever domain the mail claims to come from, so sharing production's
  # sender means QA traffic degrades real pharmacies' deliverability.
  if [[ -n "${PROD_MAIL_FROM_ADDRESS:-}" && "$MAIL_FROM_ADDRESS" == "$PROD_MAIL_FROM_ADDRESS" ]]; then
    warn "MAIL_FROM_ADDRESS is identical to production's."
    warn "Test bounces will count against production's sender reputation."
    warn "Prefer a subdomain, e.g. no-reply@test.yourdomain.com."
  fi
fi

if [[ "$SEED_DEMO" == "true" && "$SEND_REAL_EMAIL" == "true" ]]; then
  warn "Demo accounts and real email are both on."
  warn "Demo pharmacies use @example.test addresses, which cannot receive mail —"
  warn "so campaigns to them will bounce. That is expected; add a real address"
  warn "to a campaign when you want to see one arrive."
fi

command -v az >/dev/null || die "Azure CLI not found. See AZURE.md step 0."
az account show >/dev/null 2>&1 || die "Not signed in. Run: az login"

SUBSCRIPTION="$(az account show --query name -o tsv)"

# ── plan ────────────────────────────────────────────────────────────────

bold "Test environment deployment"
cat <<SUMMARY

  subscription     $SUBSCRIPTION
  resource group   $RESOURCE_GROUP  (created if missing)
  location         $LOCATION
  name prefix      $NAME_PREFIX
  app service SKU  $APP_SERVICE_SKU

  environment      Staging   (diagnostics endpoints stay 404)
  demo accounts    $SEED_DEMO
  demo password    $([[ -n "$DEMO_PASSWORD" ]] && echo 'custom' || echo 'repo default — public knowledge')
  real email       $SEND_REAL_EMAIL$([[ "$SEND_REAL_EMAIL" == "true" ]] && echo "  from $MAIL_FROM_ADDRESS")

  will create      ${NAME_PREFIX}-web, ${NAME_PREFIX}-api, ${NAME_PREFIX}-ai
                   ${NAME_PREFIX}-cosmos, ${NAME_PREFIX}store, ${NAME_PREFIX}acr

SUMMARY

if [[ "$APPLY" != "true" ]]; then
  bold "Dry run — nothing was changed."
  echo "Re-run with --apply to deploy."
  exit 0
fi

# ── apply ───────────────────────────────────────────────────────────────

bold "Creating resource group…"
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none

bold "Deploying infrastructure (this takes ~10 minutes for Cosmos)…"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$ROOT/infra/azure/main.bicep" \
  --parameters \
      namePrefix="$NAME_PREFIX" \
      location="$LOCATION" \
      appServiceSku="$APP_SERVICE_SKU" \
      aspNetEnvironment=Staging \
      seedDemoAccounts="$SEED_DEMO" \
      demoAccountPassword="$DEMO_PASSWORD" \
      superAdminEmail="${SUPERADMIN_EMAIL:-superadmin@sonarinformatics.com}" \
      superAdminPassword="$SUPERADMIN_PASSWORD" \
      sendRealEmail="$SEND_REAL_EMAIL" \
      mailjetApiKey="$MAILJET_API_KEY" \
      mailjetSecretKey="$MAILJET_SECRET_KEY" \
      mailFromAddress="$MAIL_FROM_ADDRESS" \
      mailFromName="$MAIL_FROM_NAME" \
      jwtSecret="$JWT_SECRET" \
      clientIpSigningKey="${CLIENT_IP_SIGNING_KEY:-}" \
      linkSigningSecret="${LINK_SIGNING_SECRET:-}" \
      apiImage="$REGISTRY/sonarmarketingbackend:test" \
      webImage="$REGISTRY/sonar-marketing-auth:test" \
      aiImage="$REGISTRY/sonar-marketing-ai:test" \
  --output none

WEB="$(az deployment group show --resource-group "$RESOURCE_GROUP" \
        --name main --query properties.outputs.webHostname.value -o tsv)"

bold "Done — infrastructure is up."
cat <<DONE

  Test environment:  https://$WEB

  This script provisions infrastructure only. The app services are pointed at
  the :test images on Docker Hub, which do not exist until you push them:

      pnpm ship:test

  That builds all three images, pushes them as :test, and restarts the apps.
  Until then the app services will fail to pull, which is expected.

  Once shipped, give it two or three minutes and check:
    az webapp log tail -g $RESOURCE_GROUP -n ${NAME_PREFIX}-api

  Look for either
    "Seeded 12 demo pharmacy account(s)"           — working
    "Refusing to seed demo accounts"               — pointed at the wrong database

  QA guide: docs/TEST-ENVIRONMENT.md
DONE

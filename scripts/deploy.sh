#!/usr/bin/env bash
# ------------------------------------------------------------------
# One-shot deploy for the Pharmacy Poster monorepo.
#
# Builds the API + web Docker images, pushes both to Docker Hub, and
# (optionally) tells Azure App Service to pull the new images.
#
# Image tags pushed each run:
#   :dev               — moving tag (matches what App Service is watching)
#   :<git-short-sha>   — immutable tag so you can roll back instantly
#
# Usage:
#   ./scripts/deploy.sh                  # build + push only
#   ./scripts/deploy.sh --restart        # also restart the App Services
#   ./scripts/deploy.sh --pin            # also pin the App Services to the SHA tag
#   RG=my-rg API_APP=my-api WEB_APP=my-web ./scripts/deploy.sh --pin
# ------------------------------------------------------------------
set -euo pipefail

# --- Config (override via env vars) ------------------------------------------
REGISTRY="${REGISTRY:-infosonarinformatics}"
API_IMAGE="${API_IMAGE:-sonarmarketingbackend}"
WEB_IMAGE="${WEB_IMAGE:-sonar-marketing-auth}"
MOVING_TAG="${MOVING_TAG:-dev}"

# Azure resources — only needed for --restart / --pin
RG="${RG:-rg-sonar-pharmacyposter}"
API_APP="${API_APP:-sonarmarketingbackend}"
WEB_APP="${WEB_APP:-sonar-marketing-auth}"

# Derived
SHA_TAG="$(git -C "$(dirname "$0")/.." rev-parse --short HEAD 2>/dev/null || echo "manual-$(date +%Y%m%d-%H%M%S)")"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# --- Args --------------------------------------------------------------------
DO_RESTART=0
DO_PIN=0
BUMP_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --restart) DO_RESTART=1 ;;
    --pin)     DO_PIN=1 ; DO_RESTART=1 ;;
    # --bump: only restart (no container-set). Use when your Azure role can
    # restart apps but not update container config. App Service will re-pull
    # the moving tag ($MOVING_TAG) on the restart.
    --bump)    DO_RESTART=1 ; BUMP_ONLY=1 ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) echo "Unknown arg: $arg" >&2 ; exit 1 ;;
  esac
done

echo "==> Repo:     $REPO_ROOT"
echo "==> Registry: docker.io/$REGISTRY"
echo "==> API:      $API_IMAGE  →  tags: $MOVING_TAG, $SHA_TAG"
echo "==> Web:      $WEB_IMAGE  →  tags: $MOVING_TAG, $SHA_TAG"
echo

cd "$REPO_ROOT"

# --- 1. Build ----------------------------------------------------------------
echo "==> Building API image"
docker build \
  -f apps/api/Dockerfile \
  -t "$REGISTRY/$API_IMAGE:$MOVING_TAG" \
  -t "$REGISTRY/$API_IMAGE:$SHA_TAG" \
  ./apps/api

echo
echo "==> Building Web image"
docker build \
  -f apps/web/Dockerfile \
  -t "$REGISTRY/$WEB_IMAGE:$MOVING_TAG" \
  -t "$REGISTRY/$WEB_IMAGE:$SHA_TAG" \
  .

# --- 2. Push -----------------------------------------------------------------
echo
echo "==> Pushing API image"
docker push "$REGISTRY/$API_IMAGE:$MOVING_TAG"
docker push "$REGISTRY/$API_IMAGE:$SHA_TAG"

echo
echo "==> Pushing Web image"
docker push "$REGISTRY/$WEB_IMAGE:$MOVING_TAG"
docker push "$REGISTRY/$WEB_IMAGE:$SHA_TAG"

# --- 3. (Optional) Update App Service ----------------------------------------
if [[ "$DO_PIN" -eq 1 ]]; then
  TARGET_TAG="$SHA_TAG"
elif [[ "$DO_RESTART" -eq 1 ]]; then
  TARGET_TAG="$MOVING_TAG"
fi

if [[ "$DO_RESTART" -eq 1 ]]; then
  if ! command -v az >/dev/null 2>&1; then
    echo "!! az CLI not found — skipping the Azure update step." >&2
    exit 0
  fi

  if [[ "$BUMP_ONLY" -eq 0 ]]; then
    echo
    echo "==> Pointing API App Service '$API_APP' at $REGISTRY/$API_IMAGE:$TARGET_TAG"
    az webapp config container set \
      -g "$RG" -n "$API_APP" \
      --container-image-name "docker.io/$REGISTRY/$API_IMAGE:$TARGET_TAG" \
      > /dev/null

    echo "==> Pointing Web App Service '$WEB_APP' at $REGISTRY/$WEB_IMAGE:$TARGET_TAG"
    az webapp config container set \
      -g "$RG" -n "$WEB_APP" \
      --container-image-name "docker.io/$REGISTRY/$WEB_IMAGE:$TARGET_TAG" \
      > /dev/null
  else
    echo
    echo "==> --bump: skipping container-set (assuming :$MOVING_TAG is already wired up)"
  fi

  echo
  echo "==> Restarting API"
  az webapp restart -g "$RG" -n "$API_APP" > /dev/null
  echo "==> Restarting Web"
  az webapp restart -g "$RG" -n "$WEB_APP" > /dev/null
fi

echo
echo "✔ Done."
echo
echo "Tail the API log to verify Mongo ping + template seed:"
echo "  az webapp log tail -g $RG -n $API_APP"
echo
echo "Health check:"
echo "  curl https://$API_APP.azurewebsites.net/health"

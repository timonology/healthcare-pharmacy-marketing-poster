#!/usr/bin/env bash
# ------------------------------------------------------------------
# One-shot deploy for the Pharmacy Poster monorepo.
#
# Builds the API + web + ai-service Docker images, pushes them to Docker
# Hub, and (optionally) tells Azure App Service to pull the new images.
#
# Image tags pushed each run:
#   :dev               — moving tag (matches what App Service is watching)
#   :<git-short-sha>   — immutable tag so you can roll back instantly
#
# Usage:
#   ./scripts/deploy.sh                  # build + push all three
#   ./scripts/deploy.sh --restart        # also restart the App Services
#   ./scripts/deploy.sh --pin            # also pin the App Services to the SHA tag
#   ./scripts/deploy.sh --bump           # restart only (no container-set)
#   SKIP_AI=1 ./scripts/deploy.sh        # skip the heavy ai-service build
#   SKIP_API=1 SKIP_WEB=1 ./scripts/deploy.sh   # only ship the ai-service
#   RG=my-rg API_APP=my-api WEB_APP=my-web AI_APP=my-ai ./scripts/deploy.sh --pin
# ------------------------------------------------------------------
set -euo pipefail

# --- Config (override via env vars) ------------------------------------------
REGISTRY="${REGISTRY:-infosonarinformatics}"
API_IMAGE="${API_IMAGE:-sonarmarketingbackend}"
WEB_IMAGE="${WEB_IMAGE:-sonar-marketing-auth}"
AI_IMAGE="${AI_IMAGE:-sonar-marketing-ai}"
MOVING_TAG="${MOVING_TAG:-dev}"

# How many times to retry a push before giving up. Large layers over a flaky
# link or a corporate proxy fail part-way surprisingly often, and a failure
# here is pure waste: the images are already built, and every layer that made
# it stays on the registry, so a retry resumes rather than restarts.
PUSH_RETRIES="${PUSH_RETRIES:-4}"

# Target architecture. Empty means "whatever this machine is", which is the
# historical behaviour. Azure App Service for Linux runs amd64 — so on an
# Apple Silicon Mac an unset PLATFORM produces an arm64 image that pushes
# perfectly and then will not start. Set PLATFORM=linux/amd64 to cross-build
# (slower, emulated) or build on an amd64 machine.
PLATFORM="${PLATFORM:-}"

# Which images to build. `SKIP_AI=1 ./scripts/deploy.sh` skips the ai-service
# (useful when you only touched the .NET or web code — the ai-service image
# is ~1.5GB with ffmpeg + moviepy and takes several minutes to build).
SKIP_API="${SKIP_API:-0}"
SKIP_WEB="${SKIP_WEB:-0}"
SKIP_AI="${SKIP_AI:-0}"

# Azure resources — only needed for --restart / --pin
RG="${RG:-rg-sonar-pharmacyposter}"
API_APP="${API_APP:-sonarmarketingbackend}"
WEB_APP="${WEB_APP:-sonar-marketing-auth}"
AI_APP="${AI_APP:-sonar-marketing-ai}"

# Derived
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# The image tag, and the BUILD_ID every service reports from /health (the
# API and ai-service at /health, the web app at /api/health).
#
# <b>A commit SHA alone is a lie whenever the working tree is dirty.</b> Docker
# builds from the files on disk, not from HEAD, so deploying with uncommitted
# changes produces an image whose marker names a commit that does not contain
# the code inside it — and two different images then carry the same id. That is
# not a theoretical worry: a production ai-service was serving an endpoint that
# existed in no commit while reporting the SHA of a commit predating it, which
# makes the marker useless for the one question it exists to answer.
#
# So a dirty tree gets `-dirty-<timestamp>` appended. It stays a valid Docker
# tag, it is unique per build, and it says plainly that this image cannot be
# reconstructed from git.
SHA_TAG="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo "manual-$(date +%Y%m%d-%H%M%S)")"
if [ -n "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]; then
  SHA_TAG="${SHA_TAG}-dirty-$(date +%Y%m%d-%H%M%S)"
  echo "!!  Working tree is dirty. Tagging this build $SHA_TAG."
  echo "    The image contains code that is in no commit — commit before a real"
  echo "    release, or you will not be able to tell later what shipped."
fi

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
[[ "$SKIP_API" -eq 0 ]] && echo "==> API:      $API_IMAGE  →  tags: $MOVING_TAG, $SHA_TAG" || echo "==> API:      skipped (SKIP_API=1)"
[[ "$SKIP_WEB" -eq 0 ]] && echo "==> Web:      $WEB_IMAGE  →  tags: $MOVING_TAG, $SHA_TAG" || echo "==> Web:      skipped (SKIP_WEB=1)"
[[ "$SKIP_AI"  -eq 0 ]] && echo "==> AI:       $AI_IMAGE   →  tags: $MOVING_TAG, $SHA_TAG" || echo "==> AI:       skipped (SKIP_AI=1)"
echo

cd "$REPO_ROOT"

# --- 0. Is Docker answering? -------------------------------------------------
# The build used to print "Building API image" and then sit there, silently,
# for as long as anyone waited: Docker Desktop's daemon was not responding, and
# `docker build` waits for it without a word. Ctrl-C then reported
# "error during connect … docker.sock/_ping". So ask first, with a deadline.
docker_ready() {
  docker info >/dev/null 2>&1 &
  local pid=$! waited=0
  while kill -0 "$pid" 2>/dev/null; do
    if (( waited >= 20 )); then
      kill "$pid" 2>/dev/null
      return 2
    fi
    sleep 1
    waited=$(( waited + 1 ))
  done
  wait "$pid" && return 0
  return 1
}
# `|| status=$?`, not a bare call: under `set -e` a failing check would exit
# here with no message, which is the silence this exists to end.
status=0
docker_ready || status=$?
case $status in
  0) ;;
  2)
    echo "!! Docker did not answer within 20s. Docker Desktop is running but stuck." >&2
    echo "!! Quit Docker Desktop fully (whale icon → Quit), start it again, wait for" >&2
    echo "!! 'Engine running', then re-run. 'Troubleshoot → Restart' also works." >&2
    exit 1 ;;
  *)
    echo "!! Docker is not running. Start Docker Desktop and re-run." >&2
    exit 1 ;;
esac

# Cross-building from Apple Silicon. The API and web images now compile on
# this machine natively and only their runtime layer is amd64; the ai-service
# image still installs its Python packages under emulation. Rosetta does that
# several times faster than the default QEMU.
if [[ "$PLATFORM" == "linux/amd64" ]] && [[ "$(uname -m)" == "arm64" ]]; then
  echo "==> Cross-building for amd64 on Apple Silicon."
  echo "    Tip: Docker Desktop → Settings → General → 'Use Rosetta for x86_64/amd64"
  echo "    emulation on Apple Silicon' makes the ai-service build much faster."
  echo
fi

# Prints how long a step took, so a slow ship says which part was slow.
step_started=0
step_start() { step_started=$SECONDS; }
step_done() { echo "    ($1 took $(( (SECONDS - step_started) / 60 ))m $(( (SECONDS - step_started) % 60 ))s)"; }

# Provenance attestations turn a single image into a manifest list with extra
# blobs. Useful for supply-chain verification, and here they only add things
# to upload and for App Service to resolve. Off when the builder supports the
# flag; silently skipped on an older Docker that does not.
BUILD_FLAGS=()
if docker build --help 2>/dev/null | grep -q -- '--provenance'; then
  BUILD_FLAGS+=(--provenance=false)
fi
[[ -n "$PLATFORM" ]] && BUILD_FLAGS+=(--platform "$PLATFORM")

# Warn once, rather than letting a container that cannot start be discovered
# from an App Service log twenty minutes later.
if [[ -z "$PLATFORM" ]] && [[ "$(uname -m)" == "arm64" || "$(uname -m)" == "aarch64" ]]; then
  echo "!! This machine is $(uname -m) and PLATFORM is unset, so the images will"
  echo "!! be arm64. Azure App Service for Linux runs amd64 and will fail to"
  echo "!! start them. Re-run with:  PLATFORM=linux/amd64 pnpm ship:<env>"
  echo
fi

# Retry a push, resuming from whatever already landed.
push_with_retry() {
  local ref="$1" attempt=1 wait_s
  while true; do
    if docker push "$ref"; then return 0; fi
    if (( attempt >= PUSH_RETRIES )); then
      echo "!! Push failed after $PUSH_RETRIES attempts: $ref" >&2
      echo "!! Layers that uploaded are kept, so re-running resumes from here." >&2
      return 1
    fi
    wait_s=$(( attempt * 10 ))
    echo "   push failed (attempt $attempt/$PUSH_RETRIES) — retrying in ${wait_s}s"
    sleep "$wait_s"
    attempt=$(( attempt + 1 ))
  done
}

# --- 1. Build ----------------------------------------------------------------
if [[ "$SKIP_API" -eq 0 ]]; then
  echo "==> Building API image"
  step_start
  docker build \
    -f apps/api/Dockerfile \
    --build-arg BUILD_ID="$SHA_TAG" \
    -t "$REGISTRY/$API_IMAGE:$MOVING_TAG" \
    -t "$REGISTRY/$API_IMAGE:$SHA_TAG" \
    ${BUILD_FLAGS[@]+"${BUILD_FLAGS[@]}"} \
    ./apps/api
  step_done "API build"
fi

if [[ "$SKIP_WEB" -eq 0 ]]; then
  echo
  echo "==> Building Web image"
  step_start
  docker build \
    -f apps/web/Dockerfile \
    --build-arg BUILD_ID="$SHA_TAG" \
    -t "$REGISTRY/$WEB_IMAGE:$MOVING_TAG" \
    -t "$REGISTRY/$WEB_IMAGE:$SHA_TAG" \
    ${BUILD_FLAGS[@]+"${BUILD_FLAGS[@]}"} \
    .
  step_done "Web build"
fi

if [[ "$SKIP_AI" -eq 0 ]]; then
  echo
  echo "==> Building AI service image"
  # The ai-service image ships ffmpeg + moviepy + edge-tts + fonts and lands
  # around 1.5-2 GB. First build is slow (~5 min); subsequent builds reuse
  # the base + apt layer, so only the Python COPY step is cold.
  # BUILD_ID is the SHA tag. The image prints it at boot and serves it from
  # /health, so "which build is running?" is answerable from the container
  # itself and resolves to the same commit as the registry tag beside it.
  step_start
  docker build \
    -f apps/ai-service/Dockerfile \
    --build-arg BUILD_ID="$SHA_TAG" \
    -t "$REGISTRY/$AI_IMAGE:$MOVING_TAG" \
    -t "$REGISTRY/$AI_IMAGE:$SHA_TAG" \
    ${BUILD_FLAGS[@]+"${BUILD_FLAGS[@]}"} \
    ./apps/ai-service
  step_done "AI service build"
fi

# --- 2. Push -----------------------------------------------------------------
if [[ "$SKIP_API" -eq 0 ]]; then
  echo
  echo "==> Pushing API image"
  push_with_retry "$REGISTRY/$API_IMAGE:$MOVING_TAG"
  push_with_retry "$REGISTRY/$API_IMAGE:$SHA_TAG"
fi

if [[ "$SKIP_WEB" -eq 0 ]]; then
  echo
  echo "==> Pushing Web image"
  push_with_retry "$REGISTRY/$WEB_IMAGE:$MOVING_TAG"
  push_with_retry "$REGISTRY/$WEB_IMAGE:$SHA_TAG"
fi

if [[ "$SKIP_AI" -eq 0 ]]; then
  echo
  echo "==> Pushing AI service image"
  push_with_retry "$REGISTRY/$AI_IMAGE:$MOVING_TAG"
  push_with_retry "$REGISTRY/$AI_IMAGE:$SHA_TAG"
fi

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
    if [[ "$SKIP_API" -eq 0 ]]; then
      echo
      echo "==> Pointing API App Service '$API_APP' at $REGISTRY/$API_IMAGE:$TARGET_TAG"
      az webapp config container set \
        -g "$RG" -n "$API_APP" \
        --container-image-name "docker.io/$REGISTRY/$API_IMAGE:$TARGET_TAG" \
        --container-registry-url "https://index.docker.io" \
        > /dev/null
    fi

    if [[ "$SKIP_WEB" -eq 0 ]]; then
      echo "==> Pointing Web App Service '$WEB_APP' at $REGISTRY/$WEB_IMAGE:$TARGET_TAG"
      az webapp config container set \
        -g "$RG" -n "$WEB_APP" \
        --container-image-name "docker.io/$REGISTRY/$WEB_IMAGE:$TARGET_TAG" \
        --container-registry-url "https://index.docker.io" \
        > /dev/null
    fi

    if [[ "$SKIP_AI" -eq 0 ]]; then
      echo "==> Pointing AI App Service '$AI_APP' at $REGISTRY/$AI_IMAGE:$TARGET_TAG"
      az webapp config container set \
        -g "$RG" -n "$AI_APP" \
        --container-image-name "docker.io/$REGISTRY/$AI_IMAGE:$TARGET_TAG" \
        --container-registry-url "https://index.docker.io" \
        > /dev/null
    fi
  else
    echo
    echo "==> --bump: skipping container-set (assuming :$MOVING_TAG is already wired up)"
  fi

  echo
  [[ "$SKIP_API" -eq 0 ]] && { echo "==> Restarting API"; az webapp restart -g "$RG" -n "$API_APP" > /dev/null; }
  [[ "$SKIP_WEB" -eq 0 ]] && { echo "==> Restarting Web"; az webapp restart -g "$RG" -n "$WEB_APP" > /dev/null; }
  [[ "$SKIP_AI"  -eq 0 ]] && { echo "==> Restarting AI";  az webapp restart -g "$RG" -n "$AI_APP"  > /dev/null; }
fi

echo
echo "✔ Done."
echo
echo "Tail logs to verify boot:"
[[ "$SKIP_API" -eq 0 ]] && echo "  API:  az webapp log tail -g $RG -n $API_APP"
[[ "$SKIP_WEB" -eq 0 ]] && echo "  Web:  az webapp log tail -g $RG -n $WEB_APP"
[[ "$SKIP_AI"  -eq 0 ]] && echo "  AI:   az webapp log tail -g $RG -n $AI_APP     # look for '✅ Video worker started'"
echo
echo "Health checks:"
[[ "$SKIP_API" -eq 0 ]] && echo "  API:  curl https://$API_APP.azurewebsites.net/health"
[[ "$SKIP_AI"  -eq 0 ]] && echo "  AI:   curl https://$AI_APP.azurewebsites.net/health"

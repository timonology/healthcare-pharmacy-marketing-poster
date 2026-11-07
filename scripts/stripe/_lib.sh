#!/usr/bin/env bash
#
# Shared helpers for the Stripe setup scripts. Sourced, not executed.
#
# shellcheck shell=bash

set -euo pipefail

# Stripe objects belong to the account that created them. `stripe login` stores
# its own session, which can point at a different account — or a different
# sandbox within one account — than the secret key your API uses. When that
# happens every object is created successfully and then the API reports
# "No such price", because it's looking in the other account.
#
# Passing --api-key explicitly makes the CLI and the API provably agree. Set
# STRIPE_SECRET_KEY to the same sk_test_… the API has and this plumbs it
# through; leave it unset to fall back to the CLI's stored session.
API_KEY_ARGS=()
resolve_api_key() {
  if [[ -n "${STRIPE_SECRET_KEY:-}" ]]; then
    API_KEY_ARGS=(--api-key "$STRIPE_SECRET_KEY")
    echo "🔑 Using STRIPE_SECRET_KEY (objects will belong to that key's account)"
  else
    cat <<'EOF'
⚠️  STRIPE_SECRET_KEY is not set, so the CLI's stored login will be used.

   If that session belongs to a different account or sandbox than your API's
   secret key, the objects created here will be invisible to the API and
   checkout will fail with "No such price".

   To be certain they match:
     export STRIPE_SECRET_KEY=sk_test_...   # the same key the API uses
EOF
    read -rp "   Continue with the CLI's stored login? [y/N] " ok
    [[ "$ok" == "y" || "$ok" == "Y" ]] || exit 1
  fi
}

require_stripe_cli() {
  if ! command -v stripe >/dev/null 2>&1; then
    cat >&2 <<'EOF'
❌ The Stripe CLI is not installed.

   macOS:  brew install stripe/stripe-cli/stripe
   other:  https://stripe.com/docs/stripe-cli

Then authenticate once with:  stripe login
EOF
    exit 1
  fi
}

# Sets LIVE_FLAG and MODE from "$1".
parse_mode() {
  LIVE_FLAG=""
  MODE="TEST"
  if [[ "${1:-}" == "--live" ]]; then
    LIVE_FLAG="--live"
    MODE="LIVE"
  fi
  MODE_LOWER=$(echo "$MODE" | tr '[:upper:]' '[:lower:]')
}

confirm_live() {
  [[ "$MODE" == "LIVE" ]] || return 0
  echo "⚠️  This writes to your LIVE Stripe account and can charge real cards."
  read -rp "   Type 'live' to continue: " confirm
  [[ "$confirm" == "live" ]] || { echo "Aborted."; exit 1; }
}

# extract_id <cli-output> <expected-prefix>
#
# Pulls the object id out of the Stripe CLI's response. The CLI prints the
# created object as JSON on success, but can also emit banners, upgrade
# notices, or telemetry lines alongside it — so this tries a strict JSON parse
# first and falls back to a regex scan rather than assuming the whole of stdout
# is a single JSON document.
extract_id() {
  printf '%s' "$1" | python3 -c '
import json, re, sys

raw = sys.stdin.read()
prefix = sys.argv[1]

try:
    obj = json.loads(raw)
    if isinstance(obj, dict) and str(obj.get("id", "")).startswith(prefix):
        print(obj["id"])
        sys.exit(0)
except json.JSONDecodeError:
    pass

m = re.search(r"\"id\"\s*:\s*\"(" + prefix + r"_[A-Za-z0-9_]+)\"", raw)
if not m:
    sys.stderr.write("could not find a %s_* id in the Stripe response\n" % prefix)
    sys.exit(1)
print(m.group(1))
' "$2"
}

# run_stripe <expected-prefix> <args...>
#
# Runs the Stripe CLI, and on failure prints what Stripe actually said instead
# of letting a JSON parse error bury it.
run_stripe() {
  local prefix="$1"; shift
  local out
  if ! out=$(stripe "$@" "${API_KEY_ARGS[@]+"${API_KEY_ARGS[@]}"}" 2>&1); then
    {
      echo "❌ Stripe CLI failed:"
      echo "   stripe $*"
      echo "$out" | sed 's/^/   /'
    } >&2
    exit 1
  fi
  if ! extract_id "$out" "$prefix"; then
    {
      echo "❌ Could not read the created object's id. Stripe said:"
      echo "$out" | sed 's/^/   /'
    } >&2
    exit 1
  fi
}

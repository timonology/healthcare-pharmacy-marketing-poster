#!/usr/bin/env bash
#
# Starts webhook forwarding, pinned to the same Stripe account the API uses.
#
# Why not plain `stripe listen`: that uses the session stored by `stripe login`,
# which can point at a different account — or a different sandbox within one
# account — than your API's secret key. When it does, the listener runs happily
# and forwards nothing, because the events are happening in the other account.
# Payments succeed, no webhook arrives, and nobody gets upgraded. Passing
# --api-key removes the ambiguity.
#
# Usage:
#   ./listen.sh
#
set -euo pipefail
cd "$(dirname "$0")"

API_DIR="../../apps/api/src/Acme.WebApi"
FORWARD_TO="${FORWARD_TO:-http://localhost:5050/api/billing/webhook}"

KEY="${STRIPE_SECRET_KEY:-}"
if [[ -z "$KEY" ]]; then
  KEY=$(dotnet user-secrets --project "$API_DIR" list 2>/dev/null \
        | sed -n 's/^Stripe:SecretKey = //p' | tr -d '[:space:]')
fi

if [[ -z "$KEY" ]]; then
  echo "❌ No Stripe secret key found in STRIPE_SECRET_KEY or user-secrets." >&2
  exit 1
fi

cat <<EOF
👂 Forwarding Stripe webhooks
==================================================================
   to:      $FORWARD_TO
   account: pinned to the API's own secret key

The whsec_… printed below is specific to THIS session. If it differs from
what's in user-secrets, signature verification will reject every event —
copy it across with:

  dotnet user-secrets --project $API_DIR set "Stripe:WebhookSecret" "whsec_..."

Leave this running while you test.
==================================================================

EOF

exec stripe listen --api-key "$KEY" --forward-to "$FORWARD_TO"

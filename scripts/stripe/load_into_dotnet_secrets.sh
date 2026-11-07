#!/usr/bin/env bash
#
# Loads the generated Stripe price ids, plus your secret key and webhook
# secret, into .NET user-secrets for local development.
#
# Why user-secrets rather than appsettings.Development.json: that file is
# tracked by git in this repo, so putting sk_test_… in it would commit a live
# credential. User-secrets live in your OS profile, outside the working tree,
# and ASP.NET loads them automatically in the Development environment.
#
# Usage:
#   ./load_into_dotnet_secrets.sh
#
set -euo pipefail
cd "$(dirname "$0")"

API_DIR="../../apps/api/src/Acme.WebApi"
SRC="stripe_prices.test.env"

if [[ ! -f "$SRC" ]]; then
  echo "❌ $SRC not found. Run ./create_stripe_prices.sh first." >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "❌ dotnet not found on PATH." >&2
  exit 1
fi

echo "🔐 Loading Stripe config into user-secrets for Acme.WebApi"
echo "=================================================================="

set_secret() {
  local key="$1" value="$2"
  [[ -n "$value" ]] || return 0
  dotnet user-secrets --project "$API_DIR" set "$key" "$value" >/dev/null
  # Never echo the value — this output tends to end up pasted into chats.
  printf '  ✅ %s\n' "$key"
}

# Translate the Stripe__A__B env form into the Stripe:A:B form user-secrets
# expects. Doing it mechanically removes the single-vs-double underscore
# mistake that otherwise silently leaves a setting unbound.
while IFS='=' read -r rawkey value; do
  [[ -z "$rawkey" || "$rawkey" == \#* ]] && continue
  key="${rawkey//__/:}"
  set_secret "$key" "$value"
done < "$SRC"

echo ""
echo "Now the two secrets. Both are hidden as you type."
echo ""

read -rsp "  Stripe secret key (sk_test_…): " SECRET_KEY
echo ""
if [[ -n "$SECRET_KEY" && "$SECRET_KEY" != sk_test_* ]]; then
  echo "  ⚠️  That doesn't look like a TEST key. Live keys belong in your host's" >&2
  echo "     environment variables, not in local user-secrets." >&2
  read -rp "     Continue anyway? [y/N] " ok
  [[ "$ok" == "y" || "$ok" == "Y" ]] || exit 1
fi
set_secret "Stripe:SecretKey" "$SECRET_KEY"

echo ""
echo "  Get this by running, in another terminal:"
echo "    stripe listen --forward-to http://localhost:5050/api/billing/webhook"
echo ""
read -rsp "  Webhook signing secret (whsec_…): " WEBHOOK_SECRET
echo ""
set_secret "Stripe:WebhookSecret" "$WEBHOOK_SECRET"

echo ""
echo "=================================================================="
echo "✅ Done. Verify with:"
echo "     dotnet user-secrets --project $API_DIR list"
echo ""
echo "Then restart the API. On boot it should NOT log any Stripe warning —"
echo "a missing key throws at startup rather than failing at checkout."
echo ""

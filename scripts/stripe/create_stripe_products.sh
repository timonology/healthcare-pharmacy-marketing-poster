#!/usr/bin/env bash
#
# Creates the Sonar Marketing25 product catalogue in Stripe.
#
# Run once per Stripe mode (once in test, once in live). Products are durable —
# you only re-run this to add a new plan or credit pack.
#
# Usage:
#   ./create_stripe_products.sh              # test mode
#   ./create_stripe_products.sh --live       # live mode
#
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=_lib.sh
source ./_lib.sh

require_stripe_cli
parse_mode "${1:-}"
resolve_api_key

echo "🚀 Creating Stripe products for Sonar Marketing25  [$MODE mode]"
echo "=================================================================="
confirm_live

OUT="stripe_products.${MODE_LOWER}.env"
TMP=$(mktemp)
# Only replace the real output file once every product succeeded, so a partial
# failure can't leave behind a half-written file that the prices script would
# then happily source.
trap 'rm -f "$TMP"' EXIT

create_product() {
  local name="$1" description="$2" varname="$3" id
  id=$(run_stripe prod products create ${LIVE_FLAG:+$LIVE_FLAG} \
        --name="$name" \
        --description="$description")
  printf '  ✅ %-22s → %s\n' "$name" "$id"
  echo "${varname}=${id}" >> "$TMP"
}

echo ""
echo "Subscription plans"
echo "------------------"
create_product "Starter" \
  "30 posters, 50 AI generations, 5 marketing videos per month" \
  PRODUCT_STARTER

create_product "Pro" \
  "Unlimited posters and AI generations, 30 videos per month incl. AI imagery" \
  PRODUCT_PRO

echo ""
echo "Video credit packs (one-time)"
echo "----------------------------"
create_product "10 video credits" \
  "10 extra marketing videos, on top of your monthly allowance" \
  PRODUCT_CREDITS_10

create_product "25 video credits" \
  "25 extra marketing videos, on top of your monthly allowance" \
  PRODUCT_CREDITS_25

create_product "50 video credits" \
  "50 extra marketing videos, on top of your monthly allowance" \
  PRODUCT_CREDITS_50

mv "$TMP" "$OUT"
trap - EXIT

echo ""
echo "=================================================================="
echo "✅ Products created. Ids written to: $OUT"
echo ""
echo "Next:"
if [[ -n "$LIVE_FLAG" ]]; then
  echo "   ./create_stripe_prices.sh --live"
else
  echo "   ./create_stripe_prices.sh"
fi
echo ""

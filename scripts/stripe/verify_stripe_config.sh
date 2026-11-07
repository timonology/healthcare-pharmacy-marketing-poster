#!/usr/bin/env bash
#
# Checks that every price id the API is configured with actually resolves
# against the API's own secret key, and that the amounts are what you intended.
#
# This exists because the failure mode is genuinely confusing: objects create
# successfully, the key authenticates fine, and then checkout dies with
# "No such price" — because the CLI and the API were pointed at different
# Stripe accounts or sandboxes. Everything looks configured; nothing works.
#
# Run after any change to prices or keys, and before going live.
#
# Usage:
#   ./verify_stripe_config.sh                       # reads key from user-secrets
#   STRIPE_SECRET_KEY=sk_test_... ./verify_stripe_config.sh
#
set -euo pipefail
cd "$(dirname "$0")"

API_DIR="../../apps/api/src/Acme.WebApi"
SETTINGS="$API_DIR/appsettings.json"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# ---------------------------------------------------------------------------
# Helper scripts are written to files rather than passed with `python3 -c '…'`.
# Inline single-quoted Python cannot contain an escaped double quote — bash
# treats \" literally inside single quotes, so the Python ends up malformed and
# fails in a way that's easy to mistake for an API error. Files sidestep the
# whole quoting problem.
# ---------------------------------------------------------------------------

cat > "$WORK/read_settings.py" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1]))
s = cfg.get("Stripe", {})
for name, pid in (s.get("Prices") or {}).items():
    print("Prices.%s\t%s" % (name, pid or ""))
for pack in (s.get("CreditPacks") or []):
    print("CreditPacks.%s\t%s" % (pack.get("Key"), pack.get("PriceId") or ""))
PY

cat > "$WORK/describe_account.py" <<'PY'
import json, sys
try:
    d = json.load(sys.stdin)
except Exception:
    print("(could not parse account response)")
    sys.exit(0)
if "error" in d:
    print("ERROR: " + d["error"].get("message", "unknown"))
    sys.exit(0)
name = (d.get("settings") or {}).get("dashboard", {}).get("display_name") or "(unnamed)"
print("%s - %s" % (d.get("id", "(unknown id)"), name))
PY

cat > "$WORK/describe_price.py" <<'PY'
import json, sys

try:
    d = json.load(sys.stdin)
except Exception:
    print("ERR could not parse Stripe's response")
    sys.exit(0)

if "error" in d:
    print("ERR " + d["error"].get("message", "unknown error"))
    sys.exit(0)

amount = d.get("unit_amount")
currency = (d.get("currency") or "").upper()
recurring = d.get("recurring") or None

if recurring:
    cadence = "/" + str(recurring.get("interval", "?"))
else:
    cadence = " one-time"

money = "?" if amount is None else "%.2f" % (amount / 100.0)
flags = "" if d.get("active") else "  <- INACTIVE"

print("OK %s %s%s%s" % (currency, money, cadence, flags))
PY

# ---------------------------------------------------------------------------
# Resolve the key. Never echoed — this output tends to get pasted into chats.
# ---------------------------------------------------------------------------
KEY="${STRIPE_SECRET_KEY:-}"
if [[ -z "$KEY" ]]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "❌ Set STRIPE_SECRET_KEY, or install dotnet so the key can be read from user-secrets." >&2
    exit 1
  fi
  KEY=$(dotnet user-secrets --project "$API_DIR" list 2>/dev/null \
        | sed -n 's/^Stripe:SecretKey = //p' | tr -d '[:space:]')
fi

if [[ -z "$KEY" ]]; then
  echo "❌ No Stripe secret key found in STRIPE_SECRET_KEY or user-secrets." >&2
  echo "   Check: dotnet user-secrets --project $API_DIR list" >&2
  exit 1
fi

echo "🔍 Verifying Stripe configuration"
echo "=================================================================="
if [[ "$KEY" == sk_live_* ]]; then
  echo "   key mode: LIVE"
elif [[ "$KEY" == sk_test_* ]]; then
  echo "   key mode: test"
else
  echo "   key mode: unrecognised prefix — is this really a Stripe secret key?"
fi

ACCOUNT=$(curl -s https://api.stripe.com/v1/account -u "$KEY": | python3 "$WORK/describe_account.py")
echo "   account:  $ACCOUNT"
echo ""

python3 "$WORK/read_settings.py" "$SETTINGS" > "$WORK/entries.tsv"

# `while read` rather than mapfile: macOS still ships bash 3.2.
fail=0
while IFS=$'\t' read -r label pid; do
  [[ -n "$label" ]] || continue

  if [[ -z "$pid" ]]; then
    printf '  ⚠️  %-28s (not configured)\n' "$label"
    continue
  fi

  if [[ "$pid" != price_* ]]; then
    printf '  ❌ %-28s %s  <- not a price id\n' "$label" "$pid"
    fail=1
    continue
  fi

  detail=$(curl -s "https://api.stripe.com/v1/prices/$pid" -u "$KEY": \
           | python3 "$WORK/describe_price.py")

  if [[ "$detail" == OK* ]]; then
    printf '  ✅ %-28s %s\n' "$label" "${detail#OK }"
  else
    printf '  ❌ %-28s %s\n' "$label" "${detail#ERR }"
    fail=1
  fi
done < "$WORK/entries.tsv"

echo ""
echo "=================================================================="
if [[ $fail -eq 0 ]]; then
  cat <<'EOF'
✅ Every configured price resolves against this key.

Check the amounts above match what you intended — a factor-of-100 slip in the
pence conversion produces valid prices that are simply wrong, and this is the
last point at which that's cheap to notice.
EOF
else
  cat <<'EOF'
❌ At least one price did not resolve.

If the message was "No such price", the prices live in a different Stripe
account or sandbox than the key the API uses. Recreate them against that key:

  export STRIPE_SECRET_KEY=sk_test_...      # the key the API uses
  ./create_stripe_products.sh
  ./create_stripe_prices.sh

Then copy the new ids from stripe_prices.test.env into appsettings.json and
re-run this script.
EOF
  exit 1
fi

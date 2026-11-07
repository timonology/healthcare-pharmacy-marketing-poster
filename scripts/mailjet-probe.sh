#!/usr/bin/env bash
#
# Does Mailjet accept a large request from THIS machine?
#
# Campaign sends fail with `Broken pipe` — the connection closes while the body
# is still uploading — at about 1.6 MB for a single recipient. Mailjet's
# documented limit is 15 MB, so the size alone does not explain it. Two fixes
# aimed at the application (size-aware batching, then Content-Length plus
# Expect: 100-continue) both left the failure unchanged.
#
# That pattern means the next question is not "what else can the code do" but
# "is the code involved at all". This script asks Mailjet the same question the
# app asks, with curl, from the same machine — no .NET, no HttpClient, none of
# our code in the path.
#
#   Every size succeeds  -> the problem is in our code after all.
#   Large sizes fail too -> the problem is the network path or the credentials,
#                           and no amount of editing MailjetEmailSender will fix
#                           it. Look at a proxy, a TLS-inspecting firewall, VPN,
#                           or antivirus doing HTTPS scanning.
#   Every size fails      -> credentials. Rotate the key (overdue anyway) and
#                           re-run.
#
# SandboxMode is on, so Mailjet validates the request fully and delivers
# nothing. Safe to run repeatedly, and safe to point at a real address.
#
# Usage:
#   ./scripts/mailjet-probe.sh you@example.com
#   ./scripts/mailjet-probe.sh you@example.com 0 500 1000 2000 4000
#
# Credentials are read from the environment, falling back to appsettings.json.
#   MAILJET_KEY=... MAILJET_SECRET=... ./scripts/mailjet-probe.sh you@example.com

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SETTINGS="$ROOT/apps/api/src/Acme.WebApi/appsettings.json"

TO="${1:-}"
if [[ -z "$TO" ]]; then
  echo "usage: $0 <recipient-email> [sizeKB ...]" >&2
  exit 2
fi
shift || true

SIZES=("$@")
if [[ ${#SIZES[@]} -eq 0 ]]; then
  SIZES=(0 250 500 1000 1500 2000 4000)
fi

# ---- credentials -----------------------------------------------------------
KEY="${MAILJET_KEY:-}"
SECRET="${MAILJET_SECRET:-}"

if [[ -z "$KEY" || -z "$SECRET" ]] && [[ -f "$SETTINGS" ]]; then
  read -r KEY SECRET <<<"$(python3 - "$SETTINGS" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
mj = d.get("Messaging", {}).get("Mailjet", {})
print(mj.get("ApiKey") or "", mj.get("SecretKey") or "")
PY
)"
fi

if [[ -z "$KEY" || -z "$SECRET" ]]; then
  echo "No Mailjet credentials. Set MAILJET_KEY and MAILJET_SECRET." >&2
  exit 2
fi

# Never printed in full: this script is meant to be run and its output pasted
# into a chat or an issue.
echo "Key ...${KEY: -4}   host api.mailjet.com   recipient $TO"
echo "SandboxMode is on — Mailjet validates and delivers nothing."
echo

FROM="$(python3 - "$SETTINGS" <<'PY' 2>/dev/null || echo ""
import json, sys
d = json.load(open(sys.argv[1]))
print(d.get("Messaging", {}).get("Mailjet", {}).get("FromAddress") or "")
PY
)"
if [[ -z "$FROM" ]]; then
  echo "No Messaging:Mailjet:FromAddress configured — using the recipient as sender." >&2
  FROM="$TO"
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

printf '%-8s %-12s %-9s %-8s  %s\n' "SIZE" "UPLOADED" "HTTP" "TIME" "RESULT"
printf '%.0s-' {1..78}; echo

for KB in "${SIZES[@]}"; do
  BODY="$TMP/body.json"

  if [[ "$KB" -eq 0 ]]; then
    ATTACH=""
  else
    # base64 inflates by 4/3, so feed 3/4 of the target to land on KB kilobytes
    # of actual JSON string. Random bytes, not zeroes — a run of zeroes
    # compresses away under TLS and would not move the same data.
    B64="$(head -c $((KB * 1024 * 3 / 4)) /dev/urandom | base64 | tr -d '\n')"
    ATTACH=",\"InlinedAttachments\":[{\"ContentType\":\"image/png\",\"Filename\":\"probe.png\",\"ContentID\":\"probe\",\"Base64Content\":\"$B64\"}]"
  fi

  cat >"$BODY" <<JSON
{"SandboxMode":true,"Messages":[{"From":{"Email":"$FROM","Name":"Probe"},"To":[{"Email":"$TO"}],"Subject":"Mailjet probe ${KB}KB","TextPart":"probe","HTMLPart":"<p>probe</p>"$ATTACH}]}
JSON

  ACTUAL=$(wc -c <"$BODY" | tr -d ' ')

  OUT=$(curl -sS -o "$TMP/resp" -w '%{http_code} %{size_upload} %{time_total}' \
        -u "$KEY:$SECRET" \
        -H 'Content-Type: application/json' \
        --data-binary "@$BODY" \
        --max-time 120 \
        https://api.mailjet.com/v3.1/send 2>"$TMP/err")
  RC=$?

  if [[ $RC -ne 0 ]]; then
    printf '%-8s %-12s %-9s %-8s  %s\n' "${KB}KB" "$ACTUAL" "-" "-" \
      "curl exit $RC: $(tr '\n' ' ' <"$TMP/err" | cut -c1-40)"
    continue
  fi

  read -r CODE UP TIME <<<"$OUT"
  if [[ "$CODE" == "200" ]]; then
    VERDICT="OK"
  else
    VERDICT="$(tr -d '\n' <"$TMP/resp" | cut -c1-38)"
  fi
  printf '%-8s %-12s %-9s %-8s  %s\n' "${KB}KB" "$UP" "$CODE" "${TIME}s" "$VERDICT"
done

echo
echo "Paste this table back. The size where it turns over is the answer."

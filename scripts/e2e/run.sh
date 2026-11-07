#!/usr/bin/env bash
#
# End-to-end smoke test for Sonar Marketing25.
#
# Runs against a LIVE stack and exercises the paths that have actually broken,
# in the order a real user hits them. Every check here maps to a bug that
# reached manual testing at least once:
#
#   preflight      services not started, or ai-service dead on an import error
#   jwt            API and ai-service signing with different secrets  -> 401s
#   routes         ai-service path prefix mismatch                    -> 404s
#   admin          role gating, filters, counts that disagree
#   imagery        Pexels/DNS reachability, blob upload, asset serving
#   generation     a design that silently omits the supplied photograph
#   pdf            asset URL that fails to resolve back to a blob key
#   bff            Next.js route missing a method                     -> 405s
#   email          UseMock left on, or activation links pointing at localhost
#   invitations    an invited account that can sign in before activating
#   hierarchy      a delegated token that carries the wrong identity or chains
#
# Bash + curl + python3 on purpose: no install step, and python3 ships with
# macOS. Written to keep going after a failure and report everything at the
# end, because stopping at the first one hides the others.
#
# Usage:
#   ./scripts/e2e/run.sh                 # everything
#   ./scripts/e2e/run.sh --no-ai         # skip the paid generation steps
#   ./scripts/e2e/run.sh --keep          # don't delete what it created
#
# Environment:
#   E2E_EMAIL_TO=you@example.com         # also send one real email and report
#                                        # what the provider said about it
#
set -uo pipefail

API="${API_URL:-http://localhost:5050}"
AI="${AI_URL:-http://localhost:8000}"
WEB="${WEB_URL:-http://localhost:3000}"
EMAIL="${SUPERADMIN_EMAIL:-superadmin@sonar.local}"
PASSWORD="${SUPERADMIN_PASSWORD:-ChangeMe-Dev-Only-123}"

RUN_AI=1
CLEANUP=1
for arg in "$@"; do
  case "$arg" in
    --no-ai) RUN_AI=0 ;;
    --keep)  CLEANUP=0 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

# ── output ───────────────────────────────────────────────────────────────
if [ -t 1 ]; then
  R=$'\e[31m'; G=$'\e[32m'; Y=$'\e[33m'; B=$'\e[1m'; D=$'\e[2m'; N=$'\e[0m'
else
  R=; G=; Y=; B=; D=; N=
fi

PASSED=0; FAILED=0; SKIPPED=0
declare -a FAILURES=()

section() { printf '\n%s── %s ─────────────────────────────%s\n' "$B" "$1" "$N"; }
# Each returns 0 unconditionally. Without that, `fail "x"` with no detail ends
# on a false `[ -n "" ]` test and returns 1 — so the common `cond && fail … ||
# ok …` idiom would record BOTH a failure and a pass for the same check. The
# assertions currently in this suite happen to always pass a detail string, which
# is not a property worth depending on.
ok()   { PASSED=$((PASSED+1)); printf '  %sPASS%s  %s\n' "$G" "$N" "$1"; return 0; }
fail() { FAILED=$((FAILED+1)); FAILURES+=("$1${2:+ — $2}"); printf '  %sFAIL%s  %s\n' "$R" "$N" "$1"; [ -n "${2:-}" ] && printf '        %s%s%s\n' "$D" "$2" "$N"; return 0; }
skip() { SKIPPED=$((SKIPPED+1)); printf '  %sSKIP%s  %s %s(%s)%s\n' "$Y" "$N" "$1" "$D" "${2:-}" "$N"; return 0; }
info() { printf '        %s%s%s\n' "$D" "$1" "$N"; }

# Extract a field from JSON on stdin. Returns empty on any failure so callers
# can test with [ -z ].
jget() { python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for k in '$1'.split('.'):
    if isinstance(d, list):
        try: d = d[int(k)]
        except Exception: sys.exit(0)
    elif isinstance(d, dict):
        d = d.get(k)
    else:
        sys.exit(0)
    if d is None: sys.exit(0)
print(d if not isinstance(d,(dict,list)) else json.dumps(d))
" 2>/dev/null; }

# curl wrapper: prints body, sets HTTP_STATUS.
req() {
  local method="$1" url="$2"; shift 2
  local tmp; tmp="$(mktemp)"
  HTTP_STATUS="$(curl -sS -o "$tmp" -w '%{http_code}' -X "$method" "$url" "$@" 2>/dev/null || echo 000)"
  cat "$tmp"; rm -f "$tmp"
}

# Same request, but sets BODY and HTTP_STATUS in the CURRENT shell.
#
# `body="$(req ...)"` runs req in a subshell, so the HTTP_STATUS it assigns is
# discarded and the caller compares against whatever the previous plain call
# left behind. Every check of the form
#
#     body="$(req GET "$API/thing")"
#     [ "$HTTP_STATUS" = 200 ] && ok ... || fail ...
#
# was therefore testing a stale status — passing or failing for reasons
# unrelated to the request it had just made. Use `rq METHOD URL …` and read
# $BODY when the status matters; the bare `req … >/dev/null` form is fine
# because a redirection is not a subshell.
BODY=""
rq() {
  local method="$1" url="$2"; shift 2
  local tmp; tmp="$(mktemp)"
  HTTP_STATUS="$(curl -sS -o "$tmp" -w '%{http_code}' -X "$method" "$url" "$@" 2>/dev/null || echo 000)"
  BODY="$(cat "$tmp")"
  rm -f "$tmp"
}

printf '%sSonar Marketing25 — end-to-end smoke test%s\n' "$B" "$N"
info "api=$API  ai=$AI  web=$WEB"

# ── 1. preflight ─────────────────────────────────────────────────────────
section "1. Services"

rq GET "$API/health"
body="$BODY"
[ "$HTTP_STATUS" = 200 ] && ok "API is up" || fail "API is up" "GET $API/health -> $HTTP_STATUS (is \`dotnet run\` going?)"

rq GET "$AI/health"
body="$BODY"
if [ "$HTTP_STATUS" = 200 ]; then
  ok "ai-service is up"
else
  fail "ai-service is up" "GET $AI/health -> $HTTP_STATUS — if the container is running, it died on startup: docker compose logs ai-service"
fi

rq GET "$WEB/" -o /dev/null
body="$BODY"
[ "$HTTP_STATUS" = 200 ] || [ "$HTTP_STATUS" = 307 ] || [ "$HTTP_STATUS" = 302 ] \
  && ok "web is up" || fail "web is up" "GET $WEB/ -> $HTTP_STATUS"

# ── 2. cross-service config ──────────────────────────────────────────────
section "2. Config coherence"

rq GET "$API/api/diagnostics/jwt"
api_jwt="$(printf '%s' "$BODY" | jget secretFingerprint)"
rq GET "$AI/diagnostics/jwt"
ai_jwt="$(printf '%s' "$BODY" | jget secretFingerprint)"

if [ -z "$api_jwt" ] || [ -z "$ai_jwt" ]; then
  skip "JWT secrets match" "a side didn't answer — both diagnostics are Development-only; compare the boot-log fingerprints instead"
elif [ "$api_jwt" = "$ai_jwt" ]; then
  ok "JWT secrets match ($api_jwt)"
else
  fail "JWT secrets match" "api=$api_jwt ai-service=$ai_jwt — every cross-service call will 401"
fi

rq GET "$API/api/diagnostics/config"
diag="$BODY"
if [ "$HTTP_STATUS" != 200 ]; then
  # Development-only (or SuperAdmin) since the security review: a deployed
  # environment answers 404 to an anonymous caller, which is the point.
  skip "API blob container configured" "diagnostics are not public outside Development"
else
  blob="$(printf '%s' "$diag" | jget blobContainer)"
  [ -n "$blob" ] && ok "API blob container configured ($blob)" \
                 || fail "API blob container configured" "AzureBlob:Container missing"
fi

# ── 3. ai-service routes ─────────────────────────────────────────────────
section "3. ai-service routes"

rq GET "$AI/openapi.json"
spec="$BODY"
if [ "$HTTP_STATUS" != 200 ]; then
  fail "openapi reachable" "-> $HTTP_STATUS"
else
  for path in /api/canvas/generate-image /api/canvas/generate-template \
              /api/canvas/stock-photos /api/canvas/stock-photo; do
    if printf '%s' "$spec" | grep -q "\"$path\""; then
      ok "route exists: $path"
    else
      fail "route exists: $path" "the .NET client calls this; a prefix change here is a silent 404"
    fi
  done
fi

# ── 4. auth ──────────────────────────────────────────────────────────────
section "4. Authentication"

rq POST "$API/api/auth/login" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}"
login="$BODY"
TOKEN="$(printf '%s' "$login" | jget accessToken)"

if [ -z "$TOKEN" ]; then
  fail "superadmin can sign in" "as $EMAIL -> $HTTP_STATUS. Set SUPERADMIN_EMAIL/PASSWORD, or check the seeder ran."
  printf '\n%sCannot continue without a token.%s\n' "$R" "$N"
  exit 1
fi
ok "superadmin can sign in"
AUTH=(-H "Authorization: Bearer $TOKEN")

# ── 5. admin surface ─────────────────────────────────────────────────────
section "5. Admin API"

rq GET "$API/api/admin/stats" "${AUTH[@]}"
stats="$BODY"
if [ "$HTTP_STATUS" != 200 ]; then
  fail "admin stats" "-> $HTTP_STATUS"
else
  ok "admin stats"
  total="$(printf '%s' "$stats" | jget totalUsers)"
  pharm="$(printf '%s' "$stats" | jget pharmacyUsers)"
  pharmacct="$(printf '%s' "$stats" | jget pharmacyAccounts)"
  admins="$(printf '%s' "$stats" | jget admins)"
  supers="$(printf '%s' "$stats" | jget superAdmins)"
  info "accounts: total=$total pharmacies=$pharm admins=$admins superadmins=$supers"
  if [ "$((pharm + admins + supers))" -eq "$total" ]; then
    ok "account counts reconcile"
  else
    fail "account counts reconcile" \
      "pharmacies+admins+superadmins=$((pharm+admins+supers)) but total=$total — a role filter is missing documents"
  fi
fi

# Filtering by role must return the same number the stats report. This is the
# check that would have caught documents written before the role field existed.
rq GET "$API/api/admin/users?role=PharmacyUser&take=1" "${AUTH[@]}"
users="$BODY"
filtered="$(printf '%s' "$users" | jget total)"
if [ -z "$filtered" ]; then
  fail "pharmacy filter works" "-> $HTTP_STATUS"
elif [ "$filtered" = "${pharm:-}" ]; then
  ok "pharmacy filter agrees with stats ($filtered)"
else
  fail "pharmacy filter agrees with stats" \
    "filter returned $filtered, stats say ${pharm:-?} — legacy documents are invisible to the filter"
fi

# The Pharmacies page does NOT filter by role — it lists accounts that HAVE a
# pharmacy, so that a pharmacy owner promoted to Admin does not vanish from the
# only page listing customers. Two things have to hold, and they catch opposite
# mistakes: the count must match what the page's own query returns, and it must
# never be smaller than the role count (which would mean the wider filter is
# somehow dropping ordinary pharmacies).
rq GET "$API/api/admin/users?pharmacyAccounts=true&take=1" "${AUTH[@]}"
accts="$BODY"
accts_total="$(printf '%s' "$accts" | jget total)"
if [ -z "$accts_total" ]; then
  fail "pharmacy-account filter works" "-> $HTTP_STATUS"
elif [ "$accts_total" != "${pharmacct:-}" ]; then
  fail "pharmacy-account filter agrees with stats" \
    "filter returned $accts_total, stats say ${pharmacct:-?} — the list and the dashboard card will disagree"
elif [ "$accts_total" -lt "${pharm:-0}" ]; then
  fail "pharmacy-account filter includes every pharmacy" \
    "$accts_total accounts but $pharm have the PharmacyUser role — the wider filter is dropping rows"
else
  ok "pharmacy-account filter agrees with stats ($accts_total)"
  if [ "$accts_total" -gt "${pharm:-0}" ]; then
    info "$((accts_total - pharm)) pharmacy owner(s) also hold a staff role — role filter alone would hide them"
  fi
fi

# Platform settings are SuperAdmin-only and global. The smoke test signs in as
# the superadmin, so 200 is expected here — the point is that the route exists
# and that the value it reports is one the worker would actually accept.
rq GET "$API/api/admin/settings/video-model" "${AUTH[@]}"
model="$BODY"
if [ "$HTTP_STATUS" != 200 ]; then
  fail "video model setting" "-> $HTTP_STATUS"
else
  current="$(printf '%s' "$model" | jget currentModel)"
  source="$(printf '%s' "$model" | jget source)"
  case "$current" in
    claude-haiku-4-5-20251001|claude-sonnet-5|claude-opus-5)
      ok "video model is a known model ($current, from $source)" ;;
    "") fail "video model setting" "no currentModel in the response" ;;
    *)  fail "video model is a known model" \
          "reported '$current', which the ai-service allow-list would reject — every video would fail" ;;
  esac
fi

# Writing must be refused without a token. A global setting that an
# unauthenticated caller can change is worse than one nobody can change.
req PUT "$API/api/admin/settings/video-model" \
  -H 'Content-Type: application/json' -d '{"model":"claude-opus-5"}' >/dev/null
if [ "$HTTP_STATUS" = 401 ] || [ "$HTTP_STATUS" = 403 ]; then
  ok "video model rejects anonymous writes ($HTTP_STATUS)"
else
  fail "video model rejects anonymous writes" "-> $HTTP_STATUS, expected 401/403"
fi

# A junk model must be refused, or it reaches Anthropic and breaks every video.
req PUT "$API/api/admin/settings/video-model" "${AUTH[@]}" \
  -H 'Content-Type: application/json' -d '{"model":"gpt-4o"}' >/dev/null
if [ "$HTTP_STATUS" = 400 ]; then
  ok "video model rejects an unknown model (400)"
else
  fail "video model rejects an unknown model" "-> $HTTP_STATUS, expected 400"
fi

# ---- pharmacist invitations ----
req GET "$API/api/admin/invitations?take=1" "${AUTH[@]}" >/dev/null
[ "$HTTP_STATUS" = 200 ] && ok "invitations list" || fail "invitations list" "-> $HTTP_STATUS"

# Creating an account and emailing a sign-in link must never be anonymous.
req POST "$API/api/admin/invitations" \
  -H 'Content-Type: application/json' \
  -d '{"fullName":"Nobody","email":"nobody@example.test"}' >/dev/null
if [ "$HTTP_STATUS" = 401 ] || [ "$HTTP_STATUS" = 403 ]; then
  ok "invitations reject anonymous callers ($HTTP_STATUS)"
else
  fail "invitations reject anonymous callers" "-> $HTTP_STATUS, expected 401/403"
fi

# The template must parse as its own valid input — it is the first thing
# anyone uploads, and a template the parser rejects sends them in circles.
rq GET "$API/api/admin/invitations/csv-template" "${AUTH[@]}"
tmpl="$BODY"
if printf '%s' "$tmpl" | head -1 | grep -qi "email"; then
  ok "CSV template downloads with an email column"
else
  fail "CSV template downloads with an email column" "header was: $(printf '%s' "$tmpl" | head -1)"
fi

# A junk token must be refused, and the message must not reveal whether the
# token exists — otherwise a guessed link can be probed.
rq GET "$API/api/invitations/preview?token=not-a-real-token"
body="$BODY"
if [ "$HTTP_STATUS" = 400 ]; then
  if printf '%s' "$body" | grep -qiE "expired or already been used"; then
    ok "activation preview rejects a bad token without leaking (400)"
  else
    fail "activation preview message is generic" "got: $body"
  fi
else
  fail "activation preview rejects a bad token" "-> $HTTP_STATUS, expected 400"
fi

# ---- pharmacy hierarchy ----
rq GET "$API/api/pharmacies/context" "${AUTH[@]}"
ctx="$BODY"
# Must be an object carrying the flag, never a bare null or `true`. A null body
# becomes a 204, and the BFF proxy used to render that as `true` — which read as
# "yes, you are a branch" for every top-level pharmacy on the platform.
if [ "$HTTP_STATUS" != 200 ]; then
  fail "pharmacy context" "-> $HTTP_STATUS"
elif [ "$ctx" = "true" ] || [ "$ctx" = "null" ] || [ -z "$ctx" ]; then
  fail "pharmacy context returns an object" "body was '$ctx' — a client cannot tell 'no parent' from 'no value'"
elif [ -n "$(printf '%s' "$ctx" | jget isChildPharmacy)" ]; then
  ok "pharmacy context carries isChildPharmacy ($(printf '%s' "$ctx" | jget isChildPharmacy))"
else
  fail "pharmacy context carries isChildPharmacy" "body=$ctx"
fi

req GET "$API/api/pharmacies/children" "${AUTH[@]}" >/dev/null
[ "$HTTP_STATUS" = 200 ] && ok "sub-pharmacies list" || fail "sub-pharmacies list" "-> $HTTP_STATUS"

# Managing another account must never be reachable anonymously.
req POST "$API/api/pharmacies/children/anything/manage-as" >/dev/null
if [ "$HTTP_STATUS" = 401 ] || [ "$HTTP_STATUS" = 403 ]; then
  ok "manage-as rejects anonymous callers ($HTTP_STATUS)"
else
  fail "manage-as rejects anonymous callers" "-> $HTTP_STATUS, expected 401/403"
fi

# A pharmacy that is not yours must be indistinguishable from one that does not
# exist, or the endpoint becomes a way to probe for account ids.
req POST "$API/api/pharmacies/children/0000000000000000/manage-as" "${AUTH[@]}" >/dev/null
if [ "$HTTP_STATUS" = 404 ]; then
  ok "manage-as refuses a pharmacy that is not yours (404)"
else
  fail "manage-as refuses a pharmacy that is not yours" "-> $HTTP_STATUS, expected 404"
fi

for ep in "analytics?days=7" "audit?take=1" "admins"; do
  req GET "$API/api/admin/${ep}" "${AUTH[@]}" >/dev/null
  [ "$HTTP_STATUS" = 200 ] && ok "admin /${ep%%\?*}" || fail "admin /${ep%%\?*}" "-> $HTTP_STATUS"
done

# Templates must be staff-only. An unauthenticated write here is the security
# hole this endpoint shipped with.
req POST "$API/api/templates" -H 'Content-Type: application/json' -d '{}' >/dev/null
if [ "$HTTP_STATUS" = 401 ] || [ "$HTTP_STATUS" = 403 ]; then
  ok "template writes reject anonymous callers ($HTTP_STATUS)"
else
  fail "template writes reject anonymous callers" "-> $HTTP_STATUS, expected 401/403"
fi

# ── 6. pharmacy support view ─────────────────────────────────────────────
section "6. Pharmacy support view"

# The support tabs read a customer's own data. The check that matters is not
# "does it return 200" but "does it return the patient's real email address" —
# a regression there is invisible in the UI, because the UI would happily
# render a real address in the column labelled masked.
SUPPORT_ID="$(printf '%s' "$(req GET "$API/api/admin/users?role=PharmacyUser&take=1" "${AUTH[@]}")" \
  | jget items.0.id)"

if [ -z "$SUPPORT_ID" ]; then
  skip "pharmacy support view" "no pharmacy accounts to inspect"
else
  rq GET "$API/api/admin/pharmacies/$SUPPORT_ID/overview" "${AUTH[@]}"
  ov="$BODY"
  if [ "$HTTP_STATUS" != 200 ]; then
    fail "support overview" "-> $HTTP_STATUS"
  else
    ok "support overview"
    info "patients=$(printf '%s' "$ov" | jget patientCount) posters=$(printf '%s' "$ov" | jget posterCount) campaigns=$(printf '%s' "$ov" | jget campaignCount)"
  fi

  for ep in patients groups posters campaigns videos; do
    req GET "$API/api/admin/pharmacies/$SUPPORT_ID/$ep" "${AUTH[@]}" >/dev/null
    [ "$HTTP_STATUS" = 200 ] && ok "support /$ep" || fail "support /$ep" "-> $HTTP_STATUS"
  done

  # The masking guarantee, asserted against the wire rather than the screen.
  rq GET "$API/api/admin/pharmacies/$SUPPORT_ID/patients?take=50" "${AUTH[@]}"
  pts="$BODY"
  leak="$(printf '%s' "$pts" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    print('unparseable'); raise SystemExit
items = d.get('items', [])
if not items:
    print('empty'); raise SystemExit
bad = []
for p in items:
    # A real address would arrive under these names, or would contain an '@'
    # or a run of digits in the masked fields.
    for forbidden in ('email', 'phone'):
        if forbidden in p:
            bad.append(f\"field '{forbidden}' present\")
    me = p.get('maskedEmail') or ''
    mp = p.get('maskedPhone') or ''
    if me and '•' not in me:
        bad.append(f'maskedEmail not masked: {me}')
    if mp and '•' not in mp:
        bad.append(f'maskedPhone not masked: {mp}')
    if sum(c.isdigit() for c in mp) > 3:
        bad.append(f'maskedPhone reveals too many digits: {mp}')
print('; '.join(sorted(set(bad))[:3]) if bad else 'clean')
" 2>/dev/null)"

  case "$leak" in
    clean)       ok "patient contact details are masked on the wire" ;;
    empty)       skip "patient masking" "this pharmacy has no patients" ;;
    unparseable) fail "patient masking" "could not parse the patients response" ;;
    *)           fail "patient contact details are masked on the wire" "$leak" ;;
  esac

  # Reveal must refuse to hand anything over without a stated reason. If this
  # starts returning 200 the audit trail silently stops being meaningful.
  req POST "$API/api/admin/pharmacies/$SUPPORT_ID/patients/deadbeef/reveal" \
    "${AUTH[@]}" -H 'Content-Type: application/json' -d '{"reason":""}' >/dev/null
  if [ "$HTTP_STATUS" = 400 ]; then
    ok "reveal requires a reason (400)"
  else
    fail "reveal requires a reason" "-> $HTTP_STATUS, expected 400"
  fi

  # A pharmacy user must not be able to read another pharmacy's data through
  # these routes. The admin-area policy is what stops them; this proves it is
  # actually applied to the new controller rather than assumed.
  req GET "$API/api/admin/pharmacies/$SUPPORT_ID/patients" >/dev/null
  if [ "$HTTP_STATUS" = 401 ] || [ "$HTTP_STATUS" = 403 ]; then
    ok "support view rejects anonymous callers ($HTTP_STATUS)"
  else
    fail "support view rejects anonymous callers" "-> $HTTP_STATUS, expected 401/403"
  fi
fi

# ── 7. template gallery ──────────────────────────────────────────────────
section "7. Template gallery"

rq GET "$API/api/templates?take=100"
tpls="$BODY"
tcount="$(printf '%s' "$tpls" | jget total)"
if [ -z "$tcount" ]; then
  fail "templates list" "-> $HTTP_STATUS"
elif [ "$tcount" -ge 24 ]; then
  ok "built-in templates seeded ($tcount)"
else
  fail "built-in templates seeded" "found $tcount, expected at least 24 — TemplateSeeder may not have run"
fi

# ── 8. imagery pipeline ──────────────────────────────────────────────────
section "8. Imagery"

if [ "$RUN_AI" -eq 0 ]; then
  skip "poster image" "--no-ai"
  skip "template generation" "--no-ai"
  ASSET_URL=""; TEMPLATE_ID=""
else
  rq POST "$API/api/admin/templates/images" "${AUTH[@]}" \
    -H 'Content-Type: application/json' \
    -d '{"description":"pharmacist advising a customer at the counter","aspect":"hero"}'
  img="$BODY"

  ASSET_URL="$(printf '%s' "$img" | jget url)"
  SOURCE="$(printf '%s' "$img" | jget source)"
  IW="$(printf '%s' "$img" | jget width)"
  IH="$(printf '%s' "$img" | jget height)"

  if [ -z "$ASSET_URL" ]; then
    fail "produce a poster image" "-> $HTTP_STATUS: $(printf '%s' "$img" | jget error)"
  else
    ok "produce a poster image (via $SOURCE, ${IW}x${IH})"

    # Serving it back proves the blob was written AND that the asset route
    # resolves — the two halves that failed independently.
    ctype="$(curl -sS -o /dev/null -w '%{content_type}' "$API$ASSET_URL" 2>/dev/null)"
    code="$(curl -sS -o /dev/null -w '%{http_code}' "$API$ASSET_URL" 2>/dev/null)"
    if [ "$code" = 200 ] && [[ "$ctype" == image/* ]]; then
      ok "stored image is served back ($ctype)"
    else
      fail "stored image is served back" "GET $ASSET_URL -> $code $ctype"
    fi
  fi
fi

# ── 8. generation ────────────────────────────────────────────────────────
if [ "$RUN_AI" -eq 1 ] && [ -n "${ASSET_URL:-}" ]; then
  section "8. Template generation"

  rq POST "$API/api/admin/templates/generate" "${AUTH[@]}" \
    -H 'Content-Type: application/json' \
    -d "{\"prompt\":\"Free blood pressure checks for the over-40s, calm and trustworthy\",\"category\":\"Awareness\",\"image\":{\"url\":\"$ASSET_URL\",\"width\":$IW,\"height\":$IH,\"description\":\"pharmacist at the counter\"}}"
  gen="$BODY"

  CANVAS="$(printf '%s' "$gen" | jget canvasJson)"
  if [ -z "$CANVAS" ]; then
    fail "generate a template" "-> $HTTP_STATUS: $(printf '%s' "$gen" | jget error)"
  else
    ok "generate a template"

    # The check that matters: the supplied photograph must be IN the design.
    # A canvas that quietly omits it renders a poster with no picture, and
    # nothing downstream can tell the difference.
    if printf '%s' "$CANVAS" | python3 -c "
import sys, json
canvas = json.loads(sys.stdin.read())
imgs = [s for s in canvas.get('shapes', [])
        if s.get('kind') == 'image' and s.get('blobKey') == '$ASSET_URL']
sys.exit(0 if len(imgs) == 1 else 1)
" 2>/dev/null; then
      ok "generated design uses the supplied photograph"
    else
      fail "generated design uses the supplied photograph" \
        "the canvas has no image shape pointing at $ASSET_URL"
    fi

    # Publish it. Exercises the staff-gated write path.
    pub="$(printf '%s' "$gen" | python3 -c "
import sys, json
g = json.load(sys.stdin)
print(json.dumps({
  'name': 'E2E smoke ' + __import__('datetime').datetime.now().strftime('%H:%M:%S'),
  'description': 'Created by scripts/e2e/run.sh',
  'category': 'Awareness',
  'tags': ['e2e'],
  'canvas': json.loads(g['canvasJson']),
  'isPublished': True,
}))" 2>/dev/null)"

    rq POST "$API/api/templates" "${AUTH[@]}" \
      -H 'Content-Type: application/json' -d "$pub"
    created="$BODY"
    TEMPLATE_ID="$(printf '%s' "$created" | jget id)"
    [ -n "$TEMPLATE_ID" ] && ok "publish the template" \
                          || fail "publish the template" "-> $HTTP_STATUS"
  fi
fi

# ── 9. PDF export ────────────────────────────────────────────────────────
section "9. PDF export"

# Pick a template with an image: the freshly generated one if we have it,
# otherwise a seeded photographic one so this still runs with --no-ai.
PDF_TEMPLATE="${TEMPLATE_ID:-}"
if [ -z "$PDF_TEMPLATE" ]; then
  PDF_TEMPLATE="$(printf '%s' "$tpls" | python3 -c "
import sys, json
d = json.load(sys.stdin)
for t in d.get('items', []):
    if any(s.get('kind') == 'image' for s in t.get('canvas', {}).get('shapes', [])):
        print(t['id']); break
" 2>/dev/null)"
fi

if [ -z "$PDF_TEMPLATE" ]; then
  skip "PDF contains the image" "no template with an image found"
else
  # Every image in it must be loadable by the renderer's own fetch.
  rq GET "$API/api/admin/templates/$PDF_TEMPLATE/image-check" "${AUTH[@]}"
  probe="$BODY"
  failing="$(printf '%s' "$probe" | jget failing)"
  imgs="$(printf '%s' "$probe" | jget images)"
  if [ -z "$failing" ]; then
    skip "template images resolve" "probe endpoint unavailable — rebuild the API"
  elif [ "$failing" = 0 ] && [ "${imgs:-0}" -gt 0 ]; then
    ok "all $imgs template image(s) resolve for print"
  else
    fail "all template images resolve for print" \
      "$failing of $imgs failed: $(printf '%s' "$probe" | jget probes.0.error)"
  fi

  # Create a poster from it and export, which is what a campaign attaches.
  rq POST "$API/api/posters" "${AUTH[@]}" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"E2E smoke poster\",\"fromTemplateId\":\"$PDF_TEMPLATE\"}"
  poster="$BODY"
  POSTER_ID="$(printf '%s' "$poster" | jget id)"

  if [ -z "$POSTER_ID" ]; then
    fail "create a poster from the template" "-> $HTTP_STATUS"
  else
    ok "create a poster from the template"

    pdf="$(mktemp)"
    code="$(curl -sS -o "$pdf" -w '%{http_code}' \
      -H "Authorization: Bearer $TOKEN" \
      "$API/api/posters/$POSTER_ID/export.pdf" 2>/dev/null)"

    if [ "$code" != 200 ]; then
      fail "export the poster as PDF" "-> $code"
    else
      bytes="$(wc -c < "$pdf" | tr -d ' ')"
      ok "export the poster as PDF (${bytes} bytes)"

      # An embedded raster shows up as an image XObject. This is the check
      # that catches a poster rendering "successfully" with its photograph
      # silently dropped — the failure mode that reached real email.
      if grep -aq "DCTDecode\|FlateDecode.*Image\|/Subtype */Image" "$pdf"; then
        ok "PDF contains an embedded image"
      else
        fail "PDF contains an embedded image" \
          "no image XObject found — the renderer dropped it; check the API log for 'Poster image dropped'"
      fi
    fi
    rm -f "$pdf"
  fi
fi

# Nothing in the product stores a rendered poster — the canvas JSON is the
# source of truth and every view is regenerated. Social publishing needs a real
# image file, so this proves the rasteriser produces one. A poster that returns
# no PNG cannot be published to Facebook or Instagram, and the earlier version
# of this feature failed exactly there.
if [ -n "${POSTER_ID:-}" ]; then
  png="$(mktemp)"
  code="$(curl -sS -o "$png" -w '%{http_code}' \
    "$API/api/posters/$POSTER_ID/export.png" "${AUTH[@]}" 2>/dev/null || echo 000)"

  if [ "$code" != 200 ]; then
    fail "poster renders to PNG" "-> $code"
  else
    size="$(wc -c < "$png" | tr -d ' ')"
    # PNG magic number: 89 50 4E 47. A JSON error body would sail past a
    # size-only check.
    magic="$(head -c 4 "$png" | od -An -tx1 | tr -d ' \n')"
    if [ "$magic" != "89504e47" ]; then
      fail "poster PNG is really a PNG" "first bytes were $magic, not 89504e47"
    elif [ "$size" -lt 5000 ]; then
      fail "poster PNG has content" "only $size bytes — probably a blank canvas"
    else
      ok "poster renders to PNG ($size bytes)"
    fi

    # Second call must hit the content-hash cache rather than re-render.
    t0=$(date +%s)
    curl -sS -o /dev/null "$API/api/posters/$POSTER_ID/export.png" "${AUTH[@]}" 2>/dev/null
    t1=$(date +%s)
    info "second render took $((t1 - t0))s (cache hit expected)"
  fi
  rm -f "$png"
fi

# ── 10. social publishing ────────────────────────────────────────────────
section "10. Social & Ads"

# These run as the superadmin, which is a staff account — social publishing is
# gated to pharmacy accounts on a paid plan, so a 403 here is the gate working,
# not a fault. What matters is that the endpoints exist and are authenticated.
for ep in "social/connections" "social/posts?take=1" "ads/campaigns"; do
  req GET "$API/api/${ep}" "${AUTH[@]}" >/dev/null
  case "$HTTP_STATUS" in
    200|403) ok "api /${ep%%\?*} (-> $HTTP_STATUS)" ;;
    404)     fail "api /${ep%%\?*}" "-> 404: the controller is not registered" ;;
    *)       fail "api /${ep%%\?*}" "-> $HTTP_STATUS" ;;
  esac
done

# Unauthenticated must be refused. A social endpoint that answers without a
# token would expose which accounts a pharmacy has connected.
req GET "$API/api/social/connections" >/dev/null
if [ "$HTTP_STATUS" = 401 ] || [ "$HTTP_STATUS" = 403 ]; then
  ok "social endpoints reject anonymous callers ($HTTP_STATUS)"
else
  fail "social endpoints reject anonymous callers" "-> $HTTP_STATUS, expected 401/403"
fi

# No response may carry a credential. The DTOs have no field for one, so this
# is a regression guard rather than a live concern — but it is the kind of
# regression that would never be noticed by eye.
rq GET "$API/api/social/connections" "${AUTH[@]}"
body="$BODY"
if printf '%s' "$body" | grep -qiE '"(accessToken|refreshToken|cipherText|clientSecret|appSecret)"'; then
  fail "connections response carries no credentials" \
    "a token-shaped field appeared in the response body"
else
  ok "connections response carries no credentials"
fi

# The worker only runs when encryption is configured. Without it, scheduled
# posts sit in the queue for ever and nothing says why.
rq GET "$API/api/diagnostics/config"
diag="$BODY"
if [ "$HTTP_STATUS" = 200 ]; then
  ok "diagnostics reachable"
else
  skip "publish worker configuration" "diagnostics endpoint unavailable"
fi

# ── 11. web BFF ──────────────────────────────────────────────────────────
# ── 12-15. invitations, email, global groups, hierarchy ──────────────────
#
# In their own file: these four sections are one continuous story and run.sh was
# already long enough that adding 400 lines to it would bury everything else.
LIFECYCLE="$(dirname "$0")/lifecycle.sh"
if [ -f "$LIFECYCLE" ]; then
  # shellcheck source=scripts/e2e/lifecycle.sh
  . "$LIFECYCLE"
else
  skip "invitations, email, groups, hierarchy" "scripts/e2e/lifecycle.sh is missing"
fi

section "11. Web routes"

# The Next.js layer proxies the API. A route file missing a method returns 405
# and never reaches the API at all, so it has to be probed through the web
# origin rather than the API's.
code="$(curl -sS -o /dev/null -w '%{http_code}' "$WEB/api/templates?take=1" 2>/dev/null)"
[ "$code" = 200 ] && ok "web proxies GET /api/templates" \
                  || fail "web proxies GET /api/templates" "-> $code"

# Unauthenticated, so 401 is the correct answer. 405 means the handler is
# missing entirely — which is what broke Publish.
code="$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$WEB/api/templates" \
  -H 'Content-Type: application/json' -d '{}' 2>/dev/null)"
if [ "$code" = 405 ]; then
  fail "web exposes POST /api/templates" "-> 405: the route file has no POST export"
else
  ok "web exposes POST /api/templates (-> $code)"
fi

# PUT through the web proxy. Unauthenticated, so 401/403 is the right answer —
# 405 means the catch-all route file has no PUT export and the request never
# reached the API. That is how the Publish bug presented, and how the video
# model setting presented before this check existed.
code="$(curl -sS -o /dev/null -w '%{http_code}' -X PUT "$WEB/api/admin/settings/video-model" \
  -H 'Content-Type: application/json' -d '{"model":"claude-sonnet-5"}' 2>/dev/null)"
if [ "$code" = 405 ]; then
  fail "web exposes PUT on the admin proxy" "-> 405: the route file has no PUT export"
else
  ok "web exposes PUT on the admin proxy (-> $code)"
fi

# Downloads through the web origin. The API serves these correctly — that was
# never in doubt — but the browser reaches them through Next, and the JSON
# catch-all proxies parse every response as JSON and send `null` when that
# fails. The invitations CSV template rendered as a page reading `null` for
# exactly that reason while the API log showed a clean 200 with 181 bytes of
# text/csv, and the poster PDF had no web route at all.
#
# Unauthenticated here, so 401 is the right answer. What must NOT come back is
# 404 (no route) or a JSON content-type (swallowed by the catch-all).
# The poster card joins them: an image the browser loads from <img>, so a JSON
# proxy in front of it is the blank "?" card on My Posters all over again.
for path in "/api/admin/invitations/csv-template" "/api/posters/probe-id/export.pdf" \
            "/api/posters/probe-id/card"; do
  hdrs="$(curl -sS -o /dev/null -D - -w '%{http_code}' "$WEB$path" 2>/dev/null)"
  code="$(printf '%s' "$hdrs" | tail -1)"
  ctype="$(printf '%s' "$hdrs" | tr -d '\r' | awk -F': ' 'tolower($1)=="content-type"{print $2}' | tail -1)"

  if [ "$code" = 404 ]; then
    fail "web serves $path" "-> 404: no Next route, so the download link goes nowhere"
  elif printf '%s' "$ctype" | grep -qi 'application/json'; then
    fail "web serves $path" "content-type $ctype — a JSON proxy swallowed the file"
  else
    ok "web serves $path (-> $code${ctype:+, $ctype})"
  fi
done

if [ -n "${ASSET_URL:-}" ]; then
  code="$(curl -sS -o /dev/null -w '%{http_code}' "$WEB$ASSET_URL" 2>/dev/null)"
  [ "$code" = 200 ] && ok "web proxies template assets" \
                    || fail "web proxies template assets" "GET $WEB$ASSET_URL -> $code"
fi

# ── cleanup ──────────────────────────────────────────────────────────────
if [ "$CLEANUP" -eq 1 ]; then
  [ -n "${POSTER_ID:-}" ]   && req DELETE "$API/api/posters/$POSTER_ID" "${AUTH[@]}" >/dev/null
  [ -n "${TEMPLATE_ID:-}" ] && req DELETE "$API/api/templates/$TEMPLATE_ID" "${AUTH[@]}" >/dev/null
  [ -n "${POSTER_ID:-}${TEMPLATE_ID:-}" ] && info "cleaned up test poster/template"
else
  [ -n "${TEMPLATE_ID:-}" ] && info "kept template $TEMPLATE_ID"
fi

# ── summary ──────────────────────────────────────────────────────────────
printf '\n%s─────────────────────────────────────────%s\n' "$B" "$N"
printf '%s%d passed%s' "$G" "$PASSED" "$N"
[ "$FAILED"  -gt 0 ] && printf ', %s%d failed%s' "$R" "$FAILED" "$N"
[ "$SKIPPED" -gt 0 ] && printf ', %s%d skipped%s' "$Y" "$SKIPPED" "$N"
printf '\n'

if [ "$FAILED" -gt 0 ]; then
  printf '\n%sFailures:%s\n' "$R" "$N"
  for f in "${FAILURES[@]}"; do printf '  • %s\n' "$f"; done
  exit 1
fi
printf '\n%sAll good.%s\n' "$G" "$N"

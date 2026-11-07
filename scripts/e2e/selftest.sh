#!/usr/bin/env bash
#
# Tests the end-to-end test.
#
# lifecycle.sh cannot be run without a live stack, which means its assertions
# are normally only exercised when everything already works — and an assertion
# that has never seen a failure is an assertion nobody knows is wired up. This
# harness replaces `req` with a table of canned responses and runs lifecycle.sh
# twice:
#
#   HEALTHY  a backend that behaves correctly        -> expect zero failures
#   BROKEN   a backend with four specific defects    -> expect each one named
#
# The BROKEN run is the point. It encodes the failures that actually matter:
#
#   · Messaging:UseMock left on, so nothing is ever sent
#   · an invited account that can sign in before activating
#   · a delegated token that can mint another delegated token
#   · detaching a branch that disables the account instead of freeing it
#
# If a future edit breaks one of those checks, this fails and says which.
#
# Run: ./scripts/e2e/selftest.sh
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SCENARIO="${1:-}"

# ── the harness itself ───────────────────────────────────────────────────
if [ -z "$SCENARIO" ]; then
  overall=0

  run_scenario() {
    local name="$1"; shift
    local out; out="$("$0" "$name" 2>&1)"
    local p f
    p="$(printf '%s' "$out" | grep -c '^  PASS' || true)"
    f="$(printf '%s' "$out" | grep -c '^  FAIL' || true)"
    printf '\n\033[1m%s\033[0m — %s passed, %s failed\n' "$name" "$p" "$f"
    printf '%s\n' "$out" | grep '^  FAIL' | sed 's/^/    /'
    SCENARIO_PASSED="$p"; SCENARIO_FAILED="$f"; SCENARIO_OUT="$out"
  }

  expect_caught() {
    if printf '%s' "$SCENARIO_OUT" | grep -q "FAIL.*$1"; then
      printf '  \033[32m✓\033[0m caught: %s\n' "$1"
    else
      printf '  \033[31m✗\033[0m NOT caught: %s\n' "$1"
      overall=1
    fi
  }

  run_scenario HEALTHY
  if [ "$SCENARIO_FAILED" -eq 0 ]; then
    printf '  \033[32m✓\033[0m a correct backend produces no failures\n'
  else
    printf '  \033[31m✗\033[0m a correct backend produced %s failure(s) — false alarms above\n' "$SCENARIO_FAILED"
    overall=1
  fi
  [ "$SCENARIO_PASSED" -gt 20 ] || {
    printf '  \033[31m✗\033[0m only %s assertions ran; the script exited early\n' "$SCENARIO_PASSED"
    overall=1
  }

  run_scenario BROKEN
  expect_caught "email is not in mock mode"
  expect_caught "an invited account cannot sign in yet"
  expect_caught "a delegated token cannot mint another"
  expect_caught "a detached branch keeps working independently"

  printf '\n'
  if [ "$overall" -eq 0 ]; then
    printf '\033[32mThe end-to-end assertions are wired up and can fail.\033[0m\n'
  else
    printf '\033[31mSome assertions did not behave as intended.\033[0m\n'
  fi
  exit "$overall"
fi

# ── stubs matching run.sh's contract ─────────────────────────────────────
PASSED=0; FAILED=0; SKIPPED=0
declare -a FAILURES=()
R=; G=; Y=; B=; D=; N=

section() { printf '\n== %s ==\n' "$1"; }
ok()   { PASSED=$((PASSED+1)); printf '  PASS  %s\n' "$1"; return 0; }
fail() { FAILED=$((FAILED+1)); FAILURES+=("$1"); printf '  FAIL  %s\n' "$1"; return 0; }
skip() { SKIPPED=$((SKIPPED+1)); printf '  SKIP  %s\n' "$1"; return 0; }
info() { printf '        %s\n' "$1"; }

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

API="http://stub"
AUTH=(-H "Authorization: Bearer stub")
CLEANUP=0
HTTP_STATUS=000
BROKEN=0
[ "$SCENARIO" = "BROKEN" ] && BROKEN=1

# Marker the stub uses to recognise a delegated bearer token.
DELEG_MARK="DELEGATED-TOKEN"

# ── the fake backend ─────────────────────────────────────────────────────
# Keyed on method + path. Echoes a body and records the status.
#
# The status goes through a file rather than a variable, because `rq` captures
# this function's stdout in a command substitution — the very subshell problem
# this harness exposed in run.sh. A file crosses that boundary; a variable does
# not. State the stub mutates (ACTIVATED, BRANCH_MADE, …) has the same problem,
# so it lives in files too.
STUB_DIR="$(mktemp -d)"
trap 'rm -rf "$STUB_DIR"' EXIT
flag()    { [ -f "$STUB_DIR/$1" ]; }
setflag() { : > "$STUB_DIR/$1"; }

req() {
  local method="$1" url="$2"; shift 2
  local path="${url#http://stub}"
  local is_delegated=0
  local a
  for a in "$@"; do
    case "$a" in *"$DELEG_MARK"*) is_delegated=1 ;; esac
  done

  HTTP_STATUS=200

  case "$method $path" in
    "GET /api/admin/diagnostics/email")
      if [ "$BROKEN" -eq 1 ]; then
        echo '{"provider":"mailjet","useMock":true,"hasCredentials":true,"fromAddress":"info@x.com","sampleActivationLink":"https://app.example.com/activate?token=x","sandboxMode":false,"environment":"Development","wouldSend":false,"problems":["UseMock is on"]}'
      else
        echo '{"provider":"mailjet","useMock":false,"hasCredentials":true,"fromAddress":"info@x.com","sampleActivationLink":"https://app.example.com/activate?token=x","sandboxMode":false,"environment":"Development","wouldSend":true,"problems":[]}'
      fi ;;

    "POST /api/admin/invitations")
      echo '{"email":"x","success":true,"userId":"INVITED1","error":null,"emailSent":true}' ;;

    "GET /api/admin/invitations"*)
      echo '{"items":[{"userId":"INVITED1","status":"Invited"}],"total":1}' ;;

    "POST /api/auth/login")
      # Which account is logging in, and has it activated?
      local body=""
      for a in "$@"; do case "$a" in *"@example.test"*) body="$a" ;; esac; done
      case "$body" in
        *-invited@example.test*)
          # The defect: an invited account that signs in before activating.
          if [ "$BROKEN" -eq 1 ] || flag ACTIVATED; then
            echo '{"accessToken":"PARENT-TOKEN"}'
          else
            HTTP_STATUS=401; echo '{"error":"Invalid credentials."}'
          fi ;;
        *-branch@example.test*)
          # The defect: detaching disables the account.
          if [ "$BROKEN" -eq 1 ] && flag DETACHED; then
            HTTP_STATUS=401; echo '{"error":"Account disabled."}'
          else
            echo '{"accessToken":"BRANCH-TOKEN"}'
          fi ;;
        *) HTTP_STATUS=401; echo '{"error":"no"}' ;;
      esac ;;

    "GET /api/invitations/preview"*)
      case "$path" in
        *not-a-real-token*) HTTP_STATUS=400; echo '{"error":"This link is not valid."}' ;;
        *) echo "{\"email\":\"${INVITE_EMAIL:-x}\",\"displayName\":\"E2E\"}" ;;
      esac ;;

    "POST /api/admin/invitations/INVITED1/activation-link")
      echo '{"link":"https://app.example.com/activate?token=RAWTOKEN1"}' ;;
    "POST /api/admin/invitations/BRANCH1/activation-link")
      echo '{"link":"https://app.example.com/activate?token=RAWTOKEN2"}' ;;

    "POST /api/invitations/accept")
      local tok=""
      for a in "$@"; do case "$a" in *RAWTOKEN*) tok="$a" ;; esac; done
      case "$tok" in
        *RAWTOKEN1*)
          if flag ACTIVATED; then
            HTTP_STATUS=400; echo '{"error":"This link has already been used."}'
          else
            setflag ACTIVATED; echo '{"activated":true}'
          fi ;;
        *RAWTOKEN2*) setflag BRANCH_ACTIVE; echo '{"activated":true}' ;;
        *) HTTP_STATUS=400; echo '{"error":"bad token"}' ;;
      esac ;;

    "POST /api/admin/invitations/INVITED1/resend")
      # Already activated by this point in the run.
      HTTP_STATUS=400; echo '{"error":"That account is already activated."}' ;;

    "POST /api/admin/global-groups")
      if flag GG_MADE; then
        HTTP_STATUS=409; echo '{"error":"A general group with that name already exists."}'
      else
        setflag GG_MADE; echo '{"id":"GG1","name":"g","isGlobal":true}'
      fi ;;
    "DELETE /api/admin/global-groups/GG1") HTTP_STATUS=204; echo '' ;;
    "GET /api/admin/global-groups")
      HTTP_STATUS=403; echo '{"error":"SuperAdmin only."}' ;;

    "GET /api/patient-groups")
      echo "[{\"id\":\"GG1\",\"name\":\"${GG_NAME:-g}\",\"isGlobal\":true}]" ;;
    "PUT /api/patient-groups/GG1"|"DELETE /api/patient-groups/GG1")
      HTTP_STATUS=403; echo '{"error":"A general group is managed by the platform."}' ;;

    "GET /api/pharmacies/context")
      local who="branch"
      for a in "$@"; do case "$a" in *PARENT-TOKEN*) who="parent" ;; esac; done
      if [ "$who" = "parent" ]; then
        echo '{"isChildPharmacy":false,"parent":null}'
      else
        echo '{"isChildPharmacy":true,"parent":{"userId":"INVITED1","pharmacyName":"E2E Invited Pharmacy"}}'
      fi ;;

    "POST /api/pharmacies/children")
      if [ "$is_delegated" -eq 1 ]; then
        HTTP_STATUS=403; echo '{"error":"Not while managing another pharmacy."}'
      else
        local caller="parent"
        for a in "$@"; do case "$a" in *BRANCH-TOKEN*) caller="branch" ;; esac; done
        if [ "$caller" = "branch" ]; then
          HTTP_STATUS=400; echo '{"error":"A sub-pharmacy cannot have its own."}'
        else
          setflag BRANCH_MADE
          echo '{"userId":"BRANCH1","email":"b","pharmacyName":"E2E Branch","status":"Invited","isActive":true}'
        fi
      fi ;;

    "GET /api/pharmacies/children")
      if flag BRANCH_MADE && ! flag DETACHED; then
        echo '[{"userId":"BRANCH1","pharmacyName":"E2E Branch","status":"Invited"}]'
      else
        echo '[]'
      fi ;;

    "POST /api/pharmacies/children/BRANCH1/manage-as")
      if [ "$is_delegated" -eq 1 ]; then
        # The defect: delegation chains.
        if [ "$BROKEN" -eq 1 ]; then
          echo "{\"token\":\"$DELEG_MARK-2\",\"expiresAtUtc\":\"2030-01-01T00:00:00Z\"}"
        else
          HTTP_STATUS=403; echo '{"error":"Already managing another pharmacy."}'
        fi
      elif ! flag BRANCH_ACTIVE; then
        HTTP_STATUS=400; echo '{"error":"They need to activate their account first."}'
      else
        echo "{\"token\":\"$DELEG_MARK\",\"expiresAtUtc\":\"2030-01-01T00:00:00Z\",\"childUserId\":\"BRANCH1\"}"
      fi ;;

    "POST /api/pharmacies/children/INVITED1/manage-as")
      HTTP_STATUS=404; echo '{"error":"Not found."}' ;;

    "DELETE /api/pharmacies/children/BRANCH1")
      if [ "$is_delegated" -eq 1 ]; then
        HTTP_STATUS=403; echo '{"error":"Not while managing another pharmacy."}'
      else
        setflag DETACHED; HTTP_STATUS=204; echo ''
      fi ;;

    "GET /api/me")
      if [ "$is_delegated" -eq 1 ]; then
        echo "{\"email\":\"${BRANCH_EMAIL:-b}\"}"
      else
        echo "{\"email\":\"${INVITE_EMAIL:-p}\"}"
      fi ;;

    "POST /api/admin/users/"*"/deactivate") echo '{}' ;;

    *) HTTP_STATUS=404; echo "{\"error\":\"stub has no route for $method $path\"}" ;;
  esac
}

# `rq` as lifecycle.sh now uses it: body in $BODY, status in $HTTP_STATUS, both
# visible to the caller.
rq() {
  BODY="$(req "$@"; printf '%s' "$HTTP_STATUS" > "$STUB_DIR/status")"
  HTTP_STATUS="$(cat "$STUB_DIR/status")"
}

# ── run the thing under test ─────────────────────────────────────────────
# shellcheck source=scripts/e2e/lifecycle.sh
. "$HERE/lifecycle.sh"

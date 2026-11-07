#!/usr/bin/env bash
#
# Finds the seeded demo accounts on a deployment and deactivates them.
#
# DevUserSeeder creates 12 demo pharmacies and 2 demo admins, all at
# @example.test, all sharing the password TestPass123!. It is guarded twice —
# it returns unless the environment is Development, and again if
# DevData:SeedUsers is false — so if these exist on production, the deployment
# is running with ASPNETCORE_ENVIRONMENT=Development. Removing the accounts
# without fixing that leaves the door open on the next fresh database.
#
# Why deactivate rather than delete:
#
#   · There is no delete endpoint for a user, deliberately. A pharmacy row is
#     referenced by patients, posters and campaigns, and this is a healthcare
#     system.
#   · The seeder is idempotent *by email*, so a deactivated row is left alone on
#     reboot. A hard delete would be recreated on the next restart for as long
#     as the environment stays Development.
#   · It goes through the API, so every change lands in the audit log against
#     the SuperAdmin who made it. A mongosh session leaves no such trace.
#
# Everything here is read-only until you pass --apply.
#
# Usage:
#   API_URL=https://your-api ./scripts/demo-accounts.sh                # report
#   API_URL=https://your-api ./scripts/demo-accounts.sh --apply        # act
#
#   SUPERADMIN_EMAIL / SUPERADMIN_PASSWORD to authenticate.
#
set -uo pipefail

APPLY=0
FORCE=0
for arg in "$@"; do
  case "$arg" in
    --apply) APPLY=1 ;;
    --force) FORCE=1 ;;
    -h|--help) sed -n '2,32p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

# No default. Pointing a destructive script at localhost by accident is
# harmless; pointing it at the wrong *remote* because a default filled itself in
# is not. The address has to be stated.
API="${API_URL:-}"
if [ -z "$API" ]; then
  echo "Set API_URL to the deployment you mean to act on. There is no default." >&2
  exit 2
fi

EMAIL="${SUPERADMIN_EMAIL:-}"
PASSWORD="${SUPERADMIN_PASSWORD:-}"
if [ -z "$EMAIL" ] || [ -z "$PASSWORD" ]; then
  echo "Set SUPERADMIN_EMAIL and SUPERADMIN_PASSWORD." >&2
  exit 2
fi

# The only accounts this script will ever touch. Checked again per account
# immediately before acting, so a bug in the listing cannot widen it.
DEMO_SUFFIX="@example.test"

if [ -t 1 ]; then
  R=$'\e[31m'; G=$'\e[32m'; Y=$'\e[33m'; B=$'\e[1m'; D=$'\e[2m'; N=$'\e[0m'
else
  R=; G=; Y=; B=; D=; N=
fi

BODY=""
rq() {
  local method="$1" url="$2"; shift 2
  local tmp; tmp="$(mktemp)"
  HTTP_STATUS="$(curl -sS -o "$tmp" -w '%{http_code}' -X "$method" "$url" "$@" 2>/dev/null || echo 000)"
  BODY="$(cat "$tmp")"; rm -f "$tmp"
}

jget() { python3 -c "
import sys, json
try: d = json.load(sys.stdin)
except Exception: sys.exit(0)
for k in '$1'.split('.'):
    if isinstance(d, list):
        try: d = d[int(k)]
        except Exception: sys.exit(0)
    elif isinstance(d, dict): d = d.get(k)
    else: sys.exit(0)
    if d is None: sys.exit(0)
print(d if not isinstance(d,(dict,list)) else json.dumps(d))
" 2>/dev/null; }

printf '%sDemo account cleanup%s\n' "$B" "$N"
printf '  target: %s\n' "$API"
[ "$APPLY" -eq 1 ] && printf '  mode:   %sAPPLY — accounts will be deactivated%s\n' "$Y" "$N" \
                   || printf '  mode:   %sreport only%s (pass --apply to act)\n' "$D" "$N"

# ── which environment is this? ───────────────────────────────────────────
# The reason the accounts exist in the first place, so it is worth stating
# before anything else.
rq GET "$API/api/diagnostics/config"
if [ "$HTTP_STATUS" = 200 ]; then
  ENVNAME="$(printf '%s' "$BODY" | jget aspNetCoreEnv)"
  printf '  env:    %s\n' "${ENVNAME:-unknown}"
  if [ "$ENVNAME" = "Development" ]; then
    printf '\n%sASPNETCORE_ENVIRONMENT is Development on this deployment.%s\n' "$Y" "$N"
    printf '  That is why the demo accounts exist. Deactivating them is worth doing,\n'
    printf '  but until this changes the deployment also has Swagger and the\n'
    printf '  diagnostics endpoints open, and a fresh database would be seeded again.\n'
    printf '  Set DevData__SeedUsers=false now, and ASPNETCORE_ENVIRONMENT=Production\n'
    printf '  as the real fix.\n'
  fi
else
  printf '  env:    %s(diagnostics endpoint not reachable — %s)%s\n' "$D" "$HTTP_STATUS" "$N"
fi

# ── sign in ──────────────────────────────────────────────────────────────
rq POST "$API/api/auth/login" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}"
TOKEN="$(printf '%s' "$BODY" | jget accessToken)"
if [ -z "$TOKEN" ]; then
  printf '\n%sCould not sign in as %s (HTTP %s).%s\n' "$R" "$EMAIL" "$HTTP_STATUS" "$N" >&2
  exit 1
fi
AUTH=(-H "Authorization: Bearer $TOKEN")

# ── find them ────────────────────────────────────────────────────────────
rq GET "$API/api/admin/users?search=$DEMO_SUFFIX&take=200" "${AUTH[@]}"
if [ "$HTTP_STATUS" != 200 ]; then
  printf '\n%sCould not list users (HTTP %s).%s\n' "$R" "$HTTP_STATUS" "$N" >&2
  exit 1
fi

# Filter in python rather than trusting the search: `search` matches substrings
# across several fields, and this script must only ever see addresses that
# genuinely end in the demo domain.
ACCOUNTS="$(printf '%s' "$BODY" | python3 -c "
import sys, json
d = json.load(sys.stdin)
for u in d.get('items', []):
    if str(u.get('email','')).lower().endswith('$DEMO_SUFFIX'):
        print('\t'.join([
            u['id'], u['email'], str(u.get('role')),
            str(u.get('isActive')), str(u.get('tier')),
            str(u.get('lastLoginAtUtc') or '-'),
        ]))
")"

if [ -z "$ACCOUNTS" ]; then
  printf '\n%sNo accounts at %s on this deployment. Nothing to do.%s\n' "$G" "$DEMO_SUFFIX" "$N"
  exit 0
fi

TOTAL="$(printf '%s\n' "$ACCOUNTS" | grep -c .)"
printf '\n%s%s account(s) at %s%s\n\n' "$B" "$TOTAL" "$DEMO_SUFFIX" "$N"

# ── what do they own? ────────────────────────────────────────────────────
#
# These accounts share a published password, so somebody may have signed into
# one and done real work in it. An account holding real records is not test
# data any more, and deactivating it would take a working pharmacy offline —
# so it is reported and skipped rather than swept up with the rest.
declare -a SAFE=() HAS_DATA=() ADMINS=()

while IFS=$'\t' read -r id email role active tier lastlogin; do
  [ -z "$id" ] && continue

  if [ "$role" = "Admin" ] || [ "$role" = "SuperAdmin" ]; then
    rq GET "$API/api/admin/admins" "${AUTH[@]}"
    managed="$(printf '%s' "$BODY" | python3 -c "
import sys, json
for a in json.load(sys.stdin):
    if a.get('id') == '$id': print(a.get('managedUserCount', 0)); break
" 2>/dev/null)"
    managed="${managed:-0}"

    printf '  %-34s %-11s active=%-5s manages=%s\n' "$email" "$role" "$active" "$managed"
    if [ "$managed" -gt 0 ] 2>/dev/null; then
      printf '      %s^ still the account manager for %s pharmacy account(s)%s\n' "$Y" "$managed" "$N"
      printf '        Reassign those first, or they lose their manager.\n'
      HAS_DATA+=("$id|$email|manages $managed pharmacies")
    else
      ADMINS+=("$id|$email")
    fi
    continue
  fi

  rq GET "$API/api/admin/pharmacies/$id/overview" "${AUTH[@]}"
  if [ "$HTTP_STATUS" != 200 ]; then
    printf '  %-34s %-11s active=%-5s %s(overview -> %s)%s\n' \
      "$email" "$role" "$active" "$D" "$HTTP_STATUS" "$N"
    HAS_DATA+=("$id|$email|could not read its data")
    continue
  fi

  pat="$(printf '%s' "$BODY" | jget patientCount)"
  pos="$(printf '%s' "$BODY" | jget posterCount)"
  cam="$(printf '%s' "$BODY" | jget campaignCount)"
  vid="$(printf '%s' "$BODY" | jget videoCount)"
  owned=$(( ${pat:-0} + ${pos:-0} + ${cam:-0} + ${vid:-0} ))

  printf '  %-34s %-11s active=%-5s tier=%-7s patients=%s posters=%s campaigns=%s videos=%s\n' \
    "$email" "$role" "$active" "$tier" "${pat:-0}" "${pos:-0}" "${cam:-0}" "${vid:-0}"

  if [ "$owned" -gt 0 ]; then
    printf '      %s^ holds %s record(s) — somebody has used this account%s\n' "$Y" "$owned" "$N"
    [ "$lastlogin" != "-" ] && printf '        last sign-in: %s\n' "$lastlogin"
    HAS_DATA+=("$id|$email|holds $owned records")
  else
    SAFE+=("$id|$email")
  fi
done <<< "$ACCOUNTS"

# ── summary ──────────────────────────────────────────────────────────────
printf '\n%s─────────────────────────────────────────%s\n' "$B" "$N"
printf 'empty and safe to deactivate: %s\n' "$(( ${#SAFE[@]} + ${#ADMINS[@]} ))"
printf 'needs a decision first:       %s\n' "${#HAS_DATA[@]}"

if [ "${#HAS_DATA[@]}" -gt 0 ]; then
  printf '\n%sSkipped:%s\n' "$Y" "$N"
  for entry in "${HAS_DATA[@]}"; do
    IFS='|' read -r _ e why <<< "$entry"
    printf '  · %-34s %s\n' "$e" "$why"
  done
  printf '\n  These are not obviously test data any more. Deal with each on its own\n'
  printf '  merits — reassign, export, or confirm the records are junk — then\n'
  printf '  re-run with --force to include them.\n'
fi

if [ "$APPLY" -eq 0 ]; then
  printf '\n%sReport only. Nothing was changed.%s\n' "$D" "$N"
  printf 'Re-run with --apply to deactivate the %s empty account(s).\n' \
    "$(( ${#SAFE[@]} + ${#ADMINS[@]} ))"
  exit 0
fi

# ── act ──────────────────────────────────────────────────────────────────
TARGETS=("${SAFE[@]}" "${ADMINS[@]}")
[ "$FORCE" -eq 1 ] && for entry in "${HAS_DATA[@]}"; do
  IFS='|' read -r i e _ <<< "$entry"; TARGETS+=("$i|$e")
done

printf '\n%sDeactivating %s account(s)%s\n' "$B" "${#TARGETS[@]}" "$N"
DONE=0; FAILED=0

for entry in "${TARGETS[@]}"; do
  IFS='|' read -r id email <<< "$entry"

  # The guard that matters. Checked here, immediately before the call, so it
  # holds regardless of how the list was built.
  case "$email" in
    *"$DEMO_SUFFIX")
      : ;;
    *)
      printf '  %sREFUSED%s %s — not a %s address\n' "$R" "$N" "$email" "$DEMO_SUFFIX"
      FAILED=$((FAILED+1)); continue ;;
  esac

  rq POST "$API/api/admin/users/$id/deactivate" "${AUTH[@]}"
  if [ "$HTTP_STATUS" = 200 ] || [ "$HTTP_STATUS" = 204 ]; then
    printf '  %s✓%s %s\n' "$G" "$N" "$email"; DONE=$((DONE+1))
  else
    printf '  %s✗%s %s (HTTP %s) %s\n' "$R" "$N" "$email" "$HTTP_STATUS" \
      "$(printf '%s' "$BODY" | jget error)"
    FAILED=$((FAILED+1))
  fi
done

printf '\n%s%s deactivated%s' "$G" "$DONE" "$N"
[ "$FAILED" -gt 0 ] && printf ', %s%s failed%s' "$R" "$FAILED" "$N"
printf '\n'

printf '\n%sStill to do, and the part that actually closes this:%s\n' "$B" "$N"
printf '  1. DevData__SeedUsers=false        — stops the seeder outright\n'
printf '  2. ASPNETCORE_ENVIRONMENT=Production — also closes Swagger and\n'
printf '     /api/diagnostics/*, and is the setting that let this happen\n'
printf '  3. Rotate the Mailjet keys committed in appsettings.json\n'

[ "$FAILED" -gt 0 ] && exit 1
exit 0

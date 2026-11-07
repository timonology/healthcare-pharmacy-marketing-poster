# shellcheck shell=bash
#
# Sections 12–15: invitations, email, global groups, pharmacy hierarchy.
#
# Sourced by run.sh, which owns the helpers (req/jget/ok/fail/skip/info), $API,
# $AUTH and $CLEANUP. Split into its own file because run.sh was already 700
# lines and these four sections are one coherent story: an account that does not
# exist yet becomes a working pharmacy with a parent, a branch and a shared
# group.
#
# Everything here creates real records. Names are prefixed E2E_TAG so a failed
# run leaves something identifiable rather than plausible-looking rubbish, and
# cleanup at the end removes them unless --keep was passed.

E2E_TAG="e2e-$(date +%s)"
E2E_PASSWORD='E2e-Test-Pass-123!'

# Accounts created here, cleaned up at the end.
declare -a CREATED_USERS=()

# ══════════════════════════════════════════════════════════════════════════
section "12. Email configuration"
# ══════════════════════════════════════════════════════════════════════════
#
# Email is the only part of this system whose failures are all silent. The
# invitation is created, the row says "Invited", the log says a send was
# attempted, and the pharmacist hears nothing. Three settings each cause that
# independently, so each is checked separately rather than inferred from one
# happy-path send.

rq GET "$API/api/admin/diagnostics/email" "${AUTH[@]}"
mail="$BODY"
if [ "$HTTP_STATUS" != 200 ]; then
  fail "email diagnostics" "-> $HTTP_STATUS (is the API rebuilt? this endpoint is new)"
  MAIL_WOULD_SEND=unknown
else
  MAIL_WOULD_SEND="$(printf '%s' "$mail" | jget wouldSend)"
  provider="$(printf '%s' "$mail" | jget provider)"
  usemock="$(printf '%s' "$mail" | jget useMock)"
  fromaddr="$(printf '%s' "$mail" | jget fromAddress)"
  samplelink="$(printf '%s' "$mail" | jget sampleActivationLink)"
  sandbox="$(printf '%s' "$mail" | jget sandboxMode)"
  ok "email diagnostics reachable (provider=$provider)"
  info "from=$fromaddr"
  info "activation links look like: $samplelink"

  # UseMock defaults to TRUE. An environment that simply omits the setting
  # sends nothing at all and logs instead — the single most likely reason for
  # "the invitation never arrived".
  if [ "$usemock" = "True" ] || [ "$usemock" = "true" ]; then
    fail "email is not in mock mode" \
      "Messaging:UseMock is on — every email is logged and none is sent. Set Messaging__UseMock=false."
  else
    ok "email is not in mock mode"
  fi

  if [ "$sandbox" = "True" ] || [ "$sandbox" = "true" ]; then
    fail "Mailjet sandbox mode is off" \
      "SandboxMode validates each message and reports success without delivering it"
  else
    ok "Mailjet sandbox mode is off"
  fi

  # A localhost activation link is useless to whoever receives it. Correct on a
  # developer machine, fatal anywhere else — so this reports rather than fails
  # when the API says it is running in Development.
  apienv="$(printf '%s' "$mail" | jget environment)"
  case "$samplelink" in
    *localhost*|*127.0.0.1*)
      if [ "$apienv" = "Development" ]; then
        skip "activation links are publicly reachable" "localhost is correct in Development"
      else
        fail "activation links are publicly reachable" \
          "links point at localhost on $apienv — nobody who receives one can use it. Set Public__WebBaseUrl."
      fi
      ;;
    https://*) ok "activation links use a public https address" ;;
    http://*)
      if [ "$apienv" = "Development" ]; then
        ok "activation links resolve ($samplelink)"
      else
        fail "activation links use https" "links are plain http on $apienv — the token travels in the URL"
      fi
      ;;
    *) fail "activation links look like URLs" "got '$samplelink'" ;;
  esac

  # Anything the API itself flagged.
  probs="$(printf '%s' "$mail" | jget problems)"
  if [ -n "$probs" ] && [ "$probs" != "[]" ]; then
    info "API reports: $probs"
  fi

  if [ "$MAIL_WOULD_SEND" = "True" ] || [ "$MAIL_WOULD_SEND" = "true" ]; then
    ok "email is configured to actually send"
  else
    fail "email is configured to actually send" \
      "invitations will be created but never delivered"
  fi
fi

# The one check that proves delivery rather than configuration. Opt-in: it puts
# a real message in a real inbox, and a suite that mails somebody on every run
# is a suite people stop running.
if [ -n "${E2E_EMAIL_TO:-}" ]; then
  rq POST "$API/api/admin/diagnostics/email/test" "${AUTH[@]}" \
    -H 'Content-Type: application/json' -d "{\"to\":\"$E2E_EMAIL_TO\"}"
  send="$BODY"
  accepted="$(printf '%s' "$send" | jget accepted)"
  note="$(printf '%s' "$send" | jget note)"
  if [ "$accepted" = "True" ] || [ "$accepted" = "true" ]; then
    ok "provider accepted a live email to $E2E_EMAIL_TO"
    info "$note"
    info "check that inbox — acceptance is not delivery, and spam filtering is downstream of us"
  else
    fail "provider accepted a live email to $E2E_EMAIL_TO" "$note"
  fi
else
  skip "live email delivery" "set E2E_EMAIL_TO=you@example.com to actually send one"
fi

# ══════════════════════════════════════════════════════════════════════════
section "13. Invite → activate → sign in"
# ══════════════════════════════════════════════════════════════════════════
#
# The whole point of the invitation feature, in the order a pharmacist meets
# it. The negative check in the middle is the important one: an invited account
# that can already sign in would mean anybody who guessed the email address
# owned the pharmacy.

INVITE_EMAIL="${E2E_TAG}-invited@example.test"

rq POST "$API/api/admin/invitations" "${AUTH[@]}" \
  -H 'Content-Type: application/json' \
  -d "{\"fullName\":\"E2E Invited Pharmacist\",\"email\":\"$INVITE_EMAIL\",\"fCode\":\"FE2E1\",\"postCode\":\"HP19 8AB\",\"pharmacyName\":\"E2E Invited Pharmacy\"}"
inv="$BODY"

INVITED_ID="$(printf '%s' "$inv" | jget userId)"
if [ -z "$INVITED_ID" ]; then
  fail "invite a pharmacist" "-> $HTTP_STATUS $(printf '%s' "$inv" | head -c 200)"
else
  ok "invite a pharmacist"
  CREATED_USERS+=("$INVITED_ID")

  # Reported honestly per row, so a caller can tell "account made, email failed"
  # from "nothing happened".
  emailed="$(printf '%s' "$inv" | jget emailSent)"
  if [ "$emailed" = "True" ] || [ "$emailed" = "true" ]; then
    ok "the invitation email was accepted by the provider"
  else
    fail "the invitation email was accepted by the provider" \
      "$(printf '%s' "$inv" | jget error) — the account exists but nobody can reach it"
  fi

  # Must NOT be able to sign in yet.
  rq POST "$API/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$INVITE_EMAIL\",\"password\":\"$E2E_PASSWORD\"}"
  pre="$BODY"
  if [ -n "$(printf '%s' "$pre" | jget accessToken)" ]; then
    fail "an invited account cannot sign in yet" \
      "it issued a token before activation — the placeholder password is guessable or unchecked"
  else
    ok "an invited account cannot sign in yet (-> $HTTP_STATUS)"
  fi

  # It should appear in the admin list as Invited.
  rq GET "$API/api/admin/invitations?search=$INVITE_EMAIL" "${AUTH[@]}"
  listed="$(printf '%s' "$BODY" | jget items.0.status)"
  [ "$listed" = "Invited" ] || [ "$listed" = "0" ] \
    && ok "shows as Invited in the admin list" \
    || fail "shows as Invited in the admin list" "status=$listed"

  # A garbage token must be refused, and must not say whether it ever existed.
  req GET "$API/api/invitations/preview?token=not-a-real-token-$E2E_TAG" >/dev/null
  [ "$HTTP_STATUS" = 400 ] || [ "$HTTP_STATUS" = 404 ] \
    && ok "a bogus activation token is refused (-> $HTTP_STATUS)" \
    || fail "a bogus activation token is refused" "-> $HTTP_STATUS"

  # Now the real link. Development-only endpoint; outside Development this
  # section can only go this far, which is worth saying rather than hiding.
  rq POST "$API/api/admin/invitations/$INVITED_ID/activation-link" "${AUTH[@]}"
  linkres="$BODY"
  LINK="$(printf '%s' "$linkres" | jget link)"

  if [ -z "$LINK" ]; then
    skip "complete a real activation" \
      "the activation-link endpoint is Development-only (-> $HTTP_STATUS); run against a dev API to cover this"
  else
    TOKEN_RAW="${LINK#*token=}"
    ok "obtained a real activation link"

    # Preview must recognise it and name the right account.
    rq GET "$API/api/invitations/preview?token=$TOKEN_RAW"
    pv="$BODY"
    pvemail="$(printf '%s' "$pv" | jget email)"
    if [ "$pvemail" = "$INVITE_EMAIL" ]; then
      ok "the link previews the right account"
    else
      fail "the link previews the right account" "expected $INVITE_EMAIL, got '${pvemail:-nothing}' (-> $HTTP_STATUS)"
    fi

    # Accept it.
    req POST "$API/api/invitations/accept" -H 'Content-Type: application/json' \
      -d "{\"token\":\"$TOKEN_RAW\",\"password\":\"$E2E_PASSWORD\"}" >/dev/null
    [ "$HTTP_STATUS" = 200 ] \
      && ok "activation succeeds" \
      || fail "activation succeeds" "-> $HTTP_STATUS"

    # Now sign-in must work.
    rq POST "$API/api/auth/login" -H 'Content-Type: application/json' \
      -d "{\"email\":\"$INVITE_EMAIL\",\"password\":\"$E2E_PASSWORD\"}"
    post="$BODY"
    CHILD_TOKEN="$(printf '%s' "$post" | jget accessToken)"
    if [ -n "$CHILD_TOKEN" ]; then
      ok "the pharmacist can sign in after activating"
    else
      fail "the pharmacist can sign in after activating" "-> $HTTP_STATUS"
    fi

    # And the token must be single-use. A reusable activation link is a
    # permanent password-reset for that account sitting in an inbox.
    req POST "$API/api/invitations/accept" -H 'Content-Type: application/json' \
      -d "{\"token\":\"$TOKEN_RAW\",\"password\":\"Someone-Elses-Pass-123!\"}" >/dev/null
    [ "$HTTP_STATUS" = 200 ] \
      && fail "an activation token cannot be reused" \
              "the same link activated twice — it is a standing password reset" \
      || ok "an activation token cannot be reused (-> $HTTP_STATUS)"

    # Resending to an already-active account must be refused, not silently
    # reissue a link that resets their password.
    req POST "$API/api/admin/invitations/$INVITED_ID/resend" "${AUTH[@]}" >/dev/null
    [ "$HTTP_STATUS" = 400 ] \
      && ok "resending to an activated account is refused" \
      || fail "resending to an activated account is refused" "-> $HTTP_STATUS, expected 400"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════
section "14. Global groups"
# ══════════════════════════════════════════════════════════════════════════
#
# A global group is one record shared by every pharmacy. The risk is not that
# it fails to appear — it is that one pharmacy's view of it leaks into
# another's, so the checks are about visibility and refusal, not CRUD.

GG_NAME="${E2E_TAG} General Group"
rq POST "$API/api/admin/global-groups" "${AUTH[@]}" \
  -H 'Content-Type: application/json' -d "{\"name\":\"$GG_NAME\"}"
gg="$BODY"
GG_ID="$(printf '%s' "$gg" | jget id)"

if [ -z "$GG_ID" ]; then
  fail "create a global group" "-> $HTTP_STATUS $(printf '%s' "$gg" | head -c 200)"
else
  ok "create a global group"

  # Same name twice must be refused — two identically-named groups in every
  # pharmacy's list is indistinguishable from a bug to the people using it.
  req POST "$API/api/admin/global-groups" "${AUTH[@]}" \
    -H 'Content-Type: application/json' -d "{\"name\":\"$GG_NAME\"}" >/dev/null
  [ "$HTTP_STATUS" = 409 ] || [ "$HTTP_STATUS" = 400 ] \
    && ok "a duplicate global group name is refused (-> $HTTP_STATUS)" \
    || fail "a duplicate global group name is refused" "-> $HTTP_STATUS"

  # A pharmacy must see it in their ordinary group list, flagged as global.
  if [ -n "${CHILD_TOKEN:-}" ]; then
    CHILD_AUTH=(-H "Authorization: Bearer $CHILD_TOKEN")
    rq GET "$API/api/patient-groups" "${CHILD_AUTH[@]}"
    groups="$BODY"
    if printf '%s' "$groups" | grep -q "$GG_NAME"; then
      ok "a pharmacy sees the global group in their own list"
    else
      fail "a pharmacy sees the global group in their own list" \
        "it is invisible to them, which is the whole feature"
    fi

    # And must not be able to rename or delete it.
    req PUT "$API/api/patient-groups/$GG_ID" "${CHILD_AUTH[@]}" \
      -H 'Content-Type: application/json' -d '{"name":"Hijacked by a pharmacy"}' >/dev/null
    case "$HTTP_STATUS" in
      403|404|400) ok "a pharmacy cannot rename a global group (-> $HTTP_STATUS)" ;;
      200) fail "a pharmacy cannot rename a global group" \
                "it renamed a group shared by every pharmacy on the platform" ;;
      *)   fail "a pharmacy cannot rename a global group" "-> $HTTP_STATUS" ;;
    esac

    req DELETE "$API/api/patient-groups/$GG_ID" "${CHILD_AUTH[@]}" >/dev/null
    case "$HTTP_STATUS" in
      403|404|400) ok "a pharmacy cannot delete a global group (-> $HTTP_STATUS)" ;;
      200|204) fail "a pharmacy cannot delete a global group" \
                    "it deleted a group shared by every pharmacy" ;;
      *)   fail "a pharmacy cannot delete a global group" "-> $HTTP_STATUS" ;;
    esac
  else
    skip "global group visibility from a pharmacy" "no activated pharmacy account available"
  fi

  # A non-SuperAdmin must not reach the admin endpoint at all.
  if [ -n "${CHILD_TOKEN:-}" ]; then
    req GET "$API/api/admin/global-groups" -H "Authorization: Bearer $CHILD_TOKEN" >/dev/null
    [ "$HTTP_STATUS" = 403 ] || [ "$HTTP_STATUS" = 401 ] \
      && ok "a pharmacy cannot reach the global-groups admin API (-> $HTTP_STATUS)" \
      || fail "a pharmacy cannot reach the global-groups admin API" "-> $HTTP_STATUS"
  fi
fi

# ══════════════════════════════════════════════════════════════════════════
section "15. Pharmacy hierarchy"
# ══════════════════════════════════════════════════════════════════════════
#
# The delegated "manage as" token is the only thing in the system that lets one
# account act as another, so most of this section is about what it refuses.

if [ -z "${CHILD_TOKEN:-}" ]; then
  skip "pharmacy hierarchy" "needs an activated pharmacy account, which section 13 did not produce"
else
  PARENT_AUTH=(-H "Authorization: Bearer $CHILD_TOKEN")

  # Context must be an object with the flag — the bug this replaced reported
  # every top-level pharmacy as a branch of another.
  rq GET "$API/api/pharmacies/context" "${PARENT_AUTH[@]}"
  ctx="$BODY"
  isChild="$(printf '%s' "$ctx" | jget isChildPharmacy)"
  case "$ctx" in
    true|null|"") fail "context is an object, not a bare value" "body was '$ctx'" ;;
    *)
      if [ -z "$isChild" ]; then
        fail "context reports isChildPharmacy" "body=$ctx"
      elif [ "$isChild" = "False" ] || [ "$isChild" = "false" ]; then
        ok "a fresh pharmacy is not reported as a branch"
      else
        fail "a fresh pharmacy is not reported as a branch" \
          "isChildPharmacy=$isChild on an account that has no parent"
      fi
      ;;
  esac

  # Create a branch under it.
  BRANCH_EMAIL="${E2E_TAG}-branch@example.test"
  rq POST "$API/api/pharmacies/children" "${PARENT_AUTH[@]}" \
    -H 'Content-Type: application/json' \
    -d "{\"fullName\":\"E2E Branch Contact\",\"email\":\"$BRANCH_EMAIL\",\"pharmacyName\":\"E2E Branch\",\"postCode\":\"HP19 9ZZ\"}"
  br="$BODY"
  BRANCH_ID="$(printf '%s' "$br" | jget userId)"

  if [ -z "$BRANCH_ID" ]; then
    fail "create a sub-pharmacy" "-> $HTTP_STATUS $(printf '%s' "$br" | head -c 200)"
  else
    ok "create a sub-pharmacy"
    CREATED_USERS+=("$BRANCH_ID")

    # It must show up as the parent's child.
    rq GET "$API/api/pharmacies/children" "${PARENT_AUTH[@]}"
    kids="$BODY"
    printf '%s' "$kids" | grep -q "$BRANCH_ID" \
      && ok "the branch appears under its parent" \
      || fail "the branch appears under its parent" "children list did not contain it"

    # A branch that has never activated must not be manageable — there is no
    # owner yet, so this would be using an account nobody has claimed.
    req POST "$API/api/pharmacies/children/$BRANCH_ID/manage-as" "${PARENT_AUTH[@]}" >/dev/null
    case "$HTTP_STATUS" in
      400|403|409) ok "an unactivated branch cannot be managed (-> $HTTP_STATUS)" ;;
      200) fail "an unactivated branch cannot be managed" \
                "it issued a delegated token for an account nobody has claimed" ;;
      *)   fail "an unactivated branch cannot be managed" "-> $HTTP_STATUS" ;;
    esac

    # Activate the branch so the delegation path can be exercised properly.
    rq POST "$API/api/admin/invitations/$BRANCH_ID/activation-link" "${AUTH[@]}"
    blink="$(printf '%s' "$BODY" | jget link)"
    if [ -z "$blink" ]; then
      skip "manage-as end to end" "could not obtain the branch's activation link"
    else
      req POST "$API/api/invitations/accept" -H 'Content-Type: application/json' \
        -d "{\"token\":\"${blink#*token=}\",\"password\":\"$E2E_PASSWORD\"}" >/dev/null

      # The branch must be its own independent account.
      rq POST "$API/api/auth/login" -H 'Content-Type: application/json' \
        -d "{\"email\":\"$BRANCH_EMAIL\",\"password\":\"$E2E_PASSWORD\"}"
      bt="$(printf '%s' "$BODY" | jget accessToken)"
      [ -n "$bt" ] \
        && ok "the branch signs in independently" \
        || fail "the branch signs in independently" "-> $HTTP_STATUS"

      # The branch must know it is a branch.
      rq GET "$API/api/pharmacies/context" -H "Authorization: Bearer $bt"
      bctx="$BODY"
      bIsChild="$(printf '%s' "$bctx" | jget isChildPharmacy)"
      [ "$bIsChild" = "True" ] || [ "$bIsChild" = "true" ] \
        && ok "the branch reports itself as a branch" \
        || fail "the branch reports itself as a branch" "isChildPharmacy=$bIsChild"

      # A branch must not be able to create branches of its own.
      req POST "$API/api/pharmacies/children" -H "Authorization: Bearer $bt" \
        -H 'Content-Type: application/json' \
        -d '{"fullName":"Grandchild","email":"grandchild-'"$E2E_TAG"'@example.test"}' >/dev/null
      case "$HTTP_STATUS" in
        200) fail "a branch cannot create branches" "the hierarchy is deeper than one level" ;;
        *)   ok "a branch cannot create branches (-> $HTTP_STATUS)" ;;
      esac

      # Now the real thing.
      rq POST "$API/api/pharmacies/children/$BRANCH_ID/manage-as" "${PARENT_AUTH[@]}"
      mg="$BODY"
      DELEGATED="$(printf '%s' "$mg" | jget token)"
      if [ -z "$DELEGATED" ]; then
        fail "the parent can start managing the branch" "-> $HTTP_STATUS $(printf '%s' "$mg" | head -c 200)"
      else
        ok "the parent can start managing the branch"

        # The delegated token must act as the BRANCH, not the parent.
        rq GET "$API/api/me" -H "Authorization: Bearer $DELEGATED"
        who="$(printf '%s' "$BODY" | jget email)"
        [ "$who" = "$BRANCH_EMAIL" ] \
          && ok "the delegated session is the branch's" \
          || fail "the delegated session is the branch's" "acting as '$who', expected $BRANCH_EMAIL"

        # And must refuse to chain. This is the one that matters: if a delegated
        # token can mint another, one manage-as is enough to reach every account
        # reachable from every account.
        req POST "$API/api/pharmacies/children/$BRANCH_ID/manage-as" \
          -H "Authorization: Bearer $DELEGATED" >/dev/null
        case "$HTTP_STATUS" in
          200) fail "a delegated token cannot mint another" \
                    "delegation chains — one manage-as reaches arbitrarily far" ;;
          *)   ok "a delegated token cannot mint another (-> $HTTP_STATUS)" ;;
        esac

        # A delegated session must not restructure the group.
        req POST "$API/api/pharmacies/children" -H "Authorization: Bearer $DELEGATED" \
          -H 'Content-Type: application/json' \
          -d '{"fullName":"X","email":"delegated-create-'"$E2E_TAG"'@example.test"}' >/dev/null
        [ "$HTTP_STATUS" = 200 ] \
          && fail "a delegated session cannot create a branch" "it created one" \
          || ok "a delegated session cannot create a branch (-> $HTTP_STATUS)"

        req DELETE "$API/api/pharmacies/children/$BRANCH_ID" -H "Authorization: Bearer $DELEGATED" >/dev/null
        [ "$HTTP_STATUS" = 200 ] || [ "$HTTP_STATUS" = 204 ] \
          && fail "a delegated session cannot detach a branch" "it detached one" \
          || ok "a delegated session cannot detach a branch (-> $HTTP_STATUS)"
      fi

      # Someone else's pharmacy must be indistinguishable from one that does
      # not exist, or this endpoint enumerates account ids.
      req POST "$API/api/pharmacies/children/$INVITED_ID/manage-as" -H "Authorization: Bearer $bt" >/dev/null
      [ "$HTTP_STATUS" = 404 ] \
        && ok "managing a pharmacy that is not yours 404s" \
        || fail "managing a pharmacy that is not yours 404s" "-> $HTTP_STATUS"
    fi

    # Detach: the account must survive.
    req DELETE "$API/api/pharmacies/children/$BRANCH_ID" "${PARENT_AUTH[@]}" >/dev/null
    if [ "$HTTP_STATUS" = 200 ] || [ "$HTTP_STATUS" = 204 ]; then
      ok "the parent can detach the branch"
      rq POST "$API/api/auth/login" -H 'Content-Type: application/json' \
        -d "{\"email\":\"$BRANCH_EMAIL\",\"password\":\"$E2E_PASSWORD\"}"
      still="$(printf '%s' "$BODY" | jget accessToken)"
      [ -n "$still" ] \
        && ok "a detached branch keeps working independently" \
        || fail "a detached branch keeps working independently" \
                "it can no longer sign in — detaching deleted or disabled it"
    else
      fail "the parent can detach the branch" "-> $HTTP_STATUS"
    fi
  fi
fi

# ── cleanup for this file ────────────────────────────────────────────────
#
# The group is deleted outright. The accounts are only *deactivated*, because
# there is no hard delete for a user and there should not be — a pharmacy record
# is referenced by patients, posters and campaigns, and this is a healthcare
# system. So each run leaves two disabled rows behind. They are tagged
# `e2e-<timestamp>@example.test` and can be found with:
#
#   GET /api/admin/users?search=example.test
#
# Worth saying plainly rather than implying the run was free.
if [ "$CLEANUP" -eq 1 ]; then
  [ -n "${GG_ID:-}" ] && req DELETE "$API/api/admin/global-groups/$GG_ID" "${AUTH[@]}" >/dev/null

  for u in "${CREATED_USERS[@]}"; do
    req POST "$API/api/admin/users/$u/deactivate" "${AUTH[@]}" >/dev/null
  done

  if [ "${#CREATED_USERS[@]}" -gt 0 ]; then
    info "deactivated ${#CREATED_USERS[@]} test account(s) — there is no hard delete, so the rows remain"
    info "find them later with: /api/admin/users?search=example.test"
  fi
else
  info "kept: accounts ${CREATED_USERS[*]:-none}, group ${GG_ID:-none} (tag $E2E_TAG)"
fi

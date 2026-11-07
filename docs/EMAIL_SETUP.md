# Email — how to know it actually works

Every failure mode of outbound email in this system looks like success from the
inside. The invitation is created, the admin list says **Invited**, the log says
a send was attempted, and the pharmacist never hears from us. So "the code ran"
is not evidence. This page is about getting evidence.

## The 30-second check

```
GET /api/admin/diagnostics/email      (SuperAdmin)
```

Returns `wouldSend` plus a `problems` array naming anything wrong. It asks the
real link builder for a real activation link rather than re-deriving the URL,
because a check that reimplements the thing it checks can agree with itself
while both are wrong.

## The three settings that silently stop email

### 1. `Messaging__UseMock`

**Defaults to `true`.** An environment that simply omits it logs every email and
sends none. This is the most likely single cause of "the invitation never
arrived". It must be the string `false`.

### 2. Provider credentials

`Messaging__Provider=mailjet` needs `Messaging__Mailjet__ApiKey` and
`Messaging__Mailjet__SecretKey`. Missing credentials do not throw — the send
returns `Failed`, and that only surfaces if someone reads the result. The
invitation service does read it and reports `emailSent: false` per row, so the
admin UI can show it; nothing else in the app checks.

Keys stored under another common name — `MAILJET_API_KEY` / `MAILJET_SECRET_KEY`
(what `scripts/deploy-test-env.sh` takes), `Mailjet__ApiKey` /
`Mailjet__SecretKey`, or Mailjet's own `MJ_APIKEY_PUBLIC` / `MJ_APIKEY_PRIVATE`
— are used when the canonical setting is empty, with a startup warning naming
the setting to rename. At startup the API logs one line: either `Email can be
sent: …` or `Email will NOT be sent: <what is missing>… Mailjet settings
present: <names>`. Names only, never values. If production says "Mailjet
credentials missing", that line in the App Service log stream says which
settings it can actually see.

`Messaging__Mailjet__FromAddress` must be a sender **Mailjet has verified** on
the account the keys belong to. Mailjet *accepts* a send from an unverified
sender — the API reports success, campaigns say Sent — and then holds it, so
nothing arrives and nothing in the app can see why. At startup the API asks
Mailjet and logs either `Mailjet sender … is verified on this account` or
`… is NOT verified`. Production fell into this on 16 October:
`appsettings.Production.json` still named `no-reply@pharmacyposter.app`, which
production only began reading when it switched to `ASPNETCORE_ENVIRONMENT=Production`.
That file no longer names a sender; production uses `info@sonarinformatics.com`
from `appsettings.json` unless `Messaging__Mailjet__FromAddress` says otherwise.

### 3. `Public__WebBaseUrl`

Activation links are built from `Public:WebBaseUrl`, falling back to
`Cors:Origins:0`, falling back to **`http://localhost:3000`**. That last
fallback is correct on a developer machine and useless in production: the
pharmacist receives a link to their own laptop. Nothing errors.

Also check `Messaging__Mailjet__SandboxMode` is not `true` — Mailjet validates
each message and reports success without delivering it.

## Proving delivery

Configuration checks cannot tell you a message arrived. This can:

```
POST /api/admin/diagnostics/email/test    (SuperAdmin)
{ "to": "you@example.com" }
```

It returns the provider's own verdict, not "queued". The Mailjet sender
deliberately refuses to count a message as sent without per-recipient
confirmation, so `accepted: true` means Mailjet took responsibility for that
address — the strongest claim anything server-side can honestly make.
Everything after that (greylisting, spam foldering, corporate filters) is
downstream of us and only an inbox can confirm it.

SuperAdmin-only on purpose. An anonymous endpoint that mails an arbitrary
address is a spam relay, however convenient it would be for a smoke test.

The end-to-end suite wires this in behind an opt-in variable, because a suite
that mails somebody on every run is a suite people stop running:

```
E2E_EMAIL_TO=you@example.com ./scripts/e2e/run.sh
```

## What the end-to-end suite covers

`scripts/e2e/lifecycle.sh`, sections 12–15. The order matters — it is the order
a pharmacist meets the system:

1. **Email configuration** — each of the three settings above, separately, plus
   an https check on the activation link outside Development.
2. **Invite → activate → sign in** — including the two negatives that matter:
   an invited account **must not** be able to sign in before activating, and an
   activation token **must not** work twice. A reusable link is a standing
   password reset sitting in an inbox.
3. **Global groups** — visibility to a pharmacy, and refusal when that pharmacy
   tries to rename or delete a group shared by everyone.
4. **Pharmacy hierarchy** — that a delegated token acts as the branch and not
   the parent, that it cannot mint another, and that detaching a branch leaves
   it able to sign in.

Each run leaves two **deactivated** accounts behind. There is no hard delete for
a user and there should not be — patients, posters and campaigns reference the
record. They are tagged `e2e-<timestamp>@example.test`; find them with
`/api/admin/users?search=example.test`.

## Testing the test

`./scripts/e2e/selftest.sh` runs `lifecycle.sh` against a fake backend twice:
once correct (expect zero failures) and once with four specific defects
injected, asserting each is caught by name. An assertion that has only ever seen
a working system is an assertion nobody knows is wired up.

It earned its keep immediately. It found that **25 status checks in `run.sh` had
never worked**: `body="$(req …)"` runs `req` in a subshell, so the `HTTP_STATUS`
it assigns is thrown away and the comparison reads whatever the previous request
left behind. Those checks were passing and failing for reasons unrelated to the
request they named. Fixed by adding `rq`, which assigns in the calling shell, and
`scripts/check-e2e.mjs` now fails the build if either helper is used inside a
pipeline or a command substitution again.

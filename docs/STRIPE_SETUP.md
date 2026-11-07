# Stripe setup guide

Subscriptions (monthly + yearly) and one-off video credit packs, in GBP.

Work through this in test mode first. Every step has a live-mode equivalent
at the end — the only real differences are which API keys you use and which
webhook endpoint Stripe calls.

---

## How it works

```
Browser                    Your API                     Stripe
   │                          │                            │
   │ POST /api/billing/checkout                            │
   │  { tier, interval }      │                            │
   │─────────────────────────>│                            │
   │                          │ look up price id           │
   │                          │ create Checkout session    │
   │                          │───────────────────────────>│
   │                          │<─── hosted URL ────────────│
   │<──── { url } ────────────│                            │
   │                                                       │
   │────────── redirect to Stripe, pay ───────────────────>│
   │                                                       │
   │<───────── redirect to /billing/success ───────────────│
   │                          │                            │
   │                          │<─── webhook: paid ─────────│
   │                          │  grant tier / credits      │
```

Two properties worth internalising, because they're what make this safe:

**The browser never names a price.** It sends a tier and an interval; the API
maps those to a Stripe price id server-side. A tampered request can at worst
select a different published plan — never a different amount.

**Only the webhook grants anything.** Landing on `/billing/success` means the
browser followed a redirect, not that money moved. With Strong Customer
Authentication (mandatory in the UK and EU) a payment can still fail after the
redirect fires. The success page polls; the webhook decides.

---

## 1. Prerequisites

Create a Stripe account at https://dashboard.stripe.com and install the CLI:

```bash
# macOS
brew install stripe/stripe-cli/stripe

# then
stripe login
```

Everything below happens in **test mode** until the final section. The toggle
is top-right in the dashboard; the CLI defaults to test mode unless you pass
`--live`.

---

## 2. Create products and prices

From the repository root:

```bash
cd scripts/stripe
chmod +x *.sh

./create_stripe_products.sh
./create_stripe_prices.sh
```

Both scripts `cd` to their own directory, so they work from anywhere — but the
`cd scripts/stripe` above only works from the repo root. If you're already in
`scripts/stripe`, skip it.

The first script creates five products — Starter, Pro, and three credit packs —
and writes their ids to `stripe_products.test.env`. The second reads that file,
creates seven prices, and writes a ready-to-paste env block to
`stripe_prices.test.env`.

The amounts live at the top of `create_stripe_prices.sh`:

| Item | Price | Stripe amount |
|---|---|---|
| Starter monthly | £7 | `700` |
| Starter yearly | £70 | `7000` |
| Pro monthly | £25 | `2500` |
| Pro yearly | £250 | `25000` |
| 10 credits | £9 | `900` |
| 25 credits | £20 | `2000` |
| 50 credits | £35 | `3500` |

Stripe amounts are in **pence**. `700` is £7.00; `7` would be 7p. This is the
single most common Stripe mistake, which is why every line in the script spells
out the intended figure.

Yearly is 10× the monthly rate — two months free. If you change that ratio,
change it in `apps/web/src/app/pricing/page.tsx` too, where the displayed
yearly figure is derived the same way.

**Prices in Stripe are immutable.** You never edit one; you create a new price
and point config at it. Existing subscribers stay on their original price
unless you migrate them deliberately — which is the behaviour you want, since
silently repricing someone mid-subscription is both rude and, in the UK,
likely unlawful.

---

## 3. Get your API keys

https://dashboard.stripe.com/test/apikeys

You need the **secret key** (`sk_test_…`). The publishable key isn't used —
this integration never mounts Stripe.js, it redirects to hosted Checkout.

---

## 4. Local development

Forward webhooks to your machine:

```bash
cd scripts/stripe
./listen.sh
```

That wraps `stripe listen` with `--api-key` set to the same key the API uses.
Plain `stripe listen` uses the session from `stripe login`, which can point at a
different account — or a different sandbox within one account — than your secret
key. When it does, the listener runs, forwards nothing, and there is no error
anywhere: payments succeed and no webhook ever arrives. Pinning the key removes
the ambiguity.

It prints a signing secret — `whsec_…`. That value is specific to this listener
session and differs from the one you'll create for production. If it doesn't
match what's in user-secrets, every event is rejected at signature
verification.

Add to your root `.env`:

```bash
Stripe__Enabled=true
Stripe__SecretKey=sk_test_...
Stripe__WebhookSecret=whsec_...          # from `stripe listen`
Stripe__SuccessUrl=http://localhost:9090/billing/success
Stripe__CancelUrl=http://localhost:9090/pricing
Stripe__PortalReturnUrl=http://localhost:9090/profile
```

Then paste the whole contents of `stripe_prices.test.env` (the price ids and
credit-pack entries) alongside them.

Note the double underscores — that's how .NET maps environment variables onto
nested config. `Stripe__Prices__ProMonthly` becomes `Stripe:Prices:ProMonthly`.
A single underscore will not bind and the setting is silently ignored.

Restart the API. Leave `stripe listen` running in its own terminal for as long
as you're testing.

---

## 5. Test the flows

Stripe's test cards — any future expiry, any CVC, any postcode:

| Card | Behaviour |
|---|---|
| `4242 4242 4242 4242` | Succeeds |
| `4000 0025 0000 3155` | Requires 3-D Secure authentication |
| `4000 0000 0000 9995` | Declined — insufficient funds |
| `4000 0000 0000 0341` | Attaches, then fails on first charge |

Work through all four. The 3-D Secure one matters most: UK cards trigger SCA
routinely, and it's the case where the redirect and the webhook genuinely
diverge.

**Subscription:** pricing page → pick monthly or yearly → Choose Pro → pay →
you land on `/billing/success`, which polls until the webhook lands and then
confirms. Check `stripe listen` output for `checkout.session.completed` and
`customer.subscription.created`.

**Credit pack:** pricing page → Buy pack → pay → balance appears under "Need
more videos?". Credits are only spent once the monthly allowance is exhausted.

**Cancellation:** "Manage billing" → cancel → Stripe sends
`customer.subscription.updated` with `cancel_at_period_end: true`. The user
keeps their tier until the period ends, then
`customer.subscription.deleted` drops them to Free.

**Replay protection:** run `stripe events resend <evt_id>` on a credit-pack
event. The balance must not increase a second time — every event id is
recorded before it's acted on.

---

## 6. Production webhook

https://dashboard.stripe.com/webhooks → **Add endpoint**

- **URL**: `https://sonar-marketing-backend.azurewebsites.net/api/billing/webhook`
- **Events**:
  - `checkout.session.completed`
  - `customer.subscription.created`
  - `customer.subscription.updated`
  - `customer.subscription.deleted`
  - `invoice.payment_failed`
  - `invoice.payment_succeeded`

Copy the endpoint's signing secret into `Stripe__WebhookSecret`. It is
**not** the same value as your local `stripe listen` secret — using the wrong
one makes every webhook fail signature verification, which looks exactly like
"payments work but nobody gets upgraded".

---

## 7. Going live

```bash
cd scripts/stripe
./create_stripe_products.sh --live
./create_stripe_prices.sh --live
```

Live mode is a completely separate object space: test products, prices,
customers, and webhook secrets do not exist there. Everything gets recreated.

Then set on the API (App Service → Environment variables, or Railway →
Variables):

```
Stripe__Enabled=true
Stripe__SecretKey=sk_live_...
Stripe__WebhookSecret=whsec_...                 # from the LIVE endpoint
Stripe__Prices__StarterMonthly=price_...        # from stripe_prices.live.env
Stripe__Prices__StarterYearly=price_...
Stripe__Prices__ProMonthly=price_...
Stripe__Prices__ProYearly=price_...
Stripe__CreditPacks__0__Key=credits_10
Stripe__CreditPacks__0__PriceId=price_...
Stripe__CreditPacks__0__Credits=10
Stripe__CreditPacks__0__DisplayName=10 video credits
Stripe__CreditPacks__0__PriceGbp=9
... (packs 1 and 2 likewise)
Stripe__SuccessUrl=https://marketing25.co.uk/billing/success
Stripe__CancelUrl=https://marketing25.co.uk/pricing
Stripe__PortalReturnUrl=https://marketing25.co.uk/profile
```

Before taking real money, in the Stripe dashboard:

- **Settings → Business** — legal entity, address, support email. These appear
  on every invoice and receipt.
- **Settings → Branding** — logo and colours for Checkout and the portal.
- **Settings → Customer portal** — enable plan switching, cancellation, and
  invoice history. Without this the "Manage billing" button opens an empty page.
- **Settings → Tax** — if you're VAT registered, enable Stripe Tax or set your
  rates. Retrofitting VAT onto invoices already issued is painful.
- **Settings → Emails** — turn on receipts and failed-payment notices.

### Go-live checklist

- [ ] Live products and prices created, ids in config
- [ ] `Stripe__Enabled=true` with `sk_live_…`
- [ ] Live webhook endpoint added, its secret in config
- [ ] Real card charged and refunded end to end
- [ ] `/api/subscription/upgrade` returns 409 (verify the free-upgrade hole is shut)
- [ ] Customer portal configured
- [ ] Business details and VAT settings complete

---

## What changed in the codebase

**Backend**

| File | Purpose |
|---|---|
| `Acme.Domain/Subscriptions/BillingProfile.cs` | Subscription status + credit balance |
| `Acme.Domain/Users/User.cs` | Billing methods; tier derived from Stripe state |
| `Acme.Application/Billing/IStripeGateway.cs` | Stripe abstraction (keeps the SDK out of Application) |
| `Acme.Application/Billing/BillingService.cs` | Checkout, portal, webhook processing |
| `Acme.Application/Billing/IProcessedWebhookRepository.cs` | Idempotency ledger |
| `Acme.Infrastructure/Billing/StripeGateway.cs` | Stripe.net implementation |
| `Acme.Infrastructure/Persistence/MongoProcessedWebhookRepository.cs` | Event ledger, 30-day TTL |
| `Acme.WebApi/Controllers/BillingController.cs` | `/api/billing/*` |

**Frontend**

`/pricing` gains a monthly/yearly toggle, credit packs, and a manage-billing
button. `/billing/success` polls for webhook confirmation. Four BFF routes
under `app/api/billing/` proxy to the .NET API.

**Security change worth knowing about:** `POST /api/subscription/upgrade`
previously let any authenticated user set their own tier, free. That was
harmless with no billing and critical with it. It now returns 409 whenever
Stripe is configured, and still works when `Stripe__Enabled=false` so local
development and tests can exercise paid tiers without a card.

---

## Troubleshooting

**Checkout returns "Billing is not configured."**
`Stripe__Enabled` is false or the secret key is empty. The API validates both
at startup and refuses to boot with `Enabled=true` and a missing key.

**"No Stripe price configured for Pro Monthly."**
The corresponding `Stripe__Prices__*` value is empty, or has a single
underscore where it needs two.

**Stripe redirects to a dead page after payment.**
The success URL points at a port nothing is serving. The two local run modes
use different host ports for the web app, and the URLs must match whichever
you're using:

| Run mode | Web app port | Set the URLs to |
|---|---|---|
| `pnpm dev` in `apps/web` | 3000 | `http://localhost:3000/...` |
| `docker compose up` | 9090 (maps to 3000 in-container) | `http://localhost:9090/...` |

`appsettings.json` is what `dotnet run` reads, so it carries the 3000 form.
`docker-compose.yml` sets `Stripe__SuccessUrl` from `WEB_HOST_PORT` and
overrides it for the container path. The payment still succeeds and the webhook
still fires when this is wrong — only the browser lands nowhere, so the user
sees a broken page for a purchase that actually went through.

**Payment succeeds, tier doesn't change.**
Almost always the webhook. Check Stripe dashboard → Webhooks → your endpoint →
recent deliveries. A 400 means signature verification failed — wrong
`Stripe__WebhookSecret`. Nothing listed at all means Stripe can't reach the
URL: confirm it's publicly resolvable and that `/api/billing/webhook` is
anonymous rather than sitting behind auth.

**Credits granted twice.**
Shouldn't happen — the ledger records each event id before acting. If it does,
check that `processed_webhooks` exists and that the API can write to it.

**Webhook 500s.**
Stripe retries with backoff for about three days, so transient failures
self-heal. Persistent 500s eventually disable the endpoint — the dashboard
shows a warning well before that.

---

## Security notes

Never commit `sk_live_…` or `whsec_…`. Both belong in App Service or Railway
environment variables, and both can be rolled from the dashboard if exposed.

The webhook endpoint is anonymous because Stripe has no bearer token to
present. Its authentication is the HMAC signature in the `Stripe-Signature`
header, verified against the signing secret before any of the payload is
trusted. That verification is the only thing standing between this endpoint
and a free "make me Pro" button — if you ever refactor
`BillingController.Webhook`, keep the raw-body read and the signature check
exactly where they are. Model binding would re-serialise the body and
invalidate the signature.

Card details never touch your servers. Stripe's hosted Checkout collects them,
which keeps you at PCI SAQ-A, the lightest compliance tier.

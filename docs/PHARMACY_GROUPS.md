# Pharmacy groups (parent and branch accounts)

How a pharmacy group runs several branches from one account, and what stops
that access from becoming a way around the permission model.

## The shape

A branch is a **full account**, not a sub-record. It has its own email, its own
password, its own patients, posters and campaigns, and it signs in on its own.
The parent gets access **in addition** to that, never instead of it.

The link is a single field on the child, `parentPharmacyId`. There is no group
entity, because there is nothing a group would own — every piece of data
belongs to one branch.

**One level only.** A branch cannot itself have branches. Two rules enforce it:

- `User.SetParent` refuses to point an account at itself, and refuses any
  account that is not a `PharmacyUser` (otherwise a pharmacy could be handed an
  admin as a "branch" and then manage into it — a straight escalation).
- The API refuses to issue a delegated token to a caller who is already using
  one, so a chain cannot be walked even if the data were ever wrong.

## Adding a branch

Parent opens **Branches → Add branch**. This reuses the invitation
flow: the branch receives an activation email, sets their own password, and is
`Invited` until they do. The parent never sees or sets that password.

An `Invited` branch cannot be managed — there is no owner yet, and using an
unclaimed account is not support, it is impersonation of nobody.

## "Manage as"

Pressing **Manage** exchanges the parent's session for a *delegated token*:

| | value |
|---|---|
| `sub` | the **branch's** id — every endpoint reads this |
| `role` | the **branch's** role, never the caller's |
| `act` / `act_email` | who is really acting |
| lifetime | 30 minutes requested, **hard-capped at 1 hour** at issue |

The cap is applied where the token is signed rather than at the call sites, so
no future caller can widen it by passing a larger window.

The token is held in an httpOnly cookie and is never readable by page
JavaScript. A second, ordinary cookie holds only the branch *name*, which is
what the amber banner renders.

### What the banner is for

A delegated session makes every page in the app show someone else's data, so it
must be impossible to forget you are in one. Hence a full-width bar on every
screen rather than a badge in a menu. It polls every 15 seconds, so when the
session lapses the banner goes with it rather than claiming a session that has
already expired.

Leaving does a **full page reload**, not a client navigation: every cached page
and store in memory holds the branch's data, and a soft transition would leave
some of it on screen under the parent's own account.

### What a delegated session cannot do

- Create another branch
- Detach a branch
- Issue a further delegated token
- Change the branch's billing: start a plan checkout, buy video top-ups, open
  the billing portal (card, invoices, cancellation) or switch its plan

The first three change *who has access*; the last spends or stops the branch's
money. Both are decisions for the person whose account it is. The branch's
plan stays visible — a head office may need to see that a branch is out of
videos — and the pricing page says billing belongs to the branch instead of
offering buttons. Security review R3-06.

"Delegated" means the `act` claim is present **and is not the subject**.
Switching back to your own pharmacy through the switcher also issues a token
with an `act` claim — yourself — and that is not delegation. See
`Acme.WebApi/Delegation.cs`.

Everything a delegated session *does* do is written to the audit log against
the **parent's** name, not the branch's.

## Removing a branch

Detaching clears `parentPharmacyId` and nothing else. The branch keeps its
account, its data and its login, and simply becomes a standalone pharmacy
again. The confirmation dialog says so, because "Remove" reads like a deletion
and is not one.

## What support sees

The admin pharmacy page gains a **Pharmacy group** panel showing either the
parent (for a branch) or the list of branches (for a parent), each linking to
its own support page. The panel is hidden entirely for standalone pharmacies,
which are most of them.

A branch whose parent account no longer exists shows as **"Unknown pharmacy"**
rather than a blank, so the orphan is visible rather than silently tidy.

## Asking whether you are a branch

`GET /api/pharmacies/context` → `{ isChildPharmacy, parent }`.

Always an object, and the flag is separate from the parent on purpose. The
first version answered with the parent *or null*, which cost a bug worth
recording: ASP.NET Core turns `Ok(null)` into a bodyless 204, the BFF proxy
rendered that as `true`, and `parent !== null` was satisfied by a boolean — so
**every top-level pharmacy was told it was a branch of another** and the panel
read "Part of " followed by nothing.

Two rules came out of it:

- A proxy never substitutes a value the API did not send. `scripts/check-proxies.mjs`
  enforces this; all five catch-all proxies had `result.data ?? true`.
- The UI reads `isChildPharmacy`, never the presence of a parent object. The
  flag is also the only correct answer for an orphaned branch — parent id set,
  parent account deleted — which is still a branch and must not be offered
  branches of its own.

## Indexes

`by_parent_pharmacy` on `(parentPharmacyId, createdAtUtc desc)`, partial to
string values. It is read on every load of the Branches page and, more
importantly, on every manage-as request — where it is the check deciding
whether one account may act as another.

## Tests

`apps/api/tests/Acme.Tests/Hierarchy/PharmacyHierarchyTests.cs`. The delegated
token tests are deliberately about what the token must **not** carry: a bug
there does not surface as an error, it surfaces as one pharmacy quietly holding
another's privileges.

# Data protection record

What personal data the platform holds, where it lives, how long it is kept, and
which controls protect it. This written record closes the documentation half
of security review finding R2-15. Keep it current when a feature starts storing
something new. The DPIA at the end is for the business to complete; this file
gives it the facts. This is not legal advice.

## Who is who

- **Pharmacies** decide which patients to contact and why. For patient data
  they are the controllers.
- **The platform** stores and processes that data on their behalf, as
  processor. For pharmacy and staff accounts it is the controller.

## What is held

| Data | About | Where | Kept | Notes |
|---|---|---|---|---|
| Name, email, phone, notes | Patients (marketing contacts) | `patients` collection | Until the pharmacy deletes them | No clinical fields by design (`PatientCsv`). The notes field tells staff not to record health or medication details. |
| Marketing consent date and source | Patients | `patients.marketingConsentAtUtc` / `Source` | With the patient | Recorded from the patient form or a "Marketing Consent" import column. The first date is kept. |
| Unsubscribed addresses | Patients | `email_suppressions` | **Indefinitely, by design** | Needed to keep honouring the opt-out. Holds the address only, per pharmacy. |
| Campaign recipient lists and delivery report | Patients | `campaigns` | With the campaign | Needed to resume a send and show delivery. The audit log stores counts only, never recipients. |
| Account email, name, password hash, profile | Pharmacy and staff users | `users` | Until the account is deleted | Passwords are BCrypt only. Tokens are stored as hashes. |
| Password-reset and verification tokens | Users | `auth_tokens` | 30 days after expiry (TTL index) | Stored hashed. |
| Audit log | Users (who did what) | `audit_events` | Not expired automatically | Security record: actor, action, entity, IP address. No passwords, tokens or recipient lists. |
| Activity log | Users | `activity_events` | 90 days (TTL index) | Views of sensitive pages. |
| Photo uploads for "Design with AI" | Whoever is in the photo | Blob `pharmacy-photos/{owner}/` | **7 days**, then deleted on that pharmacy's next upload | Private: served only to the uploading pharmacy (SEC-15). The finished poster keeps its own baked copy. |
| Posters, logos, rendered images | Pharmacies | Blob storage | Until deleted | Marketing material, meant to be shared. |
| Connected social accounts: platform sign-in (encrypted, AES-256-GCM), account name, picture, id, who granted it (Facebook, LinkedIn, TikTok or X user id), sign-in expiry | Pharmacy staff who connected Facebook, Instagram, YouTube, LinkedIn, TikTok or Google Ads | `social_connections` | Until disconnected (sign-in deleted at once; record kept as Revoked for post history) or a Meta data-deletion request (record deleted) | Followers, messages and insights are never read. |
| Accounts found at sign-in, not yet chosen | As above | `oauth_sessions` | **10 minutes** (TTL index), deleted on use | Encrypted. |
| Scheduled, published and recurring posts | Pharmacies | `scheduled_posts`, `recurring_series` | Until the pharmacy deletes them | Caption, target accounts, post ids and links, and a TikTok post's chosen audience and settings. |
| Application logs | Users and patients (incidentally) | App Service / container logs | Per log-sink setting; **set to 30 days or less** | Email addresses are masked (`j•••e@example.com`) and phone numbers reduced to the last three digits (`PersonalData`). |

## Controls in place

- Patients receive marketing email only with a working way out: a signed
  unsubscribe link in every message, plus one-click unsubscribe headers. The
  suppression list is checked when a campaign is created and again at send.
- Access is tenant-scoped (tested by `OwnershipIsolationTests`). Staff access
  to a pharmacy's patient contact details requires a stated reason, and is
  audited.
- Data is encrypted in transit (TLS 1.2+). Storage and the database encrypt at
  rest (platform default). Social-platform tokens are additionally encrypted
  in the application.
- Secrets live in App Service settings, not in the repository, apart from the
  documented development-only file.

## To set outside the code

1. **Log retention.** Set the App Service / Log Analytics retention to 30 days
   or less.
2. **Audit log retention.** Decide a period, typically 1–2 years for security
   records, and add a TTL index to `audit_events`. It is deliberately not
   expired today.
3. **Deletion requests.** A pharmacy deletes a patient from the Patients page.
   The address stays on that pharmacy's suppression list if they had
   unsubscribed, which is what stops them being emailed again.
4. **Processor terms.** The pharmacy terms of service should name the platform
   as processor for patient data, and list Mailjet (email), Microsoft Azure
   (hosting) and the AI providers as sub-processors. Meta, Google (YouTube,
   Ads), LinkedIn, TikTok and X receive data only when a pharmacy connects an account and
   publishes to it, on that pharmacy's instruction; the public privacy policy
   (`/privacy`) names them.

## DPIA outline (to complete)

- Purpose: pharmacies sending marketing posters and videos to their own
  patients and customers.
- Necessity: only name and contact details; no clinical data.
- Risks: emailing without consent; misuse of contact lists; exposure through
  logs or uploads; cross-tenant access.
- Mitigations: see "Controls in place" above. The 3 October 2026 security
  reviews (`docs/SECURITY_REVIEW_2026-10-03*.pdf`) cover the technical side.
- Residual risk and sign-off: for the business to record.

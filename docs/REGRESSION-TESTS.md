# Regression tests: every fixed bug, and the test that stops it coming back

Every bug in `DELIVERY-SUMMARY.md` → *Bugs found and fixed* has a test below
that fails if the bug returns. For the 76 marked ✓ in the *proven* column, that is not
assumed: `pnpm test:prove` puts each bug back into the code, runs its tests,
requires a failure, and restores the file.

## Before a release

```bash
pnpm test          # every suite: .NET, ai-service, web typecheck + unit tests, static checks
pnpm test:prove    # reintroduce each proven bug and confirm its tests catch it (~10 min)
```

`pnpm test:prove -- framer` runs only the bugs whose name contains "framer".

**What the results mean:**

- **MISSED:** the tests pass with the bug back in place. Strengthen the test before shipping.
- **BASELINE:** that bug's tests already fail on the unmodified code (or the runner cannot start). Fix that first, because a check that cannot pass also proves nothing when it fails.
- **ANCHOR:** the code the check targets has changed. Update that entry in `scripts/prove-regressions.py` so it recreates the bug in the new code. Do not delete the entry.

## Where the tests live

| Suite | Command | Location |
|---|---|---|
| .NET unit | `pnpm test:api` | `apps/api/tests/Acme.Tests` |
| .NET integration (needs Mongo) | `pnpm test:api:integration` | `apps/api/tests/Acme.IntegrationTests` |
| ai-service | `pnpm test:ai` | `apps/ai-service/tests` |
| Web unit and component | `pnpm test:web` | `apps/web/src/**/*.test.ts(x)` |
| Static checks | part of `pnpm test` | `scripts/check-*.mjs` |

Web component tests run in plain Node with `src/test/dom.ts`, a small DOM made for these tests. It needs no jsdom dependency. It is the harness that first reproduced the "Maximum update depth exceeded" crash.

## Video refine and framing (recent work)

✓ = reintroduced by `pnpm test:prove`, which confirmed the tests below catch it.

| # | Bug | Guarded by | Proven |
|---|---|---|---|
| — | The shot was guessed from a description (stock titles) and real requests refused; now tapped, or named by position only | `test_shot_edits.py` (positions, from_picker, from_typed) · `ShotPicker.test.tsx` · `video-refine.test.ts` · `VideoShotPickerTests` (.NET) | ✓ |
| — | A chosen shot's search fell to generic stock or a spare instead of this video's own clips | `test_beats.py` (replacement_queries) · `test_refine_scenarios.py` | ✓ |
| — | The last tile refused or unlabelled when an earlier moment was skipped (index checked against the tile count) | `VideoShotPickerTests.The_last_tile_is_accepted_when_an_earlier_moment_was_skipped` (.NET) · `video-refine.test.ts` · `ShotPicker.test.tsx` | ✓ (web) |
| — | Tiles showed the previous render's stills after a re-cut (browser cache) | `ShotPicker.test.tsx` | ✓ |
| — | Shot stills taken from the wrong moment | `test_shot_thumbnails.py` (real ffmpeg) | ✓ |
| — | Script overran its word budget (22.8s for 15s) because the budget was only requested | `test_script_budget.py` (through the real scriptwriter node) | ✓ |
| — | Shots on screen ≠ shots the refine knew about (top-up fillers); position ignored in favour of stock titles | `test_beats.py` (fit_timeline, positions) · `test_shot_edits.py` (position rule, shot list) | ✓ |
| 1 | "Change the last video" refused as "your own photograph" (photo list re-guessed; stale photos) | `test_rehydrate_beats.py` · `test_refine_scenarios.py` | ✓ |
| 2 | Adding a photo brought back one the pharmacist replaced | `test_rehydrate_beats.py::test_adding_a_picture_does_not_bring_back_a_replaced_one` | ✓ |
| 3 | One failed photo download removed that photo for good | `test_refine_scenarios.py::test_a_photo_that_failed_once_is_tried_again_next_time` | ✓ |
| 4 | A failed refine's message pointed at a Retry button that is not there | `test_worker_refine_rules.py` | ✓ |
| 5 | A refine that crashed marked a finished video Failed | `test_worker_refine_rules.py::test_a_crashed_change_leaves_the_finished_video_ready` | ✓ |
| 6 | The same footage appeared twice ("duplicate caption") | `test_beats.py` (fill_gaps, subclip_start, pick_topup) · `test_refine_scenarios.py::test_a_refine_never_brings_back_a_clip_the_video_already_has` | ✓ |
| 7 | A refused change looked like a change that did nothing | `apps/web/src/lib/video-refine.test.ts` | ✓ |
| 8 | Footage paired with moments by list position | `test_beats.py` · `test_rehydrate_beats.py` | ✓ |
| 9 | "Remove the person washing their hands" found no such moment | `test_beats.py` (describe_moments) · `test_shot_edits.py::test_the_model_is_told_what_each_moment_shows_not_what_was_planned` | ✓ |
| 10 | "We cannot removing duplicate captions from here." | `test_worker_refine_rules.py` · `test_refine_outcomes.py` | ✓ |
| 11 | Every refine failed ("could not work out what to change") | `test_refine_router.py` · `test_no_undefined_names.py` | ✓ |
| 12 | The same caption could flash twice | `test_captions.py` | ✓ |
| 13 | Watermarked videos said "Sonar Marketing" | `scripts/check-brand.mjs` (now scans the ai-service) | — (static check) |
| 14 | An upright phone photo rendered on its side | `test_upright.py` | ✓ |
| 15 | Framing chosen on the create dialog never reached the render | `apps/web/src/lib/video-refine.test.ts` | ✓ |
| 16 | Dialogs taller than the window could not be scrolled | `apps/web/src/components/ui/dialog.test.ts` | ✓ |
| 17 | The zoom slider was about 18px wide | `PhotoFramer.test.tsx` → "controls fit a thumbnail" | ✓ |
| 18 | A rotated CMYK JPEG lost its zoom | `test_upright.py::test_a_rotated_cmyk_jpeg_is_stood_upright_too` | ✓ |
| 19 | `"zoom": null` refused the whole video | `VideoImageTests.A_request_that_says_nothing_about_zoom_means_one` (.NET) | — (.NET, run by `pnpm test:api`) |
| 20 | A trackpad pinch could not leave 100% | `PhotoFramer.test.tsx` → "trackpad pinch and Ctrl+scroll" | ✓ |
| — | Framer "Maximum update depth exceeded" on attaching a photo | `PhotoFramer.test.tsx` → "attaching a photo does not crash the dialog" | ✓ |
| — | Preview ≠ render (rounding, geometry) | `test_framing.py` · `scripts/check-framing.mjs` (467-row golden table) · `PhotoFramer.test.tsx` → "the preview is the render" | ✓ |
| — | Unreadable zoom dropped the picture | `test_framing.py` · `test_user_media.py` | ✓ |
| — | Added-photo count not stored or read back | `VideoPictureRefineTests` (.NET) | — (.NET, run by `pnpm test:api`) |


## House-style templates

| Failure | Guarded by | Proven |
|---|---|---|
| Editor draws a closed line as an outline; PDF and preview fill it | `apps/web/src/lib/canvas/line-fill.test.ts` | ✓ |
| PDF draws a grey box for an intentionally empty image (QR with no booking link) | `SvgLogoRenderingTests.An_empty_source_draws_nothing_at_all` | — (.NET) |
| Template text that wraps in one renderer but not the other | `apps/ai-service/tests/test_house_templates.py` | ✓ |
| Low-contrast template text | `test_house_templates.py` | ✓ |
| Photo fade not baked | `test_house_template_assets.py` | ✓ |
| Circle crop with square corners | `test_house_template_assets.py` | ✓ |
| A long pharmacy name running into the phone number | `test_house_templates.py` | ✓ |
| A wave dipping below the photo it sits on (bare page between) | `test_house_templates.py` | ✓ |
| An arch photo with square top corners | `test_house_template_assets.py` | ✓ |
| Template copy saying "your pharmacy", which the API's contact-details test rejects as the old placeholder (three templates did, caught in review before `pnpm test`) | generator check mirroring `TemplateContactDetailsTests` · `test_house_templates.py` | ✓ |
| A template silently shipping without its photo (only the two Safety posters are photo-free, by design) | `test_house_templates.py` · `HouseTemplateSeedingTests` | ✓ |
| A sideways or wide-gamut stock original cropped as stored | `test_house_template_assets.py` | ✓ |
| A house template inserted before its photo is in storage, or seeding failing startup | `HouseTemplateSeedingTests` | — (.NET) |

## Videos on screens (feature guards)

Not bugs that shipped: the ways this feature would most plausibly break, each
reintroduced by `pnpm test:prove -- video` (web) or pinned by a .NET test.

| Failure | Guarded by | Proven |
|---|---|---|
| Screen shows posters but never the video: React's re-run effect left the element with no source, and the cancelled play() counted as a failure (found in manual testing) | `src/components/display/KioskVideo.test.tsx` (runs under Strict Mode) | ✓ ✓ |
| `shot_timeline` missing from the graph's state schema: LangGraph dropped it, so no stills and the tiles were the script's plan, not the shots on screen (found from a smoke run's logs) | `apps/ai-service/tests/test_state_schema.py` (every key used through the graph must be declared) | ✓ |
| `previous_aligned` / `previous_sources` dropped the same way: every refine re-searched every shot instead of keeping the untouched ones | `test_state_schema.py` | ✓ |
| A damaged attachment cost 60s per render and per refine (looped encode of a non-picture until the timeout) | `apps/ai-service/tests/test_damaged_picture.py` | ✓ |
| `VIDEO_BITRATE` ignored (CRF overrides it): a 19s re-cut came out at 38 MB / 16 Mbps (found in a smoke run) | `apps/ai-service/tests/test_encode_cap.py` (encodes noise with real ffmpeg) | ✓ |
| The kiosk downloads whole videos to cache them | `src/lib/kiosk-playlist.test.ts` | ✓ |
| A video that failed to play keeps its slot on the wall | `src/lib/kiosk-playlist.test.ts` | ✓ |
| A re-signed video link counts as a new slide (restarts mid-play) | `src/lib/kiosk-playlist.test.ts` | ✓ |
| The safety timer cuts a playing video off | `src/lib/kiosk-playlist.test.ts` | ✓ |
| The kiosk's video route drops `Range` (Safari will not play) | `src/app/api/display/video/[videoId]/route.test.ts` | ✓ |
| The kiosk's video route drops the cache validators (every loop re-downloads) | `src/app/api/display/video/[videoId]/route.test.ts` | ✓ |
| The kiosk's video route lets `..` through to the API path | `src/app/api/display/video/[videoId]/route.test.ts` | ✓ |
| A re-cut plays the old cut from the browser's cache | `DisplayVideoTests` (version in the link) · `A_video_that_stops_being_ready_…` (new link after a re-cut) | — (.NET) |
| The editor offers videos that are not finished | `src/lib/playlist-items.test.ts` | ✓ |
| The editor lets an eleventh video in | `src/lib/playlist-items.test.ts` | ✓ |
| An unfinished, file-less or someone else's video goes on a screen | `DisplayVideoTests` (.NET unit) · `DisplayScreenTests.Only_a_finished_video_can_be_added` / `Another_pharmacys_video_cannot_be_added` | — (.NET) |
| A video that stops being Ready stays on the wall | `DisplayScreenTests.A_video_that_stops_being_ready_leaves_the_screen_on_the_next_poll` | — (.NET, needs Mongo) |
| An old kiosk is sent a video it would draw as a broken image | `DisplayScreenTests.A_kiosk_that_cannot_play_video_is_sent_posters_only` | — (.NET, needs Mongo) |
| The link churns every poll, or opens another video | `DisplayVideoTests` (signing window, scope) · `A_ready_video_plays_on_the_screen_from_a_signed_link` | — (.NET) |
| A screen is sent another playlist's videos | `DisplayScreenTests.A_screen_is_sent_only_the_videos_on_its_own_playlist` | — (.NET, needs Mongo) |

## Earlier bugs — guarded, not yet in `test:prove`

| # | Bug | Guarded by |
|---|---|---|
| 21 | A supplied blob key was trusted across pharmacies | `VideoImageTests` (whose picture) · `OwnershipIsolationTests` |
| 22 | Deleting a video deleted the brand logo | `VideoImageTests.Only_uploads_are_the_videos_to_delete` |
| 23 | "Images must be under 8MB. That one is 8MB." | `VideoImageTests.A_file_barely_over_the_cap_…` |
| 24 | A fourth attached photograph went missing | `test_user_media.py` |
| 25 | The whole page froze until reloaded (duplicate Radix) | `scripts/check-radix-layers.mjs` |
| 26 | Boot marker could not tell a new image from an old one | `test_health.py` |
| 27 | A 20MB photograph failed with no explanation | `VideoImageTests` (size cap agrees with browser) · smoke self-test |
| 28 | A render could freeze on a field that was `null` | `VideoDocumentOwnershipTests` |
| 29, 35 | Smoke test skipped half / called a slow render "stuck" | `scripts/smoke/selftest.mjs` |
| 30 | Voice picker priced a render at a tenth of its cost | `NarrationCatalogMappingTests` |
| 31 | Every hosted render died at the voice stage | `test_tts_policy.py` · `test_failure_reporting.py` |
| 32 | Video thumbnail missing from campaign emails | `VideoThumbnailSourceTests` · `CampaignEmailDeliveryTests` |
| 33 | Refine opened a page with no way to save | `scripts/check-routes.mjs` |
| 34 | Videos were 27 seconds for a 15-second product | `test_script_budget.py` |
| 36 | Stop was accepted, then erased | `CampaignStopTests` |
| 37 | Monthly allowance counted only finished campaigns | `CampaignPlanLimitTests` |
| 38, 41 | Retry gated on `Failed`; stranded video renders | `VideoRenderRecoveryTests` |
| 39 | A deploy mid-send marked campaigns Failed | `CampaignDispatchStateTests` |
| 40, 58, 61 | Super-admin console; "every pharmacy is a branch"; signed-out "managing" label | `AuthLifecycleTests` · `HeadOfficeTests` · `WorkspaceSwitchingTests` · `apps/web/src/lib/server/cookies.test.ts` |
| 42, 43 | Renaming destroyed the script; audio key recorded without upload | `VideoDocumentOwnershipTests` |
| 44–46 | `{{bookinglink}}` raw; onboarding wiped the link; QR missing | `BookingLinkTests` · `BookingUrlTests` · `PharmacyTokenRenderTests` |
| 47 | Second screen paired returned 500 | `DisplayScreenTests` |
| 48, 51 | Campaign email with no poster / no pharmacy details | `CampaignEmailDeliveryTests` |
| 49 | Batches sized by recipient count, not bytes | `MailjetBatchingTests` |
| 50 | Requests uploaded without a declared length | `MailjetBatchingTests.Every_request_declares_its_length_up_front` **(new)** |
| 52, 55, 56 | Grant pressed twice; invitation email undeliverable; "Forgot password?" | `AccessRequestTests` · `InvitationEmailTests` · `AccountEmailTests` · `scripts/check-routes.mjs` |
| 53 | A refused action looked like a dead button | `apps/web/src/components/ui/dialog-errors.test.ts` **(new; ✓ proven)**, which holds every dialog's error beside its buttons |
| 54 | "Finish setup" went to the dashboard | `StaffAccountOnboardingTests` (a new pharmacy starts with setup outstanding) · `BookingLinkTests` |
| 57 | Template download rendered `null` | `scripts/check-proxies.mjs` · `scripts/check-contract.mjs` |
| 59 | Patients and pharmacies pages unreachable | `scripts/check-routes.mjs` · `EveryEndpointTests` |
| 60 | Two sidebar menus rendered see-through | `scripts/check-theme.mjs` |

## Security review fixes (3 October 2026)

From `docs/SECURITY_REVIEW_2026-10-03.pdf`. Rules are unit-tested in
`Acme.Tests/Security/SecurityReviewFixTests`; their wiring to the endpoints in
`Acme.IntegrationTests/SecurityReviewTests`; the ai-service side in
`apps/ai-service/tests/test_security_review.py`. `pnpm test:prove -- SEC` puts
each back (the .NET probes need `dotnet` on the path, or `PROVE_DOTNET`).

| Finding | What it stops | Tests |
|---|---|---|
| SEC-01, SEC-02 | A poster making the API fetch any URL, or read another pharmacy's storage key | `SecurityReviewFixTests` (proof entry) · `SecurityReviewTests.A_poster_naming_an_unsafe_image_source…` |
| SEC-04 | Starting with a missing or placeholder JWT secret | `SecurityReviewFixTests.Unsafe_jwt_secrets_are_named` |
| SEC-05 | Unlimited password guessing | `SecurityReviewFixTests` lock (proof entry) · `SecurityReviewTests.Repeated_wrong_passwords…` |
| SEC-06 | Share-by-email as a mass mailer | `SecurityReviewTests.Sharing_a_poster_is_capped…` |
| SEC-08 | Unlimited model calls through ai-service; open `/docs` | `test_security_review.py` (proof entry) |
| SEC-09 | Missing security headers | `SecurityReviewTests.Api_responses_carry_the_security_headers` |
| SEC-10 | Script-capable or outward-linking SVG logos | `SecurityReviewFixTests` (proof entry) · `SecurityReviewTests.A_file_that_is_not_an_image…` |
| SEC-13 | Short and common passwords | `SecurityReviewFixTests` (proof entry) |
| SEC-17 | Provider error text shown to callers | `test_security_review.py` (proof entry) |

## Follow-up security review fixes (3 October 2026)

From `docs/SECURITY_REVIEW_2026-10-03_FOLLOWUP.pdf`. The rules are tested in
`Acme.Tests/Security/SecurityFollowupFixTests`, the endpoints in
`Acme.IntegrationTests/SecurityFollowupTests`, and the web and ai-service parts
in the files named below. `pnpm test:prove -- R2` puts each bug back.

| Finding | What it stops | Tests |
|---|---|---|
| R2-03 | The caller choosing the address rate limits are keyed on | `src/lib/server/forwarding.test.ts` (✓ proven) · `SecurityFollowupFixTests` signature tests, pinned to the same vector |
| R2-04 | Sign-in redirecting off the site (`/\evil.example`) | `src/lib/safe-redirect.test.ts` (✓ proven) |
| R2-05 | An image source under another spelling; an SVG nested in an SVG | `SecurityFollowupFixTests` (proof entry; run `pnpm test:prove -- R2` where dotnet/pytest are installed) · `SecurityFollowupTests.A_poster_cannot_smuggle…` |
| R2-06 | Campaign email with no way out; mailing someone who unsubscribed | `SecurityFollowupFixTests` (proof entry; run `pnpm test:prove -- R2` where dotnet/pytest are installed) · `SecurityFollowupTests` (links, flow, forged token) |
| R2-08 | A stranger locking the owner out | `SecurityFollowupFixTests` (proof entry; run `pnpm test:prove -- R2` where dotnet/pytest are installed) |
| R2-09 | Issued tokens outliving deactivation | `SecurityFollowupFixTests` (proof entry; run `pnpm test:prove -- R2` where dotnet/pytest are installed) · `SecurityFollowupTests.A_deactivated_account…` · `SessionGuardTests` (a branch worked as needs to be active, not able to sign in; reactivation does not revive old tokens) · `WorkspaceSwitchingTests.Deactivating_a_branch_ends…` |
| R2-11 | The JWT fingerprint answering in production | `test_security_review.py` (proof entry; run `pnpm test:prove -- R2` where dotnet/pytest are installed) |
| R2-12 | Oversized posters | `SecurityFollowupFixTests` (proof entry; run `pnpm test:prove -- R2` where dotnet/pytest are installed) |
| R2-13 | One key for every link type | `SecurityFollowupFixTests` (proof entry; run `pnpm test:prove -- R2` where dotnet/pytest are installed) |
| R2-15 | Email addresses and phone numbers written to logs in full | `SecurityFollowupFixTests` (proof entry) |
| R2-06 (import) | Consent guessed from an unreadable import cell | `SecurityFollowupFixTests` (proof entry) |
| SEC-15 | Pharmacy photo uploads public, or readable by another pharmacy | `AiDesignPhotoTests` (proof entries) · `SecurityFollowupTests.Photo_uploads_are_served_only…` |

## Social publishing review (6 October 2026)

Unit tests in `Acme.Tests/Social`. Not yet in `test:prove`.

| Bug | What it stops | Tests |
|---|---|---|
| Stranded posts | A post left in Publishing by a restart or deploy, with Cancel and Retry both refused | `ScheduledPostTests.A_target_left_mid_call…` · `PublishExecutionTests.A_reclaimed_post_does_not_resend…` |
| Retries that never ran | Every failure treated as final, so a rate limit lost the post | `PublishExecutionTests.A_transient_failure_is_retried…` · `Automatic_retries_stop_at_the_ceiling` |
| Manual retry hanging | Retry after three attempts left the post in Publishing for ever | `ScheduledPostTests.A_manual_retry_starts_the_attempt_count_again` |
| Duplicate posts | A timeout, a server fault or a missing id on the publishing call retried automatically | `PublishExecutionTests` (unknown-outcome tests) · `MetaPublisherTests` (classification, timeouts, no id) |
| Lease overrun | A slow publish losing its lease and carrying on regardless | `PublishExecutionTests.Losing_the_lease_stops…` · `The_lease_is_renewed_before_each_account` |
| Instagram refusing every poster | A4 PNG sent where Instagram needs a 4:5 JPEG | `InstagramImageTests` · `PublishExecutionTests.Instagram_gets_its_own_version…` |
| OAuth `state` not checked | Any state accepted by the callback; a forged callback could attach someone else's Page | `OAuthFlowTests` (unknown state, another pharmacy's state, replay, single-use accounts) |
| Browser-chosen redirect | The code exchanged against a redirect the browser supplied | `OAuthFlowTests.A_different_redirect_from_the_browser_is_refused` · `The_exchange_uses_the_redirect_recorded_at_start` |
| Tokens in URLs | Page tokens in Graph query strings, and request URLs logged | `MetaPublisherTests.The_token_goes_in_the_body…` · `Instagram_status_polling_sends_the_token_as_a_header` |
| Retry publishing early | Retry on a scheduled post made it due immediately | `ScheduledPostTests.Retry_cannot_pull_a_scheduled_post_forward` |
| Posting for a closed account | A deactivated or downgraded pharmacy, or an unpublished poster, still went out when due | `PublishExecutionTests` (checked again when it is due) |
| Consent for nothing | Pharmacies sent to Meta/Google consent with no encryption key or no Ads client | `OAuthFlowTests.Without_an_encryption_key…` · `A_free_pharmacy_is_told_why…` |
| Meta withdrawals ignored | No deauthorize or data-deletion callback; forged callbacks | `MetaCallbackTests` |
| Spending without a plan | Enabling a campaign after dropping to Free; a campaign in another Ads account | `GoogleAdsSafeguardTests` |
| Revoking a shared Google grant | Disconnecting in one pharmacy broke the same Google sign-in in another | `DisconnectTests` |
| Replayed Meta callbacks | An old signed_request deleting accounts reconnected since | `MetaCallbackTests.A_replayed_old_request_is_refused` |
| Broken connections found late | A revoked token only discovered at publish time | `ConnectionHealthTests` · `MetaPublisherTests` (health check) |
| Ads with no area | A campaign with no location runs everywhere Google serves ads | `GoogleAdsSafeguardTests.A_campaign_with_no_area…` · `Too_few_headlines…` |
| Google Ads request shapes | A campaign created live, with a shared budget, without the EU declaration, broad keywords, or areas silently dropped | `GoogleAdsRestClientTests` |
| Performance Max without brand assets | Logo/business name missing, or a draft poster used as an ad | `GoogleAdsSafeguardTests.Performance_max_without_a_logo…` · `AdsCreativeBuilder` (posters must be published) |
| Local Connect refused | Redirect built from `localhost:3000` while the app runs on 9090 | `docker-compose.yml` sets `Public__WebBaseUrl` (no automated test) |
| Marketing shows a design we don't have | Home hero or template cards drift from the house set, or show US copy / invented details | `house-previews.test.ts` (re-run `scripts/marketing/build-house-previews.py`) |
| Gallery order | House templates buried under older templates, or paging skips/duplicates across the boundary | `TemplateGalleryTests` |
| Gallery paging input | Negative `skip`/`take` on the anonymous list returned a 500 | `TemplateGalleryTests.Out_of_range_paging_is_clamped_not_an_error` |
| Line breaks in previews | Gallery and home-page cards folded the templates' `\n` breaks into spaces and wrapped headlines differently from the editor and PDF | `CanvasPreview` uses `pre-wrap` (no automated test) |
| Brand assets for visitors | Signed-out gallery requests `/api/brand-kit/*` and draws grey boxes | `CanvasPreview withoutBrandAssets` (no automated test) |
| Archetype names from a brief | "Checklist bar" / "circle portrait" in a brief falls back to the default layout | `test_house_style.py::test_archetype_names_from_the_brief_are_understood` |
| My Posters "?" cards | Cards linked to a signed storage URL of the last publish render: unreachable in Docker (`azurite:10000`), expired after 15 min, stale after edits | `PosterCardTests` (same-origin JPEG, ETag/304, edit changes card, owner only, viewing doesn't touch `updatedAtUtc`) |
| Empty or wrong card render | Advice checklist card empty; photo missing; QR without booking link drawn as a grey box; one missing photo blanks the card; waves/diagonals outlined; circle PNG corners filled | `PosterCardRenderingTests` |
| Pricing sells what we don't do | "Priority AI responses", "Team collaboration", "Custom domain branding", template/export tiers; plan lines drifting from enforced limits; web copy drifting from the API | `PlanCopyTests` (lines derived from limits, `plan-highlights.ts` must equal the API's, web source scanned for the three phrases) |
| A fifth "bold" layout | A "bold" brief (or a model/older client naming `bold`) gets a navy page outside the house set | `test_house_style.py` (four layouts, bold = larger type in the same layout) · `test_template_designer.py` (bold flu clinic, checklist bar) |
| Proxy path walking (R3-05) | A crafted link with `..%2F` in an id made a signed-in browser call a different API endpoint through the web proxy | `api-path.test.ts` |
| CSV formula prefixes (R3-07) | A cell starting with a tab before `=` was not neutralised in patient exports | `PatientCsvTests.Every_formula_prefix_is_neutralised` |

## Sort and date filter on every list (26 October 2026)

One toolbar (Sort: Newest first / Oldest first; Date: All time, Last 7 days, Last 30 days, This month, Custom) on the templates gallery, My Posters, Videos, Campaigns, Patients and the admin users, pharmacies and audit tables. State lives in the URL as `sort=`, `from=`, `to=` (calendar days, both inclusive). The API gets `from` as the start of the first day and `to` as the start of the day after, as UTC instants: from inclusive, to exclusive. Filtering and sorting happen in Mongo.

| List | Date it filters and sorts on |
|---|---|
| Templates | created (the house set still leads; order applies within it and after it) |
| My Posters | last updated |
| Videos | created |
| Campaigns | sent, or created if never sent |
| Patients | date added (other callers with no window keep alphabetical order) |
| Admin users / pharmacies | account created (column headers still sort by name, plan, last login) |
| Audit | event time |

| What it stops | Tests |
|---|---|
| Newest first not returning the later item first; Oldest first not reversing it; a range letting in items outside it; clearing not restoring the list | `ListSortAndDateTests` (one test per list, real Mongo) |
| Campaigns ordered by created date when they were sent later | `ListSortAndDateTests.Campaigns_sort_and_range_on_sent_or_else_created` |
| Old admin links (`sort=NameAsc`) or a bad sort word turning into a 400 | `ListSortAndDateTests.Admin_users_still_accept_the_older_sort_names` · `ListWindowTests` |
| The last chosen day dropped, or a time zone shifting the range | `list-window.test.ts` (checked under UTC, London, Los Angeles and Auckland) |
| A BFF route forwarding parameters it should not | `list-query.test.ts` |

## Recurring social posts (26 October 2026)

| What it stops | Tests |
|---|---|
| 09:00 drifting to 08:00 or 10:00 across the clock changes; a skipped or doubled hour posting never or twice | `RecurringSeriesTests` (forward, back, gap, overlap) |
| Two schedulers creating the same occurrence; two workers both claiming it | `RecurringSeriesConcurrencyTests.Two_schedulers_make_one_occurrence…` (real Mongo) · `RecurringSeriesTests.Running_the_same_due_occurrence_twice…` |
| A post going out more than two hours late, or a week's posts bursting at once after downtime | `RecurringSeriesTests.An_occurrence_more_than_two_hours_late…` · `A_week_down_never_bursts…` |
| Pausing deleting history, or the queued post still going out | `RecurringSeriesTests.Pausing_stops_future_posts…` · `RecurringSeriesConcurrencyTests.Pausing_through_the_api…` |
| One account needing reconnecting stopping the whole series | `RecurringSeriesTests.An_account_that_needs_reconnecting_fails_that_occurrence_only` |
| Free creating series; Starter over 2 or Pro over 20; resume dodging the limit | `RecurringSeriesTests` (plan tests) · `RecurringSeriesConcurrencyTests.The_plan_limit_is_enforced_by_the_api` |
| A downgrade, unpublished poster or deactivated account still posting; the pause email repeating | `RecurringSeriesTests.A_poster_no_longer_published…` · `Dropping_to_free…` |
| Editing rewriting posts already sent | `RecurringSeriesTests.Editing_changes_future_occurrences_only` |
| Emails sent twice, or for a success when set to problems only | `RecurringSeriesTests.An_account_that_needs_reconnecting…` · `Problems_only_does_not_email…` |
| Another pharmacy reading or pausing a series | `RecurringSeriesConcurrencyTests.A_stranger_cannot…` |
| The form sending the wrong cadence; the calendar strip using UTC's date | `series.test.ts` |

## YouTube (28 October 2026)

Unit tests in `Acme.Tests/Social/YouTubeTests.cs`, `DisconnectTests.cs`. Smoke: `youtube-invalid-scope`, `youtube-asks-for-ads`.

| What it stops | Tests |
|---|---|
| A poster sent to YouTube (video only) | `YouTubeTests.A_poster_is_refused_without_calling_youtube` · `RecurringSeriesTests` (poster → YouTube refused) |
| A timeout or 5xx while the file is sent retried into a duplicate | `A_timeout_while_sending_the_file_may_have_posted_it` · `A_server_fault_while_sending_the_file_may_have_posted_it` |
| A daily quota error retried every few minutes | `The_daily_quota_running_out_stops_rather_than_retrying_every_few_minutes` |
| An upload outliving the worker's lease | `The_whole_publish_fits_inside_the_workers_lease` |
| A revoked sign-in not asking for a reconnect | `A_revoked_sign_in_asks_for_a_reconnect_and_uploads_nothing` |
| The YouTube consent asking for Ads, or Ads disconnect revoking the channel's grant | `The_consent_asks_only_for_youtube` · `DisconnectTests.Disconnecting_ads_leaves_the_google_grant_while_a_youtube_channel_is_connected` |

## LinkedIn (29 October 2026)

Unit tests in `Acme.Tests/Social/LinkedInTests.cs`, `DisconnectTests.cs`, `OAuthFlowTests.cs`; web `social-connections.test.ts`. Smoke: `linkedin-unauthorized-scope`, `linkedin-redirect-mismatch`, `linkedin-no-post-scope`.

| What it stops | Tests |
|---|---|
| A timeout or 5xx creating the post retried into a duplicate; a 201 with no id counted as published | `LinkedInTests.A_timeout_while_creating_the_post_may_have_posted_it` · `A_server_fault_while_creating_the_post…` · `A_created_post_without_an_id_is_uncertain_not_published` |
| A video posted before LinkedIn finished processing failing, or waiting for one that failed processing | `A_video_still_processing_is_asked_again_and_then_posts` · `…never_finishes_processing_is_retried_later…` · `A_video_linkedin_failed_to_process_is_not_waited_for` |
| Video parts finalised out of order or with the wrong sizes | `A_video_is_sent_in_the_parts_linkedin_asks_for_and_finalised_in_order` |
| "(open till 9pm)" cutting the post short; a # in a link becoming a hashtag; an emoji split at the limit | `Reserved_characters_are_escaped_and_hashtags_kept` · `An_emoji_at_the_limit_is_kept_whole…` |
| A lost Page role or expired sign-in not asking for a reconnect | `Losing_the_page_role_asks_for_a_reconnect` · `An_expired_refresh_token_asks_for_a_reconnect…` · `The_page_check_flags_a_page_the_person_no_longer_manages` |
| Asking for Page scopes before LinkedIn approves them | `The_consent_asks_for_profile_posting_only_until_pages_are_switched_on` |
| Disconnecting a profile revoking the sign-in its Pages still use | `DisconnectTests.A_linkedin_page_on_the_same_sign_in_keeps_it…` · `An_old_linkedin_token_is_not_revoked_while…` |
| "Trying again automatically" said twice | `LinkedInTests.The_retry_note_says_it_will_try_again_exactly_once` |
| Reconnect going through the wrong sign-in; the expiry reminder at the wrong time | `social-connections.test.ts` |

## TikTok (3 November 2026)

Unit tests in `Acme.Tests/Social/TikTokTests.cs`, `DisconnectTests.cs`, `RecurringSeriesTests.cs`; web `tiktok-post.test.ts`. Smoke: `tiktok-no-publish-scope`.

| What it stops | Tests |
|---|---|
| A rotated refresh token thrown away, so the connection dies a day later | `TikTokTests.A_lapsed_access_token_is_refreshed_and_the_rotated_credential_handed_back` · `…handed_back_even_when_the_post_then_fails` · `The_health_check_saves_a_credential_the_probe_rotated` · `The_share_screen_gets_live_creator_info_and_any_rotation_is_saved` |
| A second worker refreshing with a retired refresh token during a long upload, and marking a healthy account for reconnecting | `A_rotated_credential_is_saved_before_anything_is_uploaded` |
| A timeout on the last chunk, or a post still processing, retried into a duplicate | `A_timeout_sending_the_last_chunk_may_have_posted_it` · `A_server_fault_on_the_last_chunk…` · `A_post_still_processing_when_time_runs_out_is_uncertain_not_retried` |
| TikTok's daily caps, a banned or public-while-unaudited account retried every few minutes | `TikToks_limits_stop_rather_than_retry` |
| Posting to an audience TikTok no longer offers; a creator's switched-off interaction turned back on | `An_audience_tiktok_no_longer_offers…` · `A_creator_setting_switched_off_in_tiktok_wins…` |
| A TikTok post without its settings, or branded content set private | `A_tiktok_post_needs_its_settings_chosen` · `Branded_content_cannot_be_private` · `tiktok-post.test.ts` |
| Videos cut into chunks TikTok refuses | `Videos_are_cut_into_the_chunks_tiktok_allows` |
| Disconnecting revoking a TikTok user another pharmacy still has connected | `DisconnectTests.A_tiktok_account_another_pharmacy_still_uses_is_not_revoked` |
| TikTok in a recurring post | `RecurringSeriesTests.TikTok_cannot_be_part_of_a_recurring_post` |

## X (6 November 2026)

Unit tests in `Acme.Tests/Social/XTests.cs`, `DisconnectTests.cs`, `RecurringSeriesTests.cs`, `OAuthFlowTests.cs`; web `x-text.test.ts`. Smoke: `x-no-media-scope`, `x-no-pkce`.

| What it stops | Tests |
|---|---|
| A caption X refuses at 9am (over 280, or short but full of links) | `XTests.Text_is_counted_the_way_x_counts_it` · `The_limit_is_280_counted_not_characters` · `A_post_to_x_with_a_caption_too_long_for_x_is_refused_when_composing` · `x-text.test.ts` (same cases as the server) |
| X's rotated refresh token thrown away, or saved only after a long upload | `A_lapsed_access_token_is_refreshed_and_the_rotated_credential_saved_before_uploading` · `The_daily_check_refreshes_and_hands_back_the_rotated_credential` |
| A dropped answer on creating the post retried into a duplicate | `A_server_error_creating_the_post_is_uncertain_never_retried` · `A_dropped_connection_creating_the_post_is_uncertain` · `A_success_without_an_id_is_uncertain` |
| Running out of API credits read as "reconnect" | `Running_out_of_api_credits_says_so_and_is_not_a_reconnect` |
| A poster PNG over X's 5 MB cap | `X_gets_the_jpeg_version_of_a_poster_when_there_is_one` · `An_image_over_five_megabytes_is_refused_before_uploading` |
| PKCE that X refuses, or a verifier anyone could work out | `The_pkce_challenge_follows_rfc_7636` · `The_verifier_is_fixed_per_flow_and_cannot_be_derived_without_the_secret` · `OAuthFlowTests.The_exchange_is_given_the_flows_state` |
| A refresh refused because a publish or the daily check rotated the token first, marking a healthy TikTok or X account for reconnecting | `PublishExecutionTests.A_sign_in_refused_because_another_worker_rotated_it_is_retried_not_a_reconnect` · `TikTokTests.A_refresh_refused_because_a_publish_rotated_it_first_does_not_ask_for_reconnecting` · `A_refresh_refused_with_nobody_else_rotating_does_ask_for_reconnecting` |
| A recurring post to X failing from the second time on (X refuses repeated text) | `RecurringSeriesTests.X_cannot_be_part_of_a_recurring_post` |
| An X app not attached to a Project marking every X account for reconnecting | `XTests.An_app_x_has_not_enrolled_is_an_admin_problem_not_a_reconnect` |
| Disconnecting revoking an X user another pharmacy still has connected | `DisconnectTests.An_x_account_another_pharmacy_still_uses_is_not_revoked` |

## Adding a bug to this list

1. Write the test first, and watch it fail on the buggy code.
2. Fix the bug and watch the test pass.
3. Add an entry to `scripts/prove-regressions.py` that recreates the bug. Run `pnpm test:prove -- <name>` and see **caught**.
4. Add a row here.

#!/usr/bin/env node
/**
 * Does the smoke test still detect failure?
 *
 * A smoke test that has quietly stopped noticing things is worse than no smoke
 * test, because its green line is read as assurance. This repo has been bitten
 * by that shape twice already — a source scanner that matched nothing and
 * passed, and a tamper test whose "tampered" token was byte-identical one run in
 * sixteen. So the runner is itself exercised against a fake deployment that can
 * be told to break in each of the ways that matter.
 *
 *   node scripts/smoke/selftest.mjs
 *
 * Runs in about a minute and touches nothing real: no API, no model spend, no
 * email. Run it after editing scripts/smoke/run.mjs.
 */
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const HERE = dirname(fileURLToPath(import.meta.url));
const PORT = 4021;
const BASE = `http://127.0.0.1:${PORT}`;

/** scenario → the exit code the runner must produce, and any env it needs. */
const EXPECTED = [
  ["healthy", 0, "a working deployment passes", {},
    { present: "the tapped shot changed" }],
  // My Posters showed a blank "?" card for published posters while every
  // export passed. An empty card blocks the release.
  ["poster-card-blank", 1, "an empty My Posters card blocks the release", {},
    { present: "not a JPEG" }],
  // Reported from production: password reset emails never arrived, while
  // campaign email worked. Each of these is a way that happens with every
  // page saying "a link is on its way".
  ["reset-not-sent", 1, "a reset request that never reaches the account blocks the release", {},
    { present: "no reset reached" }],
  ["reset-rejected", 1, "a reset email the provider refuses blocks the release", {},
    { present: "the email was not accepted" }],
  // Mock mode counts every email as sent and delivers none. Normal on a
  // laptop, so a warning here; the same answer from a hosted deployment fails.
  ["reset-mocked", 0, "an API in email mock mode is reported (a warning locally)", {},
    { present: "email mock mode" }],
  // Both from the 15 October production run: the API was running as
  // Development and had no Mailjet keys of its own once the development
  // settings file stopped shipping. The campaign failure used to be a warning.
  ["campaign-misconfigured", 1, "a campaign that fails for missing email credentials blocks the release", {},
    { present: "cannot send email" }],
  ["api-development", 1, "a hosted API running as Development blocks the release",
    { SMOKE_HOSTED: "1" }, { present: "ASPNETCORE_ENVIRONMENT=Development" }],
  // 21 October: the token encryption key had only ever come from the
  // development settings file, so production lost every social connection.
  ["social-no-key", 1, "no social encryption key blocks the release", {},
    { present: "encryption key" }],
  // 22 October: the site moved to its own domain, the API kept building on the
  // old one, and every new Facebook connection was refused.
  ["social-url-moved", 1, "a connection refused because the site moved blocks the release",
    { SMOKE_HOSTED: "1" },
    { present: "only starts connections from" }],
  ["google-redirect-mismatch", 1, "Google not allowing the callback address blocks the release", {},
    { present: "does not allow" }],
  // 2 November production: the client id was saved as "http://…".
  ["google-client-id-url", 1, "a Google client id saved with http:// in front blocks the release", {},
    { present: "with no http://" }],
  // YouTube: its own Google consent, upload permission only.
  ["youtube-invalid-scope", 1, "Google refusing the YouTube scopes blocks the release", {},
    { present: "refused the YouTube scopes" }],
  ["youtube-asks-for-ads", 1, "a YouTube sign-in that also asks for Ads access blocks the release", {},
    { present: "should ask for YouTube only" }],
  // LinkedIn: problems its sign-in page reports before anyone logs in.
  ["linkedin-unauthorized-scope", 1, "LinkedIn refusing the permissions asked for (e.g. Pages before approval) blocks the release", {},
    { present: "refused the permissions asked for" }],
  ["linkedin-redirect-mismatch", 1, "LinkedIn not allowing the callback address blocks the release", {},
    { present: "add it to the app's Auth" }],
  ["linkedin-no-post-scope", 1, "a LinkedIn sign-in that asks for no posting permission blocks the release", {},
    { present: "no permission to post" }],
  ["tiktok-no-publish-scope", 1, "a TikTok sign-in that asks for no permission to post blocks the release", {},
    { present: "permission to post (video.publish)" }],
  // 6 November: a home connection dropping one download mid-file ended the
  // refine step with "unexpected error: terminated" and blocked the release.
  ["download-cut-once", 0, "a download cut off once by the network is retried, not a failure", {},
    { present: "passed only on a second try" }],
  // X: what its sign-in address must carry.
  ["x-no-media-scope", 1, "an X sign-in that cannot upload pictures blocks the release", {},
    { present: "does not ask for media.write" }],
  ["x-no-pkce", 1, "an X sign-in without a PKCE challenge blocks the release", {},
    { present: "no PKCE challenge" }],
  // Every dependency, from inside each service.
  ["readiness-healthy", 0, "readiness from inside each service passes when everything works",
    { SMOKE_READINESS_TOKEN: "stub-readiness-token" }, { present: "api: database" }],
  ["readiness-db-down", 1, "a database the API cannot reach blocks the release",
    { SMOKE_READINESS_TOKEN: "stub-readiness-token" }, { present: "no answer within" }],
  ["readiness-url-moved", 1, "links built on another address than the site blocks the release",
    { SMOKE_READINESS_TOKEN: "stub-readiness-token", SMOKE_HOSTED: "1" },
    { present: "builds links and social callbacks on old-address.example" }],
  ["reset-rate-limited", 1, "a reset refused as too frequent blocks the release", {},
    { present: "refused as too frequent" }],
  ["client-ip-mismatch", 1, "web and API disagreeing on the client-address key blocks the release", {},
    { present: "every user shares one address" }],
  // A half-finished deploy: each service healthy, the seam between them not.
  ["build-split", 1, "the web app and API on different builds blocks the release", {},
    { present: "half-finished deploy" }],
  // Refining a finished video. Every one of these reached a pharmacist before
  // the refine step existed in the smoke test, so every one blocks a release.
  ["refine-not-made", 1, "a shot change that comes back Ready but unchanged blocks the release", {},
    { present: "the change was not made" }],
  ["refine-guesses", 1, "a typed request naming no shot that is carried out anyway blocks the release", {},
    { present: "was guessed from its description" }],
  ["refine-no-timeline", 1, "a fresh video whose tiles have no stills (timeline never reached the worker) blocks the release", {},
    { present: "the tiles are the plan, not the shots on screen" }],
  ["refine-no-shots", 1, "a finished video with no shots on record blocks the release", {},
    { present: "no shots on record" }],
  ["refine-wrong-shot", 1, "a change that alters a shot nobody tapped blocks the release", {},
    { present: "only the tapped shot may change" }],
  // The designed fallback, not a bug: a kept clip that will not download again
  // is re-searched. The tapped shot still changed, so this warns — blocking a
  // release on a CDN hiccup would teach everyone to ignore the smoke.
  ["refine-recovery-miss", 0, "another shot re-fetched differently alongside the tapped one warns but does not block", {},
    { present: "changed as well" }],
  ["refine-revoiced", 1, "a shot change that re-records the narration blocks the release", {},
    { present: "must not re-record the voice" }],
  ["refine-charged", 1, "a shot change that uses a video from the allowance blocks the release", {},
    { present: "must not use a video" }],
  // Videos on in-pharmacy screens.
  // An AI design that comes back in the old look, or pointing at a photo on
  // somebody else's server, blocks the release even though the call "worked".
  ["ai-design-off-style", 1, "an AI design not in the house style (hot-linked photo, no QR) blocks the release", {},
    { present: "is not in the house style" }],
  ["ai-design-photo-note", 0, "an AI design whose photo fell back is a warning, not a failure", {},
    { present: "with a photo note" }],
  ["screen-video-leak", 1, "a video sent to a kiosk that never said it could play one blocks the release", {},
    { present: "a kiosk that did not declare video was sent one" }],
  ["screen-video-broken", 1, "a screen's signed video link that will not open blocks the release", {},
    { present: "signed video link streams a range" }],
  // Plays in Chrome, so a warning — but Safari (and many TV browsers) refuse
  // a video served without ranges, which is worth saying out loud.
  ["screen-video-norange", 0, "a video link that ignores Range warns but does not block", {},
    { present: "not in Safari" }],
  ["screen-video-noetag", 0, "a video link with no validator warns (screens re-download every loop)", {},
    { present: "re-download each video on every loop" }],
  ["video-stuck", 1, "a render that can neither finish nor fail blocks the release"],
  // The case the runner used to get wrong: it reported a real render that
  // finished 28s past the deadline as "cannot finish or fail". Slow is a
  // warning; only stopped is a failure. The stub must also never receive a
  // DELETE for it — cleaning up a row the worker is still writing to orphans
  // the MP4 it is about to upload.
  ["video-slow", 0, "a slow but progressing render warns and is left to finish", {},
    { absent: "STUB deleted video", present: "Left in place to finish" }],
  // A clip much longer than the length the product sells. The video works, so
  // this must not block a release — but it must be visible, because the only
  // symptom anybody noticed for weeks was "videos take a while".
  //
  // Asserts the *measurement*, not a cause. An over-long clip is either a
  // writer over its word budget or a words-per-minute constant that does not
  // match the voice, and the smoke cannot tell which from a duration — so this
  // expectation deliberately does not name one. The earlier version asserted
  // the wording of a cause, which is how a warning ends up confidently sending
  // people to the wrong file.
  ["video-long", 0, "a 27s render for a 15s product warns but does not block", {},
    { present: "% over" }],
  // A Ready video that quietly did not use the picture the pharmacist attached.
  //
  // This is the one the other video scenarios cannot catch: nothing failed, the
  // MP4 plays, the note is empty, and the only evidence is one sentence in
  // `diagnostic` — a field the runner previously read only on the failure path.
  // A render that drops somebody's shopfront photo and reports success is not
  // shippable, so this blocks.
  ["image-dropped", 1, "a render that silently dropped the attached picture blocks the release", {},
    { present: "dropped the good picture" }],
  // The stale-image case, and the reason the runner sends a damaged file at
  // all. A worker with no attachment support ignores the pictures and says
  // nothing; a worker that used them says nothing either. Only a file that
  // cannot be decoded separates the two, and this scenario is what proves the
  // runner uses that difference rather than reading silence as success.
  //
  // This is not hypothetical: a real run passed this check against an
  // ai-service built before attachments existed, which is how the gap was
  // found.
  ["image-ignored", 1, "a render that never looked at the attached pictures blocks the release", {},
    { present: "never looked at the attached pictures" }],
  // An oversized upload refused with a bare 413 and no body — what
  // `[RequestSizeLimit]` produces, because it rejects before the action runs.
  // The upload is correctly refused, so this is not a failure; but the
  // pharmacist is told only that it failed, which is worth saying out loud.
  ["image-mute", 0, "an oversized upload refused without a reason warns but does not block", {},
    { present: "does not mention a size" }],
  ["video-crash", 1, "a traceback on a customer's screen blocks the release"],
  ["video-outage", 0, "a human failure message warns but does not block"],
  ["route-missing", 1, "a route missing from the running image blocks the release"],
  // Narration is checked before a render is started, because finding out at the
  // voice stage costs a research call, a script call and three minutes.
  // SMOKE_HOSTED because the stub is on loopback, and Edge-TTS on loopback is
  // correct — it is only fatal on a real deployment. Without the override this
  // scenario would pass for the wrong reason, which is the failure mode a
  // self-test exists to prevent.
  ["narration-edge", 1, "a hosted deployment with no ElevenLabs key blocks the release",
    { SMOKE_HOSTED: "1" }],
  ["narration-missing", 1, "the API being unable to reach ai-service for voices blocks the release"],
];

const run = (cmd, args, env = {}) =>
  new Promise((resolve) => {
    const p = spawn(cmd, args, { env: { ...process.env, ...env }, stdio: ["ignore", "pipe", "pipe"] });
    let out = "";
    p.stdout.on("data", (d) => (out += d));
    p.stderr.on("data", (d) => (out += d));
    p.on("close", (code) => resolve({ code, out }));
  });

let failures = 0;

// `node selftest.mjs video-long` runs one scenario instead of all of them.
//
// Each scenario spends real seconds waiting on polls and timeouts, so the full
// pass is slow enough that it discourages the thing it is for: changing one
// check and seeing straight away whether it still catches its bug. A filter is
// for iterating; CI and `pnpm smoke:selftest` still run everything.
const only = process.argv.slice(2).filter((a) => !a.startsWith("-"));
const selected = only.length
  ? EXPECTED.filter(([name]) => only.includes(name))
  : EXPECTED;

if (only.length && selected.length === 0) {
  console.error(
    `No scenario named ${only.join(", ")}. Known: ` +
      EXPECTED.map(([n]) => n).join(", "),
  );
  process.exit(2);
}

for (const [scenario, expected, why, extraEnv = {}, output = null] of selected) {
  const stub = spawn("node", [join(HERE, "stub-server.mjs"), "--port", String(PORT), "--scenario", scenario],
    { stdio: ["ignore", "pipe", "ignore"] });
  let stubOut = "";
  stub.stdout.on("data", (d) => (stubOut += d));
  await new Promise((r) => setTimeout(r, 700));

  const { code, out } = await run("node", [join(HERE, "run.mjs")], {
    API_URL: BASE, AI_URL: BASE, WEB_URL: BASE,
    SMOKE_VIDEO_TIMEOUT_MS: "10000",
    SMOKE_CAMPAIGN_TIMEOUT_MS: "10000",
    // The runner reads the repo-root .env. This must not: the self-test asks
    // "does the runner still detect failure", and the answer cannot be allowed
    // to depend on what somebody happens to have in their .env — a stray
    // SMOKE_KEEP or timeout there would change the result for reasons that have
    // nothing to do with the question.
    SMOKE_NO_ENV_FILE: "1",
    ...extraEnv,
  });
  stub.kill();
  await new Promise((r) => setTimeout(r, 200));

  let ok = code === expected;
  const notes = [];

  // Some scenarios are about more than the exit code. `video-slow` exits 0
  // either way — what distinguishes right from wrong is whether the runner
  // deleted a render that was still going.
  if (ok && output?.absent && (stubOut + out).includes(output.absent)) {
    ok = false;
    notes.push(`expected NOT to see "${output.absent}"`);
  }
  if (ok && output?.present && !out.includes(output.present)) {
    ok = false;
    notes.push(`expected to see "${output.present}" in the report`);
  }
  if (!ok) failures++;
  process.stdout.write(
    `  ${ok ? "\x1b[32mok  \x1b[0m" : "\x1b[31mFAIL\x1b[0m"} ${scenario.padEnd(14)} ` +
      `exit ${code} (expected ${expected}) — ${why}` +
      (notes.length ? `\n       ${notes.join("; ")}` : "") + "\n",
  );
}

process.stdout.write(
  failures === 0
    ? "\n  \x1b[32mThe smoke test still detects every failure it is meant to.\x1b[0m\n\n"
    : `\n  \x1b[31m${failures} scenario(s) went undetected — the smoke test is not doing its job.\x1b[0m\n\n`,
);
process.exitCode = failures === 0 ? 0 : 1;

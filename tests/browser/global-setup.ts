/**
 * Fails the run before any test, with a message you can act on.
 *
 * Without this, a stack that is not running produces 67 failures that all say
 * something like `net::ERR_CONNECTION_REFUSED at /login` — 67 restatements of
 * one fact, none of which is "start the app". Worse, a *partly* running stack
 * (web up, API down) produces failures that look like application bugs: pages
 * render, lists come back empty, and the suite blames the code.
 *
 * It waits rather than asking once. `next dev` compiles routes on demand, so
 * the first request to a cold server routinely takes five seconds or more —
 * one observed at 4.965s against a 5s timeout. A one-shot check on that
 * boundary reports "not running" for an app that is up and merely warming up,
 * which is a worse failure than the one it was written to prevent: it is
 * confidently wrong rather than merely unhelpful.
 */

const WEB = process.env.WEB_URL ?? "http://localhost:3000";

/** How long to keep trying before giving up. A cold Next compile is slow. */
const TOTAL_WAIT_MS = 90_000;
const ATTEMPT_TIMEOUT_MS = 15_000;

type Probe =
  | { state: "ok"; status: number }
  | { state: "down"; reason: string }
  | { state: "slow"; reason: string };

async function probe(url: string): Promise<Probe> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ATTEMPT_TIMEOUT_MS);
  try {
    const res = await fetch(url, { signal: controller.signal, redirect: "manual" });
    return { state: "ok", status: res.status };
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    // A refused connection means nothing is listening. An abort means our own
    // timeout fired — something *is* there, it is just slow, and the two want
    // very different advice.
    const aborted = controller.signal.aborted;
    return aborted
      ? { state: "slow", reason: `no response within ${ATTEMPT_TIMEOUT_MS / 1000}s` }
      : { state: "down", reason: message };
  } finally {
    clearTimeout(timer);
  }
}

/** Polls until the URL answers, or the budget runs out. */
async function waitFor(url: string, label: string): Promise<Probe> {
  const deadline = Date.now() + TOTAL_WAIT_MS;
  let last: Probe = { state: "down", reason: "not attempted" };
  let announced = false;

  while (Date.now() < deadline) {
    last = await probe(url);
    if (last.state === "ok") return last;

    if (!announced) {
      // Said once, not once per attempt. Silence for ninety seconds looks like
      // a hang; a line per second looks like a fault.
      process.stdout.write(`  waiting for ${label} at ${url} …\n`);
      announced = true;
    }
    await new Promise((r) => setTimeout(r, 1_000));
  }
  return last;
}

/**
 * A sign-in that must be refused (no such account). 400/401/403/429 are the
 * right answers; a 5xx means the API's sign-in path itself is broken.
 */
async function signInProbe(): Promise<string | null> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 20_000);
  try {
    const res = await fetch(`${WEB}/api/auth/login`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        email: `signin-probe-${Date.now()}@example.test`,
        password: "not-the-password-1A!",
      }),
      signal: controller.signal,
    });
    if (res.status < 500) return null;
    const text = await res.text().catch(() => "");
    return (
      `Signing in answers ${res.status} (${text.slice(0, 200) || "no body"}), so every test that signs in ` +
      `would fail.\n` +
      `      Look at the API's console for the exception on POST /api/auth/login. Common causes: the\n` +
      `      database refusing writes (an Atlas cluster over its storage quota, a paused cluster),\n` +
      `      or Jwt settings missing or too short.`
    );
  } catch (err) {
    return `Signing in did not answer within 20s (${err instanceof Error ? err.message : String(err)}).`;
  } finally {
    clearTimeout(timer);
  }
}

export default async function globalSetup() {
  const problems: string[] = [];

  const web = await waitFor(WEB, "the web app");
  if (web.state !== "ok") {
    problems.push(
      web.state === "down"
        ? `The web app is not answering at ${WEB} (${web.reason}).\n` +
          `      Start it with:  pnpm dev:web`
        : `${WEB} accepted the connection but did not respond (${web.reason}).\n` +
          `      Something is listening on that port — check the dev server's own\n` +
          `      output for a compile error.`,
    );
  } else {
    // The web app can serve pages perfectly while the API behind it is down.
    // That failure mode is the misleading one: every page loads, every list is
    // empty, and the suite reports assertion failures that look like bugs.
    const api = await waitFor(`${WEB}/api/templates?take=1`, "the API");

    if (api.state !== "ok") {
      problems.push(
        `The web app is up but ${WEB}/api/templates did not answer (${api.reason}).`,
      );
    } else if (api.status >= 500) {
      problems.push(
        `${WEB}/api/templates returned ${api.status}, which means the .NET API behind ` +
          `the web app is down or misconfigured.\n` +
          `      Start it with:  cd apps/api/src/Acme.WebApi && dotnet run`,
      );
    } else {
      // An API can answer reads and still fail every sign-in — a database
      // that refuses writes (Atlas over its storage quota), a bad JWT secret.
      // Then nearly every test fails at signIn() with a timeout that says
      // nothing; 6 November: 42 of them, all "Internal server error." on the
      // login page. One sign-in with an account that cannot exist says it once.
      const probe = await signInProbe();
      if (probe) problems.push(probe);
    }
  }

  if (problems.length > 0) {
    throw new Error(
      "\n\nThe browser suite needs a running stack.\n\n" +
        problems.map((p) => `  · ${p}`).join("\n\n") +
        "\n\nOnce everything is up, re-run: pnpm test:browser\n",
    );
  }
}

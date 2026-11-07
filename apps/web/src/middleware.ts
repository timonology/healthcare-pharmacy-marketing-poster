import { NextResponse, type NextRequest } from "next/server";
import { NONCE_HEADER, buildCsp, cspMode, newNonce } from "@/lib/csp";
import { canonicalRedirect } from "@/lib/canonical-host";
import { siteUrl } from "@/lib/site";

/** Authenticated areas of the app. Browsing /templates is allowed for guests;
 *  picking one to use redirects them to /login if they're not signed in. */
const PROTECTED = [
  "/dashboard",
  "/canvas",
  "/brand-kit",
  "/posters",
  "/patients",
  "/campaigns",
  "/profile",
  "/videos",
  "/analytics",
  "/pharmacies",
  // Both of these shipped unguarded. A signed-out visitor could open the
  // Social & Ads page directly, and /admin relied entirely on a client-side
  // check that renders first and decides afterwards. Found by the browser
  // suite on its first real run — no static check can see this, because the
  // page, the route and the layout are all individually correct.
  "/social",
  "/admin",

  // Screens and playlists. Note the singular /display is deliberately absent:
  // that is the kiosk, opened by a television with no account, and guarding it
  // would redirect every screen in the estate to a login page.
  "/displays",
  "/playlists",
];

/** Pages that should redirect a signed-in user away. */
const AUTH_PAGES = ["/login", "/register"];

const ACCESS_COOKIE = "acme_access";
const ONBOARDED_COOKIE = "acme_onboarded";
const ONBOARDING_SKIPPED_COOKIE = "acme_onboarding_skipped";

export function middleware(req: NextRequest) {
  const { pathname } = req.nextUrl;

  // www → one public address, before anything else. See lib/canonical-host.ts:
  // social connect, cookies and OAuth callbacks all assume a single origin.
  const canonical = canonicalRedirect(
    req.headers.get("x-forwarded-host") ?? req.headers.get("host"),
    pathname + req.nextUrl.search,
    siteUrl(),
  );
  if (canonical) return NextResponse.redirect(canonical, 308);

  const token = req.cookies.get(ACCESS_COOKIE)?.value;
  // The cookie has three states, and the two redirects below want different
  // things from the unknown one — so it is read twice, on purpose.
  //
  //   "0"     → definitely not finished
  //   "1"     → definitely finished
  //   missing → we do not know
  //
  // Pushing somebody *into* onboarding on a guess would trap them, so that
  // redirect fires only on a definite "0". Pushing them *out of* it on a guess
  // locks them out of the only page that fixes their account, so that redirect
  // fires only on a definite "1". Unknown means "leave them alone" either way.
  const onboardedRaw = req.cookies.get(ONBOARDED_COOKIE)?.value;
  const notOnboarded = onboardedRaw === "0";
  const definitelyOnboarded = onboardedRaw === "1";

  // "I'll do this later." A session-only cookie, so it lapses at sign-out and
  // the next sign-in asks again — but while it is set, the app has to actually
  // let them work. Without this the redirect below would bounce them straight
  // back to the page they just chose to leave, which is not a skip button, it
  // is a broken one.
  const skipped = req.cookies.get(ONBOARDING_SKIPPED_COOKIE)?.value === "1";

  // Already signed in? Skip /login and /register.
  if (token && AUTH_PAGES.some((p) => pathname.startsWith(p))) {
    return NextResponse.redirect(
      new URL(notOnboarded ? "/onboarding" : "/dashboard", req.url),
    );
  }

  const isProtected = PROTECTED.some((p) => pathname.startsWith(p));

  if (isProtected) {
    if (!token) {
      const loginUrl = new URL("/login", req.url);
      loginUrl.searchParams.set("redirect", pathname);
      return NextResponse.redirect(loginUrl);
    }
    if (notOnboarded && !skipped) {
      return NextResponse.redirect(new URL("/onboarding", req.url));
    }
  }

  // /onboarding itself: bounce signed-in completed users straight to dashboard.
  //
  // Somebody who skipped is *not* bounced. They have unfinished setup and a
  // standing invitation to come back and finish it, so the page has to stay
  // reachable — that is the whole point of the prompt that follows them
  // around the app.
  //
  // Only a definite "1" bounces. This used to bounce on anything that was not
  // "0", which meant a missing or stale cookie locked an unfinished account
  // out of its own setup page: the prompt said "finish setup", the click went
  // to /onboarding, and middleware sent them to the dashboard. The prompt
  // reads the API and was right; the cookie was wrong and won. If the cookie
  // is now wrong in the other direction the page simply loads and tells them
  // what is left, which is a far cheaper mistake.
  if (pathname.startsWith("/onboarding")) {
    if (!token) {
      return NextResponse.redirect(new URL("/login", req.url));
    }
    if (definitelyOnboarded) {
      return NextResponse.redirect(new URL("/dashboard", req.url));
    }
  }

  return withCsp(req);
}

/**
 * Lets the request through with this request's CSP nonce.
 *
 * Next.js reads the nonce from the request's `content-security-policy` header
 * and puts it on every script it renders; the app's own inline scripts (theme
 * and structured data) read it from `x-nonce`. The browser gets the policy as
 * a report-only header until CSP_MODE=enforce — see lib/csp.ts.
 */
function withCsp(req: NextRequest): NextResponse {
  const mode = cspMode();
  if (mode === "off") return NextResponse.next();

  const nonce = newNonce();
  // The kiosk (/display) may be framed by signage players; nothing else may.
  const kiosk = req.nextUrl.pathname === "/display" || req.nextUrl.pathname.startsWith("/display/");
  const policy = buildCsp(nonce, {
    dev: process.env.NODE_ENV === "development",
    frameAncestors: mode === "enforce" && !kiosk ? "'none'" : undefined,
  });

  const requestHeaders = new Headers(req.headers);
  requestHeaders.set(NONCE_HEADER, nonce);
  requestHeaders.set("content-security-policy", policy);

  const res = NextResponse.next({ request: { headers: requestHeaders } });
  res.headers.set(
    mode === "enforce" ? "Content-Security-Policy" : "Content-Security-Policy-Report-Only",
    policy,
  );
  return res;
}

export const config = {
  // Every page, for the CSP nonce; the auth redirects above still apply only
  // to the paths they name. Not the API routes, static files or images, which
  // render no HTML.
  matcher: [
    "/((?!api/|_next/static|_next/image|favicon.ico|icon.svg|apple-icon.png|robots.txt|sitemap.xml|manifest.webmanifest|opengraph-image|marketing/|brand/).*)",
  ],
};

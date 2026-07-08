import { NextResponse, type NextRequest } from "next/server";

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
];

/** Pages that should redirect a signed-in user away. */
const AUTH_PAGES = ["/login", "/register"];

const ACCESS_COOKIE = "acme_access";
const ONBOARDED_COOKIE = "acme_onboarded";

export function middleware(req: NextRequest) {
  const { pathname } = req.nextUrl;
  const token = req.cookies.get(ACCESS_COOKIE)?.value;
  // We only redirect to onboarding when the flag is EXPLICITLY "0".
  // Missing cookie = treat as completed (default safe — avoids loops).
  const onboardedRaw = req.cookies.get(ONBOARDED_COOKIE)?.value;
  const onboarded = onboardedRaw !== "0";

  // Already signed in? Skip /login and /register.
  if (token && AUTH_PAGES.some((p) => pathname.startsWith(p))) {
    return NextResponse.redirect(
      new URL(onboarded ? "/dashboard" : "/onboarding", req.url),
    );
  }

  const isProtected = PROTECTED.some((p) => pathname.startsWith(p));

  if (isProtected) {
    if (!token) {
      const loginUrl = new URL("/login", req.url);
      loginUrl.searchParams.set("redirect", pathname);
      return NextResponse.redirect(loginUrl);
    }
    if (!onboarded) {
      return NextResponse.redirect(new URL("/onboarding", req.url));
    }
  }

  // /onboarding itself: bounce signed-in completed users straight to dashboard.
  if (pathname.startsWith("/onboarding")) {
    if (!token) {
      return NextResponse.redirect(new URL("/login", req.url));
    }
    if (onboarded) {
      return NextResponse.redirect(new URL("/dashboard", req.url));
    }
  }

  return NextResponse.next();
}

export const config = {
  matcher: [
    "/dashboard/:path*",
    "/canvas/:path*",
    "/brand-kit/:path*",
    "/posters/:path*",
    "/patients/:path*",
    "/campaigns/:path*",
    "/profile/:path*",
    "/onboarding/:path*",
    "/login",
    "/register",
  ],
};

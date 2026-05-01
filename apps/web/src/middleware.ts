import { NextResponse, type NextRequest } from "next/server";

const PROTECTED = ["/canvas", "/brand-kit", "/templates", "/posters"];
const ACCESS_COOKIE = "acme_access";

export function middleware(req: NextRequest) {
  const isProtected = PROTECTED.some((p) => req.nextUrl.pathname.startsWith(p));
  if (!isProtected) return NextResponse.next();

  const token = req.cookies.get(ACCESS_COOKIE)?.value;
  if (token) return NextResponse.next();

  const loginUrl = new URL("/login", req.url);
  loginUrl.searchParams.set("redirect", req.nextUrl.pathname);
  return NextResponse.redirect(loginUrl);
}

export const config = {
  matcher: [
    "/canvas/:path*",
    "/brand-kit/:path*",
    "/templates/:path*",
    "/posters/:path*",
  ],
};

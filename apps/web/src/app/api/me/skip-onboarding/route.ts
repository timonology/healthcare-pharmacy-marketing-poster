import { NextResponse } from "next/server";
import { readAccessToken, setOnboardingSkippedCookie } from "@/lib/server/cookies";

/**
 * Defers onboarding for the rest of this browser session.
 *
 * Nothing is sent upstream and nothing about the account changes — setup is
 * still unfinished, and `onboardingCompleted` stays false. All this records is
 * that the person asked to get on with their work first, which is why it lives
 * entirely in a session cookie rather than in the database.
 *
 * It is a POST, and it requires a session, because it changes what the app
 * does next. A GET would let any page on the internet defer a stranger's
 * onboarding with an <img> tag.
 */
export async function POST() {
  if (!(await readAccessToken())) {
    return NextResponse.json({ error: "Not signed in." }, { status: 401 });
  }

  return setOnboardingSkippedCookie(NextResponse.json({ skipped: true }));
}

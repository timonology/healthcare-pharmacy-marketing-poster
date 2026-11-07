import type { Metadata } from "next";

/** A sign-in form is not a search result anyone wants. */
export const metadata: Metadata = {
  title: "Sign in",
  robots: { index: false, follow: true },
};

export default function LoginLayout({ children }: { children: React.ReactNode }) {
  return children;
}

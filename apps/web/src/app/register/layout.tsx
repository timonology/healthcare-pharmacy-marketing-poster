import type { Metadata } from "next";
import { pageMetadata } from "@/lib/site";

export const metadata: Metadata = {
  title: "Start free",
  ...pageMetadata({
    title: "Start free",
    description: "Create a free Marketing25 account for your pharmacy. No credit card required.",
    path: "/register",
  }),
};

export default function RegisterLayout({ children }: { children: React.ReactNode }) {
  return children;
}

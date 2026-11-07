import Link from "next/link";
import { LogoMark } from "@/components/brand/LogoMark";

// Only links that go somewhere. Demo, Blog, About and Data processing
// pointed at anchors that never existed, which is a dead end for a visitor
// and a broken link for a crawler.
const COLUMNS = [
  {
    heading: "Product",
    links: [
      { href: "/#features", label: "Features" },
      { href: "/templates", label: "Templates" },
      { href: "/pricing", label: "Pricing" },
      { href: "/register", label: "Start free" },
    ],
  },
  {
    heading: "Company",
    links: [
      { href: "mailto:info@sonarinformatics.com", label: "Contact" },
      { href: "/login", label: "Sign in" },
    ],
  },
  {
    heading: "Legal",
    links: [
      { href: "/terms", label: "Terms of service" },
      { href: "/privacy", label: "Privacy policy" },
      { href: "/privacy/data-deletion", label: "Data deletion" },
    ],
  },
];

export function SiteFooter() {
  return (
    <footer className="border-t border-border/60 bg-muted/20">
      <div className="container mx-auto px-4 py-12 md:px-6">
        <div className="grid grid-cols-2 gap-10 md:grid-cols-4">
          <div className="col-span-2 md:col-span-1">
            <Link href="/" className="flex items-center gap-2">
              <LogoMark className="h-8 w-8" />
              <span className="text-base font-semibold tracking-tight">
                Marketing25
              </span>
            </Link>
            <p className="mt-4 max-w-xs text-sm text-muted-foreground">
              Professional marketing for UK community pharmacies: posters, videos, email campaigns, social posts and in-pharmacy screens.
            </p>
          </div>

          {COLUMNS.map((col) => (
            <div key={col.heading}>
              <p className="text-sm font-semibold">{col.heading}</p>
              <ul className="mt-4 space-y-2.5">
                {col.links.map((link) => (
                  <li key={link.href}>
                    <Link
                      href={link.href}
                      className="text-sm text-muted-foreground transition-colors hover:text-foreground"
                    >
                      {link.label}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>

        <div className="mt-12 flex flex-col items-center justify-between gap-4 border-t border-border/60 pt-8 text-xs text-muted-foreground md:flex-row">
          <p>© {new Date().getFullYear()} Marketing25. All rights reserved.</p>
          <p>Built with care for community pharmacies.</p>
        </div>
      </div>
    </footer>
  );
}

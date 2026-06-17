import Link from "next/link";
import { Pill } from "lucide-react";

const COLUMNS = [
  {
    heading: "Product",
    links: [
      { href: "#features", label: "Features" },
      { href: "/templates", label: "Templates" },
      { href: "/pricing", label: "Pricing" },
      { href: "#demo", label: "Demo" },
    ],
  },
  {
    heading: "Company",
    links: [
      { href: "#blog", label: "Blog" },
      { href: "#about", label: "About" },
      { href: "#contact", label: "Contact" },
    ],
  },
  {
    heading: "Legal",
    links: [
      { href: "#terms", label: "Terms of service" },
      { href: "#privacy", label: "Privacy policy" },
      { href: "#dpa", label: "Data processing" },
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
              <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-gradient-to-br from-primary to-cyan-500 text-primary-foreground shadow-sm">
                <Pill className="h-4 w-4" />
              </span>
              <span className="text-base font-semibold tracking-tight">
                Pharmacy Poster
              </span>
            </Link>
            <p className="mt-4 max-w-xs text-sm text-muted-foreground">
              The fastest way for pharmacies to design professional, regulator-friendly marketing posters.
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
          <p>© {new Date().getFullYear()} Pharmacy Poster. All rights reserved.</p>
          <p>Built with care for community pharmacies.</p>
        </div>
      </div>
    </footer>
  );
}

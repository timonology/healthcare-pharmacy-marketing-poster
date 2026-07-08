"use client";

import Link from "next/link";
import { Pill } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ThemeToggle } from "@/components/theme-toggle";

const NAV_LINKS = [
  { href: "#features", label: "Features" },
  { href: "#templates", label: "Templates" },
  { href: "#pricing", label: "Pricing" },
  { href: "#blog", label: "Blog" },
];

export function LandingNav() {
  return (
    <header className="sticky top-0 z-40 w-full border-b border-border/40 bg-background/80 backdrop-blur-md">
      <div className="container mx-auto flex h-16 items-center justify-between px-4 md:px-6">
        <Link href="/" className="flex items-center gap-2">
          <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-gradient-to-br from-primary to-emerald-500 text-primary-foreground shadow-sm">
            <Pill className="h-4 w-4" />
          </span>
          <span className="text-base font-semibold tracking-tight">
            Sonar Marketing25
          </span>
        </Link>

        <nav className="hidden items-center gap-1 md:flex">
          {NAV_LINKS.map((link) => (
            <a
              key={link.href}
              href={link.href}
              className="rounded-md px-3 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-accent hover:text-foreground"
            >
              {link.label}
            </a>
          ))}
        </nav>

        <div className="flex items-center gap-2">
          <ThemeToggle />
          <Button asChild variant="ghost" size="sm" className="hidden sm:inline-flex">
            <Link href="/login">Login</Link>
          </Button>
          <Button
            asChild
            size="sm"
            className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground shadow-sm hover:opacity-90"
          >
            <Link href="/register">Get Started Free</Link>
          </Button>
        </div>
      </div>
    </header>
  );
}

"use client";

import { useEffect, useRef, useState } from "react";
import { Eye, LogOut } from "lucide-react";
import { api } from "@/lib/api";

/**
 * Persistent banner shown while somebody is working as another pharmacy.
 *
 * This is the whole safety story on the client side. A delegated session makes
 * every page in the app show someone else's data, so it must be impossible to
 * forget you are in one — hence a full-width bar on every screen rather than a
 * badge tucked into a menu.
 *
 * <b>Whether it appears is decided on the server, not here.</b> The earlier
 * version read the readable `acme_manage_as_name` cookie and rendered whatever
 * it found. That cookie is only a label: it is not the session, it cannot prove
 * a session exists, and it was being written with a thirty-day lifetime while
 * the token it described lasted thirty minutes. The result was a banner
 * announcing "you are managing New Home" to people who had signed out — the
 * exact opposite of what a safety indicator is for, since it teaches people to
 * disbelieve it.
 *
 * So the root layout reads the httpOnly token cookie server-side and passes the
 * name down only when a delegated session genuinely exists. This component can
 * hide itself, and never show itself.
 */
export function ManageAsBanner({ pharmacyName }: { pharmacyName?: string | null }) {
  const [pharmacy, setPharmacy] = useState<string | null>(pharmacyName ?? null);
  const [leaving, setLeaving] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    setPharmacy(pharmacyName ?? null);
  }, [pharmacyName]);

  useEffect(() => {
    if (!pharmacyName) return;

    // Polling only ever *removes* the banner. The delegated session expires on
    // its own, and the label cookie now expires with it, so its disappearance
    // is a reliable signal that the session has lapsed. Trusting the cookie to
    // turn the banner back on is what caused the bug above.
    function check() {
      const stillThere = document.cookie
        .split("; ")
        .some((c) => c.startsWith("acme_manage_as_name="));

      if (!stillThere) setPharmacy(null);
    }

    const timer = setInterval(check, 15_000);
    return () => clearInterval(timer);
  }, [pharmacyName]);

  // Publishes the banner's height so the app headers can stick *below* it.
  //
  // Both this bar and every page header are `sticky top-0`, which pins them to
  // the same line: scroll down and the header slid underneath the banner, two
  // sets of text in one strip. It reads as a rendering fault and it is one.
  //
  // Measured rather than hardcoded because the message wraps to a second line
  // on a narrow screen, so the height is a function of the viewport and of the
  // pharmacy's name.
  useEffect(() => {
    const el = ref.current;
    const root = document.documentElement;

    if (!pharmacy || !el) {
      root.style.removeProperty("--banner-h");
      return;
    }

    const observer = new ResizeObserver(([entry]) => {
      root.style.setProperty("--banner-h", `${Math.round(entry.contentRect.height)}px`);
    });
    observer.observe(el);

    return () => {
      observer.disconnect();
      root.style.removeProperty("--banner-h");
    };
  }, [pharmacy]);

  if (!pharmacy) return null;

  async function leave() {
    setLeaving(true);
    await api.pharmacies.endManageAs();
    setPharmacy(null);
    // Full reload rather than a client navigation: every cached page and store
    // in memory holds the other pharmacy's data, and a soft transition would
    // leave some of it on screen under the person's own account.
    window.location.href = "/pharmacies";
  }

  return (
    <div
      ref={ref}
      className={[
        "sticky top-0 z-40 border-b border-amber-500/40",
        // Opaque, deliberately. This bar is sticky over scrolling content, and
        // a translucent background let that content show straight through it —
        // the page text and the warning rendered on top of each other and
        // neither could be read. A warning you cannot read is not a warning.
        // Solid amber in both themes, with no alpha anywhere in the stack.
        "bg-amber-100 text-amber-950",
        "dark:bg-amber-950 dark:text-amber-100",
      ].join(" ")}
    >
      <div className="container flex flex-wrap items-center justify-between gap-2 py-2">
        <p className="flex items-center gap-2 text-sm">
          <Eye className="h-4 w-4 shrink-0" aria-hidden />
          <span>
            You are managing <strong className="font-semibold">{pharmacy}</strong>.
            Anything you change here belongs to them, and is recorded against
            your name.
          </span>
        </p>

        <button
          type="button"
          onClick={leave}
          disabled={leaving}
          className="flex items-center gap-1.5 rounded-md border border-amber-600/50 bg-amber-50 px-2.5 py-1 text-xs font-medium transition-colors hover:bg-amber-200 disabled:opacity-60 dark:border-amber-400/40 dark:bg-amber-900 dark:hover:bg-amber-800"
        >
          <LogOut className="h-3.5 w-3.5" />
          {leaving ? "Leaving…" : "Back to my account"}
        </button>
      </div>
    </div>
  );
}

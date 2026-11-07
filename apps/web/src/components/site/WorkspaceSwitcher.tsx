"use client";

import { Fragment, useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { Building2, ChevronDown, Loader2, ShieldCheck } from "lucide-react";
import type { Workspaces } from "@acme/shared-types";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTick,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import { useAuthStore } from "@/store/auth-store";

/**
 * "Working as {pharmacy}", and the way to change it.
 *
 * <b>The chip is the point, not the dropdown.</b> Somebody can be acting as
 * another pharmacy for an entire session; every poster, campaign and contact
 * they touch belongs to that pharmacy, and without a permanent indicator the
 * only clue is remembering they clicked something several screens ago. Sending
 * a flu campaign from the wrong branch is not a recoverable mistake.
 *
 * The menu is portalled (see ui/dropdown-menu). The first version positioned
 * itself with `absolute` inside the sidebar, which is a scrolling container —
 * so it was clipped by that container and drawn among the navigation links
 * instead of over them.
 */
export function WorkspaceSwitcher({ className }: { className?: string }) {
  const [data, setData] = useState<Workspaces | null>(null);
  const [switching, setSwitching] = useState<string | null>(null);
  const user = useAuthStore((s) => s.user);

  const isStaff = user?.role === "Admin" || user?.role === "SuperAdmin";

  const load = useCallback(async () => {
    // Failing quietly is right here: this is chrome on every authenticated
    // page, and an error banner across the top of the app because one
    // background call failed would be worse than no switcher.
    setData(await api.workspaces().catch(() => null));
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function choose(pharmacyId: string) {
    if (pharmacyId === data?.currentPharmacyId) return;

    setSwitching(pharmacyId);
    try {
      await api.switchWorkspace(pharmacyId);
      // A full reload rather than a re-render. Every list and store in the app
      // was loaded for the previous pharmacy; reconciling them in place is a
      // lot of machinery to get subtly wrong, and the failure mode is showing
      // one pharmacy's data under another's name.
      window.location.reload();
    } catch {
      setSwitching(null);
      void load();
    }
  }

  if (!data) return null;

  // Nothing to show for somebody with a single pharmacy who is inside it —
  // unless they are staff, who still need the route into the admin console.
  const worthShowing = data.workspaces.length > 1 || data.isActingAsAnother || isStaff;
  if (!worthShowing) return null;

  const elsewhere = data.isActingAsAnother;

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className={cn(
            "flex w-full items-center gap-2 rounded-full border px-3 py-1.5 text-xs font-medium transition-colors",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            elsewhere
              ? "border-amber-500/40 bg-amber-500/10 text-amber-900 hover:bg-amber-500/20 dark:text-amber-200"
              : "border-border/60 bg-muted/50 text-foreground hover:bg-accent",
            className,
          )}
        >
          <Building2 className="h-3.5 w-3.5 shrink-0" aria-hidden />
          <span className="min-w-0 flex-1 truncate text-left">
            <span className="hidden text-muted-foreground sm:inline">Working as </span>
            {data.currentPharmacyName}
          </span>
          <ChevronDown className="h-3.5 w-3.5 shrink-0 opacity-60" aria-hidden />
        </button>
      </DropdownMenuTrigger>

      {/* Anchored to the start edge and sized to the sidebar, so it reads as
          belonging to the chip rather than floating loose over the page. */}
      <DropdownMenuContent align="start" className="w-[16rem]">
        {/* The badge answers "what kind of account is this?" where the
            switching happens, rather than only on a page they may never open.
            Read from the account, so it stays put while acting as a branch. */}
        <DropdownMenuLabel className="flex items-center justify-between gap-2">
          <span>Switch pharmacy</span>
          {data.isHeadOffice && (
            <span className="rounded-full bg-primary/10 px-1.5 py-0.5 text-[10px] font-medium text-primary">
              Head office
            </span>
          )}
        </DropdownMenuLabel>

        {data.workspaces.map((w, i) => (
          <Fragment key={w.pharmacyId}>
            {/* Branches get their own heading, once, before the first of them.
                A flat list of eight names says nothing about which are yours
                and which you merely manage. */}
            {data.isHeadOffice
              && !w.isOwn
              && w.canManage
              && !data.workspaces.slice(0, i).some((p) => !p.isOwn && p.canManage) && (
              <DropdownMenuLabel className="pt-2">Branches</DropdownMenuLabel>
            )}
          <DropdownMenuItem
            disabled={switching !== null}
            onSelect={(e) => {
              // Keeps the menu open while the switch is in flight, so the
              // spinner is visible rather than the menu vanishing into a
              // seemingly frozen page.
              e.preventDefault();
              void choose(w.pharmacyId);
            }}
          >
            {switching === w.pharmacyId ? (
              <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center">
                <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
              </span>
            ) : (
              <DropdownMenuTick selected={w.isCurrent} />
            )}

            <span className="min-w-0 flex-1">
              <span className="block truncate font-medium">{w.pharmacyName}</span>
              <span className="block truncate text-[11px] text-muted-foreground">
                {w.isOwn ? "Your pharmacy" : w.canManage ? "You manage this" : w.email}
              </span>
            </span>
          </DropdownMenuItem>
          </Fragment>
        ))}

        {isStaff && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem asChild>
              <Link href="/admin">
                <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                <span className="min-w-0 flex-1">
                  <span className="block font-medium">Admin console</span>
                  <span className="block text-[11px] text-muted-foreground">
                    Marketing25 staff
                  </span>
                </span>
              </Link>
            </DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

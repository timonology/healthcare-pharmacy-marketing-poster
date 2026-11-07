"use client";

import * as React from "react";
import { MoreHorizontal } from "lucide-react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";

/**
 * The secondary actions for one item, behind a single control.
 *
 * <b>A card cannot hold an unbounded row of buttons.</b> The poster cards grew
 * to six — Open, Share, PDF, Duplicate, Publish, Delete — inside a column a
 * third of the screen wide. They were laid out in a flex row that did not wrap,
 * inside a card with `overflow-hidden`, so the row ran off the edge and Delete
 * was simply not on screen. Nothing was misaligned; the button existed, was
 * clickable in the DOM, and could not be seen or reached.
 *
 * Shrinking the buttons would have bought one more action before the same thing
 * happened again, and shrinking them to bare icons would cost the labels — the
 * exact complaint already made about an unlabelled control in the poster
 * designer. An overflow menu keeps every label, fits any number of actions, and
 * puts a deliberate gap between "Duplicate" and "Delete".
 *
 * The menu is the portalled primitive, so it is not clipped by the card that
 * contains it.
 */
export function ActionMenu({
  label,
  children,
  align = "end",
  className,
}: {
  /**
   * What this menu belongs to, e.g. "More actions for Flu clinic poster".
   *
   * Required, and specific on purpose: a grid of cards produces one of these
   * per card, and a screen reader hearing "More actions" a dozen times over has
   * been told nothing about which poster is about to be deleted.
   */
  label: string;
  children: React.ReactNode;
  align?: "start" | "end";
  className?: string;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={label}
          className={cn(
            "inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md",
            "text-muted-foreground transition-colors hover:bg-accent hover:text-foreground",
            "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background",
            className,
          )}
        >
          <MoreHorizontal className="h-4 w-4" aria-hidden />
        </button>
      </DropdownMenuTrigger>

      <DropdownMenuContent align={align} className="w-[13rem]">
        {children}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

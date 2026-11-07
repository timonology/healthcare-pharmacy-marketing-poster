"use client";

import * as React from "react";
import * as DropdownMenuPrimitive from "@radix-ui/react-dropdown-menu";
import { Check } from "lucide-react";
import { cn } from "@/lib/utils";

/**
 * A menu that floats above everything, wherever its trigger happens to live.
 *
 * <b>The portal is the point.</b> Both menus in the app shell previously
 * positioned themselves with `absolute` inside the sidebar — which is a
 * scrolling container (`overflow-y-auto`). An absolutely positioned child is
 * still laid out and clipped inside its scrolling ancestor, so both menus were
 * cut off and drawn among the navigation links rather than over them. No
 * z-index can fix that: the element is not on top of the sidebar, it is inside
 * it.
 *
 * Radix renders the content into `document.body`, outside every overflow
 * context, and brings with it the parts that are tedious to hand-roll and easy
 * to get subtly wrong: focus moves into the menu and returns to the trigger on
 * close, Escape and outside-click dismiss, arrow keys and typeahead navigate,
 * and the panel flips or shifts to stay on screen near an edge — which is what
 * makes these usable on a phone.
 */
export const DropdownMenu = DropdownMenuPrimitive.Root;
export const DropdownMenuTrigger = DropdownMenuPrimitive.Trigger;
export const DropdownMenuGroup = DropdownMenuPrimitive.Group;

/**
 * Above the sidebar, and above the manage-as banner.
 *
 * The shell stacks: page headers at z-30, the manage-as banner at z-40, the
 * mobile navigation drawer at z-50. A menu has to clear all three — including
 * the banner, which spans the top of every page in exactly the situation where
 * a menu most needs to be readable, since it only appears when you are working
 * as somebody else's pharmacy.
 */
const Z = "z-[60]";

export const DropdownMenuContent = React.forwardRef<
  React.ElementRef<typeof DropdownMenuPrimitive.Content>,
  React.ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Content>
>(({ className, sideOffset = 6, collisionPadding = 12, ...props }, ref) => (
  <DropdownMenuPrimitive.Portal>
    <DropdownMenuPrimitive.Content
      ref={ref}
      sideOffset={sideOffset}
      // Keeps the panel clear of the viewport edge rather than flush against
      // it, which is what makes a menu anchored to the sidebar readable on a
      // narrow screen.
      collisionPadding={collisionPadding}
      className={cn(
        Z,
        "min-w-[12rem] overflow-hidden rounded-lg border border-border/60 bg-popover p-1 text-popover-foreground shadow-lg",
        // Never taller than the space available, so a long list of pharmacies
        // scrolls inside the menu instead of running off the screen.
        "max-h-[var(--radix-dropdown-menu-content-available-height)] overflow-y-auto",
        "data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0",
        className,
      )}
      {...props}
    />
  </DropdownMenuPrimitive.Portal>
));
DropdownMenuContent.displayName = "DropdownMenuContent";

export const DropdownMenuItem = React.forwardRef<
  React.ElementRef<typeof DropdownMenuPrimitive.Item>,
  React.ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Item> & { inset?: boolean }
>(({ className, inset, ...props }, ref) => (
  <DropdownMenuPrimitive.Item
    ref={ref}
    className={cn(
      "relative flex cursor-pointer select-none items-start gap-2 rounded-md px-2.5 py-2 text-sm outline-none transition-colors",
      "focus:bg-accent focus:text-accent-foreground",
      "data-[disabled]:pointer-events-none data-[disabled]:opacity-60",
      inset && "pl-8",
      className,
    )}
    {...props}
  />
));
DropdownMenuItem.displayName = "DropdownMenuItem";

export const DropdownMenuLabel = React.forwardRef<
  React.ElementRef<typeof DropdownMenuPrimitive.Label>,
  React.ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Label>
>(({ className, ...props }, ref) => (
  <DropdownMenuPrimitive.Label
    ref={ref}
    className={cn(
      "px-2.5 py-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground",
      className,
    )}
    {...props}
  />
));
DropdownMenuLabel.displayName = "DropdownMenuLabel";

export const DropdownMenuSeparator = React.forwardRef<
  React.ElementRef<typeof DropdownMenuPrimitive.Separator>,
  React.ComponentPropsWithoutRef<typeof DropdownMenuPrimitive.Separator>
>(({ className, ...props }, ref) => (
  <DropdownMenuPrimitive.Separator
    ref={ref}
    className={cn("-mx-1 my-1 h-px bg-border/60", className)}
    {...props}
  />
));
DropdownMenuSeparator.displayName = "DropdownMenuSeparator";

/**
 * A fixed-width tick column, present whether or not the item is selected.
 *
 * Reserving the space unconditionally keeps every label on the same left edge.
 * Rendering the tick only when selected shifts the other rows, which reads as
 * the menu twitching as you arrow through it.
 */
export function DropdownMenuTick({ selected }: { selected: boolean }) {
  return (
    <span className="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center" aria-hidden>
      {selected ? <Check className="h-4 w-4 text-primary" /> : null}
    </span>
  );
}

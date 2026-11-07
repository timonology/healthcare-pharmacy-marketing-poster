"use client";

import type { LucideIcon } from "lucide-react";
import { AlertCircle, Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";

/** Shared presentational pieces for the admin console. */

/* ------------------------------------------------------------------ *
 * Sorting
 * ------------------------------------------------------------------ */

/**
 * A column header that toggles between two sort options.
 *
 * Sorting is server-side — the table shows one page at a time, and sorting the
 * 25 rows currently in memory would reorder the page rather than the result
 * set, which looks like it works until you reach page two.
 */
export function SortHeader<T extends string>({
  label,
  asc,
  desc,
  current,
  onSort,
  className,
}: {
  label: string;
  /** Sort value applied on first click. */
  asc: T;
  /** Sort value applied when already sorted by `asc`. Omit for one-way sorts. */
  desc?: T;
  current: T;
  onSort: (next: T) => void;
  className?: string;
}) {
  const isAsc = current === asc;
  const isDesc = desc !== undefined && current === desc;
  const active = isAsc || isDesc;

  return (
    <th className={cn("px-4 py-2 font-medium", className)}>
      <button
        type="button"
        onClick={() => onSort(isAsc && desc !== undefined ? desc : asc)}
        className={cn(
          "inline-flex items-center gap-1 transition-colors hover:text-foreground",
          active && "text-foreground",
        )}
        aria-sort={isAsc ? "ascending" : isDesc ? "descending" : "none"}
      >
        {label}
        <span aria-hidden className={cn("text-[10px]", !active && "opacity-30")}>
          {isDesc ? "▼" : "▲"}
        </span>
      </button>
    </th>
  );
}

/* ------------------------------------------------------------------ *
 * Loading + empty states
 * ------------------------------------------------------------------ */

/**
 * A single shimmering block.
 *
 * Skeletons rather than spinners for content that has a known shape: they hold
 * the layout at its final size, so cards and tables don't jump when data
 * lands. A spinner is right only when the eventual size is unknown.
 */
export function Skeleton({ className }: { className?: string }) {
  return <div className={cn("animate-pulse rounded-md bg-muted", className)} />;
}

/** Placeholder matching StatCard's dimensions, so the grid doesn't reflow. */
export function StatCardSkeleton() {
  return (
    <div className="rounded-lg bg-muted/40 p-4">
      <Skeleton className="h-3 w-20" />
      <Skeleton className="mt-2.5 h-7 w-14" />
      <Skeleton className="mt-2 h-2.5 w-24" />
    </div>
  );
}

/** Placeholder for a chart panel — a disc and a few legend rows. */
export function ChartSkeleton() {
  return (
    <div className="flex flex-wrap items-center gap-6 p-4">
      <Skeleton className="h-40 w-40 shrink-0 rounded-full" />
      <div className="min-w-0 flex-1 space-y-3">
        {[0, 1, 2].map((i) => (
          <Skeleton key={i} className="h-4 w-full" />
        ))}
      </div>
    </div>
  );
}

/** Placeholder for a list or feed. */
export function ListSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <div className="space-y-3 p-4">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="flex items-center gap-3">
          <Skeleton className="h-8 w-8 shrink-0 rounded-full" />
          <div className="min-w-0 flex-1 space-y-1.5">
            <Skeleton className="h-3 w-1/3" />
            <Skeleton className="h-2.5 w-2/3" />
          </div>
        </div>
      ))}
    </div>
  );
}

/**
 * Empty state with an optional call to action.
 *
 * Distinguishes "nothing exists yet" from "your filters match nothing" — the
 * first needs an action, the second needs the filters cleared, and conflating
 * them leaves people staring at a dead end wondering which it is.
 */
export function EmptyState({
  icon: Icon,
  title,
  message,
  action,
}: {
  icon?: LucideIcon;
  title: string;
  message?: string;
  action?: React.ReactNode;
}) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-12 text-center">
      {Icon && (
        <div className="mb-1 rounded-full bg-muted p-3">
          <Icon className="h-5 w-5 text-muted-foreground" aria-hidden />
        </div>
      )}
      <p className="text-sm font-medium">{title}</p>
      {message && (
        <p className="max-w-sm text-xs text-muted-foreground">{message}</p>
      )}
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}

export function StatCard({
  label,
  value,
  hint,
  tone = "default",
}: {
  label: string;
  value: string | number;
  hint?: string;
  tone?: "default" | "positive" | "warning";
}) {
  return (
    <div className="rounded-lg bg-muted/40 p-4">
      <p className="text-xs font-medium text-muted-foreground">{label}</p>
      <p
        className={cn(
          "mt-1 text-2xl font-semibold tracking-tight",
          tone === "positive" && "text-primary",
          tone === "warning" && "text-destructive",
        )}
      >
        {typeof value === "number" ? value.toLocaleString() : value}
      </p>
      {hint && <p className="mt-1 text-[11px] text-muted-foreground">{hint}</p>}
    </div>
  );
}

export function Panel({
  title,
  description,
  action,
  children,
}: {
  title: string;
  description?: string;
  action?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <section className="rounded-lg border border-border/60 bg-card">
      <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border/60 px-4 py-3">
        <div>
          <h2 className="text-sm font-semibold tracking-tight">{title}</h2>
          {description && (
            <p className="mt-0.5 text-xs text-muted-foreground">{description}</p>
          )}
        </div>
        {action}
      </header>
      {children}
    </section>
  );
}

export function ErrorNote({ message }: { message: string }) {
  return (
    <div
      role="alert"
      className="flex items-start gap-2 rounded-md border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
    >
      <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
      <span>{message}</span>
    </div>
  );
}

export function TableLoading({ colSpan }: { colSpan: number }) {
  return (
    <tr>
      <td colSpan={colSpan} className="px-4 py-10 text-center">
        <Loader2 className="mx-auto h-5 w-5 animate-spin text-muted-foreground" />
      </td>
    </tr>
  );
}

export function TableEmpty({ colSpan, message }: { colSpan: number; message: string }) {
  return (
    <tr>
      <td colSpan={colSpan} className="px-4 py-10 text-center text-sm text-muted-foreground">
        {message}
      </td>
    </tr>
  );
}

export function RoleBadge({ role }: { role: string }) {
  const styles: Record<string, string> = {
    SuperAdmin: "bg-primary/10 text-primary",
    Admin: "bg-secondary text-secondary-foreground",
    PharmacyUser: "bg-muted text-muted-foreground",
  };
  return (
    <span
      className={cn(
        "inline-flex rounded-full px-2 py-0.5 text-[11px] font-medium",
        styles[role] ?? styles.PharmacyUser,
      )}
    >
      {role === "PharmacyUser" ? "Pharmacy" : role}
    </span>
  );
}

export function TierBadge({ tier }: { tier: string }) {
  const styles: Record<string, string> = {
    Pro: "bg-primary/10 text-primary",
    Starter: "bg-secondary text-secondary-foreground",
    Free: "bg-muted text-muted-foreground",
  };
  return (
    <span
      className={cn(
        "inline-flex rounded-full px-2 py-0.5 text-[11px] font-medium",
        styles[tier] ?? styles.Free,
      )}
    >
      {tier}
    </span>
  );
}

export function StatusDot({ active }: { active: boolean }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-xs">
      <span
        aria-hidden
        className={cn(
          "h-1.5 w-1.5 rounded-full",
          active ? "bg-primary" : "bg-muted-foreground/50",
        )}
      />
      {active ? "Active" : "Inactive"}
    </span>
  );
}

export function Pagination({
  skip,
  take,
  total,
  onChange,
}: {
  skip: number;
  take: number;
  total: number;
  onChange: (skip: number) => void;
}) {
  const from = total === 0 ? 0 : skip + 1;
  const to = Math.min(skip + take, total);

  return (
    <div className="flex items-center justify-between border-t border-border/60 px-4 py-3 text-xs text-muted-foreground">
      <span>
        {from}–{to} of {total.toLocaleString()}
      </span>
      <div className="flex gap-1">
        <button
          type="button"
          disabled={skip === 0}
          onClick={() => onChange(Math.max(0, skip - take))}
          className="rounded-md border border-border/60 px-2.5 py-1 transition-colors hover:bg-accent disabled:opacity-40"
        >
          Previous
        </button>
        <button
          type="button"
          disabled={to >= total}
          onClick={() => onChange(skip + take)}
          className="rounded-md border border-border/60 px-2.5 py-1 transition-colors hover:bg-accent disabled:opacity-40"
        >
          Next
        </button>
      </div>
    </div>
  );
}

/** Consistent short date across every admin table. */
export function formatDate(value: string | null | undefined): string {
  if (!value) return "—";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleDateString(undefined, {
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}

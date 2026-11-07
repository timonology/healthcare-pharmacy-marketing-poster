"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import {
  POSTER_STATUSES,
  type CurrentSubscription,
  type PosterStatus,
  type PosterSummary,
} from "@acme/shared-types";
import { Copy, FileDown, Repeat, Share2, Sparkles, Trash2 } from "lucide-react";
import { PublishDialog } from "@/components/social/PublishDialog";
import { RecurringDialog } from "@/components/social/RecurringDialog";
import { Badge } from "@/components/ui/badge";
import { GeneratePosterDialog } from "@/components/posters/GeneratePosterDialog";
import { PosterCardImage } from "@/components/posters/PosterCardImage";
import { Button } from "@/components/ui/button";
import { ActionMenu } from "@/components/ui/action-menu";
import {
  DropdownMenuItem,
  DropdownMenuSeparator,
} from "@/components/ui/dropdown-menu";
import {
  Card,
  CardContent,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";
import { NoResultsInRange, SortDateControls } from "@/components/lists/SortDateControls";
import { useListWindow } from "@/hooks/useListWindow";
import { hasDateRange, listWindowKey, listWindowParams } from "@/lib/list-window";
import { usePharmacyTokenValues } from "@/store/token-values-store";

type Filter = "All" | PosterStatus;

export default function MyPostersPage() {
  const router = useRouter();
  const [items, setItems] = useState<PosterSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [filter, setFilter] = useState<Filter>("All");
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [loading, setLoading] = useState(true);
  const [aiOpen, setAiOpen] = useState(false);
  // Bumped to re-run the list effect below. The fetch lives inside that effect
  // because it depends on the filter and the debounced search, and lifting it
  // out to call from here would mean duplicating both — so the refresh is a
  // dependency change rather than a second copy of the query.
  const [reloadKey, setReloadKey] = useState(0);
  /** Drives the allowance line in the AI dialog. Null until it loads. */
  const [subscription, setSubscription] = useState<CurrentSubscription | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  /** The poster whose share dialog is open, or null. */
  const [sharing, setSharing] = useState<PosterSummary | null>(null);
  /** The poster being made into a recurring post, or null. */
  const [recurring, setRecurring] = useState<PosterSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { listWindow, setListWindow, ready } = useListWindow();
  const windowKey = listWindowKey(listWindow);

  /** Every filter back to its default. Sort is a preference and stays. */
  function clearFilters() {
    setSearch("");
    setDebounced("");
    setFilter("All");
    setListWindow({ ...listWindow, from: null, to: null });
  }

  // Loaded once here rather than inside the dialog: the dialog opens on a

  // click, and fetching then would show "Checking your plan…" every time

  // somebody opens it.

  const refreshSubscription = useCallback(async () => {

    setSubscription(await api.getCurrentSubscription().catch(() => null));

  }, []);

  useEffect(() => { void refreshSubscription(); }, [refreshSubscription]);

  useEffect(() => {
    const t = setTimeout(() => setDebounced(search.trim()), 250);
    return () => clearTimeout(t);
  }, [search]);

  useEffect(() => {
    // Wait for the URL's sort and dates, so a shared link fetches once.
    if (!ready) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    (async () => {
      try {
        const page = await api.listPosters({
          status: filter === "All" ? undefined : filter,
          search: debounced || undefined,
          ...listWindowParams(listWindow),
          take: 60,
        });
        if (cancelled) return;
        setItems(page.items);
        setTotal(page.total);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load");
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
    // listWindow is covered by windowKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter, debounced, reloadKey, ready, windowKey]);

  async function createBlank() {
    try {
      const poster = await api.createPoster({ name: "Untitled poster" });
      router.push(`/posters/${poster.id}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create");
    }
  }

  async function duplicate(id: string) {
    try {
      setBusyId(id);
      const copy = await api.duplicatePoster(id);
      setItems((prev) => [
        {
          id: copy.id,
          ownerId: copy.ownerId,
          name: copy.name,
          sourceTemplateId: copy.sourceTemplateId,
          thumbnailUrl: copy.thumbnailUrl,
          canvas: copy.canvas,
          status: copy.status,
          createdAtUtc: copy.createdAtUtc,
          updatedAtUtc: copy.updatedAtUtc,
        },
        ...prev,
      ]);
      setTotal((t) => t + 1);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Duplicate failed");
    } finally {
      setBusyId(null);
    }
  }

  async function remove(id: string) {
    if (!confirm("Delete this poster? This cannot be undone.")) return;
    try {
      setBusyId(id);
      await api.deletePoster(id);
      setItems((prev) => prev.filter((p) => p.id !== id));
      setTotal((t) => Math.max(0, t - 1));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Delete failed");
    } finally {
      setBusyId(null);
    }
  }

  async function setStatus(id: string, status: PosterStatus) {
    try {
      setBusyId(id);
      const updated = await api.setPosterStatus(id, status);
      setItems((prev) =>
        prev.map((p) =>
          p.id === id ? { ...p, status: updated.status, updatedAtUtc: updated.updatedAtUtc } : p,
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : "Status update failed");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <main className="container max-w-6xl py-8">
      <header className="flex flex-col gap-2 md:flex-row md:items-end md:justify-between">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">My posters</h1>
          <p className="text-sm text-muted-foreground">
            {loading
              ? "Loading…"
              : `${total} poster${total === 1 ? "" : "s"}`}
          </p>
        </div>
        <div className="flex gap-2">
          <Button asChild variant="outline">
            <Link href="/templates">Browse templates</Link>
          </Button>
          <Button variant="outline" onClick={createBlank}>New blank poster</Button>
          {/* Primary, because it is the one that saves a pharmacist the most
              work — a blank canvas is the fallback, not the headline. */}
          <Button
            onClick={() => setAiOpen(true)}
            className="bg-brand-gradient text-primary-foreground shadow-md hover:opacity-90"
          >
            <Sparkles className="mr-1.5 h-4 w-4" />
            Design with AI
          </Button>
        </div>
      </header>

      <div className="mt-6 flex flex-col gap-4 md:flex-row md:items-center">
        <Input
          placeholder="Search posters…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="md:max-w-xs"
        />
        <div className="flex flex-wrap gap-2">
          <FilterChip active={filter === "All"} onClick={() => setFilter("All")}>
            All
          </FilterChip>
          {POSTER_STATUSES.map((s) => (
            <FilterChip
              key={s}
              active={filter === s}
              onClick={() => setFilter(s)}
            >
              {s}
            </FilterChip>
          ))}
        </div>
        <SortDateControls value={listWindow} onChange={setListWindow} dateField="Last updated" />
      </div>

      {error && (
        <p className="mt-4 text-sm text-destructive" role="alert">
          {error}
        </p>
      )}

      <section className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {loading
          ? Array.from({ length: 6 }).map((_, i) => <SkeletonCard key={i} />)
          : items.length === 0 && hasDateRange(listWindow)
          ? <NoResultsInRange onClear={clearFilters} />
          : items.length === 0
          ? (
            <EmptyState onCreate={createBlank} />
          )
          : items.map((p) => (
            <Card key={p.id} className="flex flex-col overflow-hidden">
              <PosterThumb summary={p} />
              <CardHeader className="flex-1">
                <div className="flex items-start justify-between gap-2">
                  <CardTitle className="line-clamp-2">{p.name}</CardTitle>
                  <StatusBadge status={p.status} />
                </div>
              </CardHeader>
              <CardContent className="space-y-1">
                <p className="text-xs text-muted-foreground">
                  Updated {new Date(p.updatedAtUtc).toLocaleString()}
                </p>
                {/* What the status actually means, next to the button that
                    changes it. Publishing used to move a label and nothing
                    else; now it decides whether this poster can go to
                    patients, so the card says so. */}
                <p className="text-xs text-muted-foreground">
                  {statusMeaning(p.status)}
                </p>
              </CardContent>
              {/*
                Three controls, not six.
                  · Open — what you came here to do.
                  · The status change — the everyday action, and its label says
                    which way it goes, so it earns its place on the card.
                  · Everything else, behind one menu.

                Six buttons did not fit in a column a third of the screen wide.
                They were in a row that could not wrap, inside a card that
                clips its contents, so Delete was pushed off the edge and could
                not be seen or clicked at all.
              */}
              <CardFooter className="flex items-center justify-between gap-2">
                <Button asChild variant="default" size="sm">
                  <Link href={`/posters/${p.id}`}>Open</Link>
                </Button>

                <div className="flex min-w-0 items-center gap-1">
                  <Button
                    size="sm"
                    variant="secondary"
                    onClick={() => setStatus(p.id, nextStatus(p.status))}
                    disabled={busyId === p.id}
                    title={statusActionHint(p.status)}
                  >
                    {statusActionLabel(p.status)}
                  </Button>

                  <ActionMenu label={`More actions for ${p.name}`}>
                    {/*
                      "Share" rather than "Publish": the status button beside
                      this already means publish-to-the-gallery, and two
                      controls with the same word doing different things is how
                      someone posts a draft to a pharmacy's Facebook page by
                      accident.
                    */}
                    <DropdownMenuItem
                      disabled={p.status !== "Published"}
                      onSelect={() => setSharing(p)}
                    >
                      <Share2 className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                      <span className="min-w-0 flex-1">
                        <span className="block">Share to social</span>
                        {/* The reason travels with the disabled control. A
                            greyed-out item that says nothing is a dead end. */}
                        {p.status !== "Published" && (
                          <span className="block text-[11px] text-muted-foreground">
                            Publish it first
                          </span>
                        )}
                      </span>
                    </DropdownMenuItem>

                    <DropdownMenuItem
                      disabled={p.status !== "Published"}
                      onSelect={() => setRecurring(p)}
                    >
                      <Repeat className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                      <span className="min-w-0 flex-1">
                        <span className="block">Make recurring</span>
                        {p.status !== "Published" && (
                          <span className="block text-[11px] text-muted-foreground">
                            Publish it first
                          </span>
                        )}
                      </span>
                    </DropdownMenuItem>

                    <DropdownMenuItem asChild className="items-center">
                      <a href={api.posterPdfUrl(p.id)}>
                        <FileDown className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                        Download PDF
                      </a>
                    </DropdownMenuItem>

                    <DropdownMenuItem
                      className="items-center"
                      disabled={busyId === p.id}
                      onSelect={() => duplicate(p.id)}
                    >
                      <Copy className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
                      Duplicate
                    </DropdownMenuItem>

                    {/* Separated, and last. Delete used to sit immediately
                        beside Duplicate at the same size. */}
                    <DropdownMenuSeparator />
                    <DropdownMenuItem
                      className="items-center text-destructive focus:bg-destructive/10 focus:text-destructive"
                      disabled={busyId === p.id}
                      onSelect={() => remove(p.id)}
                    >
                      <Trash2 className="h-4 w-4 shrink-0" aria-hidden />
                      Delete poster
                    </DropdownMenuItem>
                  </ActionMenu>
                </div>
              </CardFooter>
            </Card>
          ))}
      </section>

      <PublishDialog
        open={sharing !== null}
        onClose={() => setSharing(null)}
        sourceKind="Poster"
        sourceId={sharing?.id ?? ""}
        sourceName={sharing?.name ?? ""}
        isVideo={false}
      />
      <RecurringDialog
        open={recurring !== null}
        onClose={() => setRecurring(null)}
        source={recurring ? { kind: "Poster", id: recurring.id, name: recurring.name, imageUrl: recurring.thumbnailUrl } : null}
        onSaved={() => router.push("/social?tab=recurring")}
      />
      <GeneratePosterDialog
        open={aiOpen}
        onOpenChange={setAiOpen}
        subscription={subscription}
        onGenerated={async () => {
          // Both: the new poster changes the list, and the design just
          // spent consumes an allowance the dialog reports.
          setReloadKey((k) => k + 1);
          await refreshSubscription();
        }}
      />

      </main>
  );
}

function StatusBadge({ status }: { status: PosterStatus }) {
  const variant: "default" | "secondary" | "outline" =
    status === "Published" ? "default" : status === "Draft" ? "secondary" : "outline";
  return <Badge variant={variant}>{status}</Badge>;
}

function FilterChip({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <Button
      type="button"
      variant={active ? "default" : "outline"}
      size="sm"
      onClick={onClick}
    >
      {children}
    </Button>
  );
}

/**
 * Where the status button takes a poster next, and what it should say.
 *
 * Draft → Published → Archived → back to Draft. Spelled out here rather than
 * as a ternary chain in the card so the label and the action cannot drift
 * apart — a button reading "Archive" that publishes is worse than no button.
 */
function statusMeaning(status: PosterSummary["status"]): string {
  if (status === "Published") return "Ready to send in campaigns and to social.";
  if (status === "Draft") return "A work in progress — publish it to send it out.";
  return "Retired. Restore it to a draft to use it again.";
}

function nextStatus(status: PosterSummary["status"]): PosterSummary["status"] {
  if (status === "Draft") return "Published";
  if (status === "Published") return "Archived";
  return "Draft";
}

function statusActionHint(status: PosterSummary["status"]): string {
  if (status === "Draft") return "Publish this poster so it can be sent in campaigns";
  if (status === "Published") return "Archive this poster — it can no longer be sent";
  return "Move this poster back to a draft";
}

function statusActionLabel(status: PosterSummary["status"]): string {
  if (status === "Draft") return "Publish";
  if (status === "Published") return "Archive";
  return "Restore";
}

function PosterThumb({ summary }: { summary: PosterSummary }) {
  // A poster made before onboarding still holds {{tokens}}: the fallback
  // preview draws them with the pharmacy's details, or blank — never braces.
  // (The rendered card resolves them on the server.)
  const tokenValues = usePharmacyTokenValues();
  return (
    <PosterCardImage
      src={summary.thumbnailUrl}
      alt={summary.name}
      canvas={summary.canvas}
      tokenValues={tokenValues}
    />
  );
}

function SkeletonCard() {
  return (
    <Card className="overflow-hidden">
      <div className="aspect-[1/1.414] w-full animate-pulse bg-muted" />
      <CardHeader>
        <div className="h-5 w-3/4 animate-pulse rounded bg-muted" />
        <div className="h-4 w-full animate-pulse rounded bg-muted" />
      </CardHeader>
    </Card>
  );
}

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return (
    <div className="col-span-full flex flex-col items-center gap-3 rounded-lg border border-dashed p-10 text-center">
      <h2 className="text-lg font-semibold">No posters yet</h2>
      <p className="text-sm text-muted-foreground">
        Start from a template or a blank canvas.
      </p>
      <div className="flex gap-2">
        <Button asChild variant="outline">
          <Link href="/templates">Browse templates</Link>
        </Button>
        <Button onClick={onCreate}>New blank poster</Button>
      </div>
    </div>
  );
}

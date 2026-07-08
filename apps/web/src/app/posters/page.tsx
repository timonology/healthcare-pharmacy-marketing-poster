"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import {
  POSTER_STATUSES,
  type PosterStatus,
  type PosterSummary,
} from "@acme/shared-types";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";

type Filter = "All" | PosterStatus;

export default function MyPostersPage() {
  const router = useRouter();
  const [items, setItems] = useState<PosterSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [filter, setFilter] = useState<Filter>("All");
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const t = setTimeout(() => setDebounced(search.trim()), 250);
    return () => clearTimeout(t);
  }, [search]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    (async () => {
      try {
        const page = await api.listPosters({
          status: filter === "All" ? undefined : filter,
          search: debounced || undefined,
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
  }, [filter, debounced]);

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
          <Button onClick={createBlank}>New blank poster</Button>
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
      </div>

      {error && (
        <p className="mt-4 text-sm text-destructive" role="alert">
          {error}
        </p>
      )}

      <section className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {loading
          ? Array.from({ length: 6 }).map((_, i) => <SkeletonCard key={i} />)
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
              <CardContent>
                <p className="text-xs text-muted-foreground">
                  Updated {new Date(p.updatedAtUtc).toLocaleString()}
                </p>
              </CardContent>
              <CardFooter className="flex flex-wrap justify-between gap-2">
                <Button asChild variant="default" size="sm">
                  <Link href={`/posters/${p.id}`}>Open</Link>
                </Button>
                <div className="flex gap-1">
                  <Button asChild size="sm" variant="ghost" title="Download as PDF">
                    <a href={api.posterPdfUrl(p.id)}>PDF</a>
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => duplicate(p.id)}
                    disabled={busyId === p.id}
                  >
                    Duplicate
                  </Button>
                  {p.status === "Draft" ? (
                    <Button
                      size="sm"
                      variant="secondary"
                      onClick={() => setStatus(p.id, "Published")}
                      disabled={busyId === p.id}
                    >
                      Publish
                    </Button>
                  ) : p.status === "Published" ? (
                    <Button
                      size="sm"
                      variant="secondary"
                      onClick={() => setStatus(p.id, "Archived")}
                      disabled={busyId === p.id}
                    >
                      Archive
                    </Button>
                  ) : (
                    <Button
                      size="sm"
                      variant="secondary"
                      onClick={() => setStatus(p.id, "Draft")}
                      disabled={busyId === p.id}
                    >
                      Restore
                    </Button>
                  )}
                  <Button
                    size="sm"
                    variant="destructive"
                    onClick={() => remove(p.id)}
                    disabled={busyId === p.id}
                  >
                    Delete
                  </Button>
                </div>
              </CardFooter>
            </Card>
          ))}
      </section>
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

function PosterThumb({ summary }: { summary: PosterSummary }) {
  if (summary.thumbnailUrl) {
    return (
      <div className="aspect-[1/1.414] w-full overflow-hidden bg-muted">
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img
          src={summary.thumbnailUrl}
          alt={summary.name}
          className="h-full w-full object-cover"
        />
      </div>
    );
  }
  return (
    <div className="aspect-[1/1.414] w-full overflow-hidden bg-muted">
      <CanvasPreview doc={summary.canvas} className="h-full w-full" />
    </div>
  );
}

function SkeletonCard() {
  return (
    <Card className="overflow-hidden">
      <div className="aspect-[3/4] w-full animate-pulse bg-muted" />
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

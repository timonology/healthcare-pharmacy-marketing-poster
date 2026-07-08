"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import {
  TEMPLATE_CATEGORIES,
  type TemplateCategory,
  type TemplateSummary,
} from "@acme/shared-types";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";

type Filter = "All" | TemplateCategory;
type SizeFilter = "All" | "A4" | "A3" | "Square" | "Story";

const SIZE_FILTERS: SizeFilter[] = ["All", "A4", "A3", "Square", "Story"];

function classifySize(width: number, height: number): Exclude<SizeFilter, "All"> {
  const ratio = width / height;
  if (Math.abs(ratio - 1) < 0.08) return "Square";
  // A-series ratio is ~0.707 (1/√2). Both A4 and A3 use this aspect.
  if (Math.abs(ratio - 0.707) < 0.05) {
    // Anything >= 1500px on the long edge is "A3-class" print.
    return Math.max(width, height) >= 2000 ? "A3" : "A4";
  }
  if (ratio < 0.65) return "Story";
  return "A4";
}

export default function TemplatesPage() {
  const router = useRouter();
  const user = useAuthStore((s) => s.user);
  const [filter, setFilter] = useState<Filter>("All");
  const [size, setSize] = useState<SizeFilter>("All");
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [items, setItems] = useState<TemplateSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [usingTemplateId, setUsingTemplateId] = useState<string | null>(null);

  // Debounce search by 250ms.
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
        const page = await api.listTemplates({
          category: filter === "All" ? undefined : filter,
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

  async function useTemplate(t: TemplateSummary) {
    // Guests can browse but must sign in to start a poster from a template.
    if (!user) {
      const redirect = encodeURIComponent(`/templates?use=${t.id}`);
      router.push(`/login?redirect=${redirect}`);
      return;
    }
    try {
      setUsingTemplateId(t.id);
      const poster = await api.createPoster({
        name: t.name,
        fromTemplateId: t.id,
      });
      router.push(`/posters/${poster.id}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create poster");
      setUsingTemplateId(null);
    }
  }

  // After a guest signs in, ?use=<id> brings them back here and we auto-start
  // the poster they originally picked.
  useEffect(() => {
    if (!user) return;
    const params = new URLSearchParams(window.location.search);
    const id = params.get("use");
    if (!id) return;
    const target = items.find(t => t.id === id);
    if (target) {
      void useTemplate(target);
      const url = new URL(window.location.href);
      url.searchParams.delete("use");
      window.history.replaceState({}, "", url.toString());
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user, items]);

  const visibleItems = useMemo(() => {
    if (size === "All") return items;
    return items.filter((t) => classifySize(t.canvas.width, t.canvas.height) === size);
  }, [items, size]);

  const headerCount = useMemo(
    () => (loading ? "" : `${visibleItems.length} of ${total} template${total === 1 ? "" : "s"}`),
    [loading, total, visibleItems.length],
  );

  return (
    <main className="container max-w-6xl py-8">
      <header className="flex flex-col gap-2 md:flex-row md:items-end md:justify-between">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Template library</h1>
          <p className="text-sm text-muted-foreground">
            Pick a starting point. We&apos;ll clone it into a new poster you can edit.
          </p>
        </div>
        <span className="text-sm text-muted-foreground">{headerCount}</span>
      </header>

      <div className="mt-6 flex flex-col gap-4 md:flex-row md:items-center">
        <Input
          placeholder="Search templates…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="md:max-w-xs"
        />
        <div className="flex flex-wrap gap-2">
          <FilterChip active={filter === "All"} onClick={() => setFilter("All")}>
            All
          </FilterChip>
          {TEMPLATE_CATEGORIES.map((c) => (
            <FilterChip
              key={c}
              active={filter === c}
              onClick={() => setFilter(c)}
            >
              {c}
            </FilterChip>
          ))}
        </div>
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Size
        </span>
        {SIZE_FILTERS.map((s) => (
          <FilterChip key={s} active={size === s} onClick={() => setSize(s)}>
            {s}
          </FilterChip>
        ))}
      </div>

      {error && (
        <p className="mt-4 text-sm text-destructive" role="alert">
          {error}
        </p>
      )}

      <section className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {loading
          ? Array.from({ length: 6 }).map((_, i) => <SkeletonCard key={i} />)
          : visibleItems.length === 0
          ? (
            <p className="col-span-full text-sm text-muted-foreground">
              No templates match your filters.
            </p>
          )
          : visibleItems.map((t) => (
            <Card key={t.id} className="flex flex-col overflow-hidden">
              <TemplateThumb summary={t} />
              <CardHeader className="flex-1">
                <div className="flex items-start justify-between gap-2">
                  <CardTitle className="line-clamp-2">{t.name}</CardTitle>
                  <div className="flex gap-1.5">
                    <Badge variant="outline" className="font-normal">
                      {classifySize(t.canvas.width, t.canvas.height)}
                    </Badge>
                    <Badge variant="secondary">{t.category}</Badge>
                  </div>
                </div>
                {t.description && (
                  <CardDescription className="line-clamp-2">
                    {t.description}
                  </CardDescription>
                )}
              </CardHeader>
              <CardContent className="flex flex-wrap gap-1.5">
                {t.tags.slice(0, 4).map((tag) => (
                  <Badge key={tag} variant="outline" className="font-normal">
                    {tag}
                  </Badge>
                ))}
              </CardContent>
              <CardFooter className="flex justify-between gap-2">
                <Button asChild variant="ghost" size="sm">
                  <Link href={`/templates/${t.id}`}>Preview</Link>
                </Button>
                <Button
                  size="sm"
                  onClick={() => useTemplate(t)}
                  disabled={usingTemplateId === t.id}
                >
                  {usingTemplateId === t.id ? "Creating…" : "Use template"}
                </Button>
              </CardFooter>
            </Card>
          ))}
      </section>
    </main>
  );
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

function TemplateThumb({ summary }: { summary: TemplateSummary }) {
  // Prefer an uploaded thumbnail when present; otherwise render the canvas
  // itself as an inline SVG.
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

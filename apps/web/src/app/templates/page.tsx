"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import {
  classifyPageSize,
  inferPrintFormat,
  PRINT_FORMATS,
  TEMPLATE_CATEGORIES,
  type CanvasDocument,
  type PrintFormat,
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
import { NoResultsInRange, SortDateControls } from "@/components/lists/SortDateControls";
import { useListWindow } from "@/hooks/useListWindow";
import { hasDateRange, listWindowKey, listWindowParams } from "@/lib/list-window";
import { useAuthStore } from "@/store/auth-store";

type Filter = "All" | TemplateCategory;
type FormatFilter = "All" | PrintFormat;

const FORMAT_FILTERS: FormatFilter[] = ["All", ...PRINT_FORMATS];


/**
 * "Leaflet · DL" — what it is, and what it prints on.
 *
 * Falls back to raw pixels for a canvas that matches no known sheet, which is
 * honest: a hand-resized 1920×1080 design is not A4 and saying so would send
 * somebody to the printer with the wrong expectation.
 */
function templateSheetLabel(canvas: CanvasDocument): string {
  const format = inferPrintFormat(canvas);
  const sheet = classifyPageSize(canvas.width, canvas.height);
  return sheet
    ? `${format} · ${sheet.name}`
    : `${format} · ${canvas.width}×${canvas.height}`;
}

export default function TemplatesPage() {
  const router = useRouter();
  const user = useAuthStore((s) => s.user);
  const [filter, setFilter] = useState<Filter>("All");
  const [format, setFormat] = useState<FormatFilter>("All");
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  const [items, setItems] = useState<TemplateSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [usingTemplateId, setUsingTemplateId] = useState<string | null>(null);
  const { listWindow, setListWindow, ready } = useListWindow();
  const windowKey = listWindowKey(listWindow);

  /** Every filter back to its default. Sort is a preference and stays. */
  function clearFilters() {
    setSearch("");
    setDebounced("");
    setFilter("All");
    setFormat("All");
    setListWindow({ ...listWindow, from: null, to: null });
  }

  // Debounce search by 250ms.
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
        const page = await api.listTemplates({
          category: filter === "All" ? undefined : filter,
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
  }, [filter, debounced, ready, windowKey]);

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
    if (format === "All") return items;
    return items.filter((t) => inferPrintFormat(t.canvas) === format);
  }, [items, format]);

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
            Print-ready pharmacy designs. Pick one and it becomes your poster, with
            your brand kit applied — edit anything you like.
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
        <SortDateControls value={listWindow} onChange={setListWindow} dateField="Created" />
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-2">
        <span className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
          Format
        </span>
        {FORMAT_FILTERS.map((f) => (
          <FilterChip key={f} active={format === f} onClick={() => setFormat(f)}>
            {f}
          </FilterChip>
        ))}
      </div>

      {error && (
        <p className="mt-4 text-sm text-destructive" role="alert">
          {error}
        </p>
      )}

      <section className="mt-6 grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
        {loading
          ? Array.from({ length: 10 }).map((_, i) => <SkeletonCard key={i} />)
          : visibleItems.length === 0 && hasDateRange(listWindow)
          ? <NoResultsInRange onClear={clearFilters} />
          : visibleItems.length === 0
          ? (
            <p className="col-span-full text-sm text-muted-foreground">
              No templates match your filters.
            </p>
          )
          : visibleItems.map((t) => (
            <Card key={t.id} className="flex flex-col overflow-hidden">
              <TemplateThumb summary={t} />
              <CardHeader className="p-3 pb-1.5">
                <CardTitle className="line-clamp-1 text-xs">{t.name}</CardTitle>
                <CardDescription className="flex items-center gap-1 text-[10px]">
                  <Badge variant="secondary" className="h-4 px-1.5 text-[9px] font-normal">
                    {t.category}
                  </Badge>
                  <span>·</span>
                  <span>{templateSheetLabel(t.canvas)}</span>
                </CardDescription>
              </CardHeader>
              <CardFooter className="flex items-center justify-between gap-1 p-3 pt-0">
                <Button asChild variant="ghost" size="sm" className="h-7 px-2 text-[10px]">
                  <Link href={`/templates/${t.id}`}>Preview</Link>
                </Button>
                <Button
                  size="sm"
                  className="h-7 px-2 text-[10px]"
                  onClick={() => useTemplate(t)}
                  disabled={usingTemplateId === t.id}
                >
                  {usingTemplateId === t.id ? "Creating…" : "Use"}
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

/**
 * The template's real preview: its uploaded thumbnail when it has one,
 * otherwise the canvas itself drawn as SVG — never a coloured placeholder.
 * Both at the sheet's own proportions, so an A4 card is A4.
 */
function TemplateThumb({ summary }: { summary: TemplateSummary }) {
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
      <CanvasPreview
        doc={summary.canvas}
        className="h-full w-full"
        // Always without the pharmacy's logo and booking QR: a template card
        // describes the template ("Your pharmacy", "Your phone"), not one
        // pharmacy's copy of it. Fetching them here also 404'd for every
        // account without a logo or booking link, once per card.
        withoutBrandAssets
      />
    </div>
  );
}

function SkeletonCard() {
  return (
    <Card className="overflow-hidden">
      <div className="aspect-[1/1.414] w-full animate-pulse bg-muted" />
      <CardHeader className="p-3 pb-1.5">
        <div className="h-3.5 w-3/4 animate-pulse rounded bg-muted" />
        <div className="mt-1 h-2.5 w-1/2 animate-pulse rounded bg-muted" />
      </CardHeader>
    </Card>
  );
}

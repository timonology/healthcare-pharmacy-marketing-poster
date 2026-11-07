"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import {
  TEMPLATE_CATEGORIES,
  type TemplateCategory,
  type TemplateSummary,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { Images, Search } from "lucide-react";
import {
  EmptyState,
  ErrorNote,
  Panel,
  Skeleton,
} from "@/components/admin/AdminUi";
import { api } from "@/lib/api";
import { useEditorStore } from "@/store/editor-store";
import { useRouter } from "next/navigation";

export default function AdminTemplatesPage() {
  const router = useRouter();
  const loadDetached = useEditorStore((s) => s.loadDetached);

  const [items, setItems] = useState<TemplateSummary[]>([]);
  const [search, setSearch] = useState("");
  const [category, setCategory] = useState<TemplateCategory | "">("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const hasFilters = search.trim().length > 0 || category !== "";

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const page = await api.listTemplates({
        search: search.trim() || undefined,
        category: category || undefined,
        take: 100,
      });
      setItems(page.items);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load templates.");
    } finally {
      setLoading(false);
    }
  }, [search, category]);

  useEffect(() => {
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load]);

  /**
   * Duplicating opens a copy in the composer rather than writing a new template
   * straight away — the point of a duplicate is to change it, and publishing an
   * identical second entry into a shared gallery helps nobody.
   *
   * <b>It used to open /canvas</b>, the scratch playground: a bare stage with
   * Add rect / Add circle / Add text, no properties panel, and — the part that
   * mattered — <i>no way to save anything</i>. A duplicate opened there could be
   * edited and then only lost, because publishing a template needs a name, a
   * category and tags, and that page has none of them. The composer has all of
   * it, plus the same editor behind "Edit in designer".
   */
  async function duplicate(t: TemplateSummary) {
    setBusyId(t.id);
    try {
      const full = await api.getTemplate(t.id);
      loadDetached({
        name: `${full.name} copy`,
        canvas: { ...full.canvas, name: `${full.name} copy` },
      });
      router.push("/admin/templates/new?from=store");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not open that template.");
      setBusyId(null);
    }
  }

  async function remove(t: TemplateSummary) {
    if (!confirm(`Delete "${t.name}"? Every pharmacy loses access to it.`)) return;
    setBusyId(t.id);
    try {
      await api.deleteTemplate(t.id);
      setItems((prev) => prev.filter((x) => x.id !== t.id));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not delete that template.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title="Template gallery"
        description="Everything here is visible to every pharmacy on the platform."
      >
        <div className="flex flex-wrap items-center gap-2 border-b border-border/60 p-3">
          <Input
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            placeholder="Search templates"
            className="h-9 max-w-xs"
          />
          <select
            value={category}
            onChange={(e) => setCategory(e.target.value as TemplateCategory | "")}
            className="h-9 rounded-md border border-border/60 bg-background px-2 text-sm"
            aria-label="Filter by category"
          >
            <option value="">All categories</option>
            {TEMPLATE_CATEGORIES.map((c) => (
              <option key={c} value={c}>{c}</option>
            ))}
          </select>

          <div className="ml-auto">
            <Button asChild size="sm">
              <Link href="/admin/templates/new">Design with AI</Link>
            </Button>
          </div>
        </div>

        {loading ? (
          // Cards sized like the real ones, so the grid doesn't jump on load.
          <div className="grid gap-4 p-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {[0, 1, 2, 3].map((i) => (
              <div key={i} className="overflow-hidden rounded-lg border border-border/60">
                <Skeleton className="aspect-[1240/1754] rounded-none" />
                <div className="space-y-2 p-3">
                  <Skeleton className="h-3.5 w-2/3" />
                  <Skeleton className="h-2.5 w-1/3" />
                </div>
              </div>
            ))}
          </div>
        ) : items.length === 0 ? (
          // "Nothing exists" and "nothing matches" need different responses:
          // one wants a way to create, the other wants the filters cleared.
          hasFilters ? (
            <EmptyState
              icon={Search}
              title="No templates match those filters"
              message="Try a different search term or category."
              action={
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => {
                    setSearch("");
                    setCategory("");
                  }}
                >
                  Clear filters
                </Button>
              }
            />
          ) : (
            <EmptyState
              icon={Images}
              title="No templates yet"
              message="Design your first poster template and publish it to the gallery."
              action={
                <Button size="sm" asChild>
                  <Link href="/admin/templates/new">Design with AI</Link>
                </Button>
              }
            />
          )
        ) : (
          <div className="grid gap-4 p-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {items.map((t) => (
              <div
                key={t.id}
                className="overflow-hidden rounded-lg border border-border/60"
              >
                <div className="aspect-[1240/1754] overflow-hidden bg-muted">
                  <CanvasPreview doc={t.canvas} className="h-full w-full" withoutBrandAssets />
                </div>
                <div className="space-y-2 p-3">
                  <div>
                    <p className="truncate text-sm font-medium">{t.name}</p>
                    <p className="text-[11px] text-muted-foreground">
                      {t.category}
                      {!t.isPublished && " · draft"}
                    </p>
                  </div>
                  <div className="flex gap-1.5">
                    <Button
                      size="sm"
                      variant="outline"
                      className="flex-1 text-xs"
                      disabled={busyId === t.id}
                      onClick={() => duplicate(t)}
                    >
                      Duplicate
                    </Button>
                    <Button
                      size="sm"
                      variant="ghost"
                      className="text-xs text-destructive hover:text-destructive"
                      disabled={busyId === t.id}
                      onClick={() => remove(t)}
                    >
                      Delete
                    </Button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </Panel>
    </div>
  );
}

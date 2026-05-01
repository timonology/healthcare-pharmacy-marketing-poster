"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ArrowLeft } from "lucide-react";
import type { Template } from "@acme/shared-types";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { api } from "@/lib/api";

/**
 * Single-template preview. Shows a large render of the canvas alongside the
 * template's metadata and a "Use template" CTA that clones it into a poster.
 */
export default function TemplatePreviewPage() {
  const params = useParams<{ id: string }>();
  const router = useRouter();
  const id = params.id;

  const [template, setTemplate] = useState<Template | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const t = await api.getTemplate(id);
        if (!cancelled) setTemplate(t);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [id]);

  async function useTemplate() {
    if (!template) return;
    try {
      setCreating(true);
      const poster = await api.createPoster({
        name: template.name,
        fromTemplateId: template.id,
      });
      router.push(`/posters/${poster.id}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to create poster");
      setCreating(false);
    }
  }

  if (error) {
    return (
      <main className="container flex min-h-[60vh] flex-col items-center justify-center gap-4 text-center">
        <h1 className="text-xl font-semibold">Couldn&apos;t load this template</h1>
        <p className="text-sm text-muted-foreground">{error}</p>
        <Button asChild variant="outline">
          <Link href="/templates">Back to templates</Link>
        </Button>
      </main>
    );
  }

  if (!template) {
    return (
      <main className="container py-8 text-sm text-muted-foreground">
        Loading template…
      </main>
    );
  }

  return (
    <main className="container max-w-6xl py-8">
      <div className="mb-6 flex items-center gap-2">
        <Button asChild variant="ghost" size="sm">
          <Link href="/templates" aria-label="Back to templates">
            <ArrowLeft className="mr-1.5 h-4 w-4" />
            All templates
          </Link>
        </Button>
      </div>

      <div className="grid grid-cols-1 gap-8 md:grid-cols-[minmax(0,1fr)_320px]">
        <section className="overflow-hidden rounded-lg border bg-muted shadow-sm">
          <div className="aspect-[1/1.414] w-full">
            <CanvasPreview doc={template.canvas} className="h-full w-full" />
          </div>
        </section>

        <aside className="space-y-6">
          <header className="space-y-2">
            <Badge variant="secondary">{template.category}</Badge>
            <h1 className="text-3xl font-bold tracking-tight">{template.name}</h1>
            {template.description && (
              <p className="text-sm text-muted-foreground">{template.description}</p>
            )}
          </header>

          <Separator />

          <Button size="lg" className="w-full" onClick={useTemplate} disabled={creating}>
            {creating ? "Creating poster…" : "Use template"}
          </Button>

          {template.tags.length > 0 && (
            <div>
              <h2 className="mb-2 text-xs font-medium uppercase tracking-wider text-muted-foreground">
                Tags
              </h2>
              <div className="flex flex-wrap gap-1.5">
                {template.tags.map((tag) => (
                  <Badge key={tag} variant="outline" className="font-normal">
                    {tag}
                  </Badge>
                ))}
              </div>
            </div>
          )}

          <div className="space-y-1 text-xs text-muted-foreground">
            <p>
              Page size: {template.canvas.width} × {template.canvas.height} px
            </p>
            <p>Updated {new Date(template.updatedAtUtc).toLocaleString()}</p>
          </div>
        </aside>
      </div>
    </main>
  );
}

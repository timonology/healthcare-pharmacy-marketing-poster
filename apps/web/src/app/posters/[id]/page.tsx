"use client";

import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { EditorShell } from "@/components/editor/EditorShell";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { useEditorStore } from "@/store/editor-store";

export default function PosterEditorPage() {
  const params = useParams<{ id: string }>();
  const router = useRouter();
  const id = params.id;

  const loadPoster = useEditorStore((s) => s.loadPoster);

  const [error, setError] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);

  // Fetch + hydrate the editor store once.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const p = await api.getPoster(id);
        if (cancelled) return;
        loadPoster({ id: p.id, name: p.name, canvas: p.canvas });
        setLoaded(true);
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [id, loadPoster]);

  if (error) {
    return (
      <main className="container flex min-h-screen flex-col items-center justify-center gap-4 text-center">
        <h1 className="text-xl font-semibold">Couldn&apos;t load this poster</h1>
        <p className="text-sm text-muted-foreground">{error}</p>
        <Button onClick={() => router.push("/posters")}>Back to my posters</Button>
      </main>
    );
  }

  if (!loaded) {
    return (
      <main className="flex h-screen items-center justify-center text-sm text-muted-foreground">
        Loading poster…
      </main>
    );
  }

  return <EditorShell />;
}

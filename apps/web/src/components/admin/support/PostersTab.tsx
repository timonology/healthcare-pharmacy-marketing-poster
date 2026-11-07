"use client";

import { useEffect, useState } from "react";
import { ImageOff, LayoutTemplate } from "lucide-react";
import type { SupportPoster } from "@acme/shared-types";
import { api } from "@/lib/api";
import { EmptyState, ErrorNote, Panel, Skeleton, formatDate } from "../AdminUi";
import { PosterStatusPill } from "./SupportUi";

/**
 * Every poster this pharmacy has made.
 *
 * A grid rather than a table because "my poster looks wrong" is a visual
 * complaint, and finding the right one from a list of names like "Poster 3"
 * takes longer than glancing at twelve thumbnails.
 */
export function PostersTab({
  pharmacyId,
  pharmacyName,
}: {
  pharmacyId: string;
  pharmacyName: string;
}) {
  const [posters, setPosters] = useState<SupportPoster[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.admin.support
      .posters(pharmacyId)
      .then((p) => {
        if (!cancelled) setPosters(p);
      })
      .catch((err: unknown) => {
        if (!cancelled)
          setError(err instanceof Error ? err.message : "Could not load posters.");
      });
    return () => {
      cancelled = true;
    };
  }, [pharmacyId]);

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title={posters ? `Posters (${posters.length})` : "Posters"}
        description="What this pharmacy has designed. Read-only — nothing here can be edited from the admin console."
      >
        {!posters ? (
          <div className="grid gap-4 p-4 sm:grid-cols-3 lg:grid-cols-4">
            {Array.from({ length: 4 }).map((_, i) => (
              <Skeleton key={i} className="aspect-[1/1.414] w-full" />
            ))}
          </div>
        ) : posters.length === 0 ? (
          <EmptyState
            icon={LayoutTemplate}
            title="No posters"
            message={`${pharmacyName} has not created any posters yet.`}
          />
        ) : (
          <ul className="grid gap-4 p-4 sm:grid-cols-3 lg:grid-cols-4">
            {posters.map((p) => (
              <li key={p.id} className="space-y-2">
                <div className="aspect-[1/1.414] w-full overflow-hidden rounded-md border border-border/60 bg-muted">
                  <SupportPosterThumb src={p.thumbnailUrl} name={p.name} />
                </div>
                <div className="space-y-1">
                  <p className="truncate text-sm font-medium" title={p.name}>
                    {p.name}
                  </p>
                  <div className="flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
                    <PosterStatusPill status={p.status} />
                    <span>{formatDate(p.updatedAtUtc)}</span>
                  </div>
                  {p.usedInCampaigns > 0 && (
                    <p className="text-[11px] text-muted-foreground">
                      Used in {p.usedInCampaigns}{" "}
                      {p.usedInCampaigns === 1 ? "campaign" : "campaigns"}
                    </p>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}

/**
 * The last render saved for a pharmacy's poster, or a plain "no preview".
 *
 * The src is a short-lived SAS link. When it cannot load — expired while the
 * tab was open, or storage not reachable from this browser (Azurite in
 * Docker) — this shows the same "no preview" state rather than the browser's
 * broken-image "?". Plain <img>: the Next image optimiser would cache a copy
 * that outlives the expiry the link was deliberately given.
 */
function SupportPosterThumb({ src, name }: { src: string | null; name: string }) {
  const [failed, setFailed] = useState(false);
  if (!src || failed) {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-1 text-muted-foreground">
        <ImageOff className="h-5 w-5" aria-hidden />
        <span className="text-[11px]">No preview saved</span>
      </div>
    );
  }
  return (
    /* eslint-disable-next-line @next/next/no-img-element */
    <img src={src} alt={name} className="h-full w-full object-cover" onError={() => setFailed(true)} />
  );
}

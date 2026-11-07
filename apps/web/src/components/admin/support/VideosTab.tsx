"use client";

import { useEffect, useState } from "react";
import { Film } from "lucide-react";
import type { SupportVideo } from "@acme/shared-types";
import { api } from "@/lib/api";
import {
  EmptyState,
  ErrorNote,
  ListSkeleton,
  Panel,
  formatDate,
} from "../AdminUi";
import { VideoStatusPill } from "./SupportUi";

/**
 * The pharmacy's videos.
 *
 * The note column carries the pipeline's failure reason, which is the whole
 * reason this tab exists — "my video didn't work" is otherwise a question with
 * no visible answer on the admin side.
 */
export function VideosTab({
  pharmacyId,
  pharmacyName,
}: {
  pharmacyId: string;
  pharmacyName: string;
}) {
  const [videos, setVideos] = useState<SupportVideo[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.admin.support
      .videos(pharmacyId)
      .then((v) => {
        if (!cancelled) setVideos(v);
      })
      .catch((err: unknown) => {
        if (!cancelled)
          setError(err instanceof Error ? err.message : "Could not load videos.");
      });
    return () => {
      cancelled = true;
    };
  }, [pharmacyId]);

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title={videos ? `Videos (${videos.length})` : "Videos"}
        description="Generated videos, including any that failed and why."
      >
        {!videos ? (
          <div className="p-4">
            <ListSkeleton rows={4} />
          </div>
        ) : videos.length === 0 ? (
          <EmptyState
            icon={Film}
            title="No videos"
            message={`${pharmacyName} has not generated any videos.`}
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-4 py-2 font-medium">Name</th>
                  <th className="px-4 py-2 font-medium">Topic</th>
                  <th className="px-4 py-2 font-medium">Status</th>
                  <th className="px-4 py-2 font-medium">Length</th>
                  <th className="px-4 py-2 font-medium">Created</th>
                </tr>
              </thead>
              <tbody>
                {videos.map((v) => (
                  <tr key={v.id} className="border-b border-border/40">
                    <td className="px-4 py-2 font-medium">{v.name}</td>
                    <td className="max-w-xs truncate px-4 py-2 text-xs text-muted-foreground">
                      {v.topic}
                    </td>
                    <td className="px-4 py-2">
                      <VideoStatusPill status={v.status} />
                      {v.status === "Failed" && v.note && (
                        <p className="mt-1 max-w-xs text-[11px] text-destructive">
                          {v.note}
                        </p>
                      )}
                    </td>
                    <td className="px-4 py-2 text-xs text-muted-foreground">
                      {v.durationSeconds}s
                    </td>
                    <td className="px-4 py-2 text-xs text-muted-foreground">
                      {formatDate(v.createdAtUtc)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </div>
  );
}

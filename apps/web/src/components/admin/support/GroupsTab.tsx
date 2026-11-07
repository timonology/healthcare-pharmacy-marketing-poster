"use client";

import { useEffect, useState } from "react";
import { FolderOpen } from "lucide-react";
import type { SupportGroup } from "@acme/shared-types";
import { api } from "@/lib/api";
import {
  EmptyState,
  ErrorNote,
  ListSkeleton,
  Panel,
  formatDate,
} from "../AdminUi";

/**
 * The pharmacy's patient groups.
 *
 * The counts here are recomputed server-side from group membership rather than
 * read off the stored counter, so a group whose counter has drifted shows the
 * number a campaign would actually send to. That is the number the
 * administrator is about to quote to the customer, and being confidently wrong
 * about it is worse than not showing it.
 */
export function GroupsTab({
  pharmacyId,
  pharmacyName,
}: {
  pharmacyId: string;
  pharmacyName: string;
}) {
  const [groups, setGroups] = useState<SupportGroup[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.admin.support
      .groups(pharmacyId)
      .then((g) => {
        if (!cancelled) setGroups(g);
      })
      .catch((err: unknown) => {
        if (!cancelled)
          setError(err instanceof Error ? err.message : "Could not load groups.");
      });
    return () => {
      cancelled = true;
    };
  }, [pharmacyId]);

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title={groups ? `Groups (${groups.length})` : "Groups"}
        description="Patient segments this pharmacy sends campaigns to."
      >
        {!groups ? (
          <div className="p-4">
            <ListSkeleton rows={4} />
          </div>
        ) : groups.length === 0 ? (
          <EmptyState
            icon={FolderOpen}
            title="No groups"
            message={`${pharmacyName} sends to individually chosen recipients rather than to saved groups.`}
          />
        ) : (
          <ul className="divide-y divide-border/40">
            {groups.map((g) => (
              <li
                key={g.id}
                className="flex flex-wrap items-baseline justify-between gap-2 px-4 py-3"
              >
                <div className="min-w-0">
                  <p className="text-sm font-medium">{g.name}</p>
                  {g.description && (
                    <p className="mt-0.5 text-xs text-muted-foreground">
                      {g.description}
                    </p>
                  )}
                </div>
                <div className="flex items-center gap-4 text-xs text-muted-foreground">
                  <span className="font-medium text-foreground">
                    {g.patientCount.toLocaleString()}{" "}
                    {g.patientCount === 1 ? "patient" : "patients"}
                  </span>
                  <span>created {formatDate(g.createdAtUtc)}</span>
                </div>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}

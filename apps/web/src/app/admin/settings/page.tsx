"use client";

import { useCallback, useEffect, useState } from "react";
import { AlertTriangle, Check, RotateCcw, ShieldAlert } from "lucide-react";
import type { VideoModelSetting } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { api } from "@/lib/api";
import { useAuthStore } from "@/store/auth-store";
import { ErrorNote, ListSkeleton, Panel, formatDate } from "@/components/admin/AdminUi";
import { cn } from "@/lib/utils";

/**
 * Platform settings. SuperAdmin only.
 *
 * The role check below hides the page; it does not secure it. The API returns
 * 403 to an ordinary Admin regardless of what the browser renders, and the
 * service re-checks the role a third time. This is here so an Admin who follows
 * a link sees an explanation rather than a wall of failed requests.
 */
export default function AdminSettingsPage() {
  const role = useAuthStore((s) => s.user?.role);
  const isSuperAdmin = role === "SuperAdmin";

  const [setting, setSetting] = useState<VideoModelSetting | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const load = useCallback(async () => {
    try {
      setSetting(await api.admin.settings.videoModel());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load settings.");
    }
  }, []);

  useEffect(() => {
    if (isSuperAdmin) void load();
  }, [isSuperAdmin, load]);

  async function apply(model: string) {
    setBusy(model);
    setError(null);
    try {
      setSetting(await api.admin.settings.setVideoModel(model));
      setSaved(true);
      setTimeout(() => setSaved(false), 2500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not change the model.");
    } finally {
      setBusy(null);
    }
  }

  async function reset() {
    setBusy("reset");
    setError(null);
    try {
      setSetting(await api.admin.settings.resetVideoModel());
      setSaved(true);
      setTimeout(() => setSaved(false), 2500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not reset the model.");
    } finally {
      setBusy(null);
    }
  }

  if (!isSuperAdmin) {
    return (
      <div className="flex flex-col items-center gap-2 rounded-lg border border-border/60 bg-card px-6 py-16 text-center">
        <ShieldAlert className="h-8 w-8 text-muted-foreground" aria-hidden />
        <p className="mt-2 text-sm font-medium">Super admins only</p>
        <p className="max-w-sm text-xs text-muted-foreground">
          These settings change how the platform behaves for every pharmacy, so
          they are limited to super admins.
        </p>
      </div>
    );
  }

  const usingOverride = setting?.source === "database";

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Panel
        title="Video generation model"
        description="Which Anthropic model writes the script and research for every marketing video."
        action={
          saved ? (
            <span className="flex items-center gap-1 text-xs text-primary">
              <Check className="h-3.5 w-3.5" />
              Saved
            </span>
          ) : undefined
        }
      >
        {!setting ? (
          <div className="p-4">
            <ListSkeleton rows={3} />
          </div>
        ) : (
          <div className="space-y-4 p-4">
            <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-xs text-muted-foreground">
              <span>
                In use:{" "}
                <span className="font-mono text-sm text-foreground">
                  {setting.currentModel}
                </span>
              </span>
              {/*
                Where the value came from is shown, not just the value. "Why is
                it using that model?" is otherwise a question only a developer
                can answer, and the answer is usually "an environment variable
                somebody set months ago".
              */}
              <span>
                Source:{" "}
                {usingOverride
                  ? `set here by ${setting.updatedByEmail ?? "an admin"}${
                      setting.updatedAtUtc ? ` on ${formatDate(setting.updatedAtUtc)}` : ""
                    }`
                  : setting.source}
              </span>
            </div>

            <ul className="space-y-2">
              {setting.options.map((option) => (
                <li key={option.id}>
                  <button
                    type="button"
                    disabled={busy !== null}
                    onClick={() => apply(option.id)}
                    className={cn(
                      "flex w-full items-start gap-3 rounded-md border p-3 text-left transition-colors disabled:opacity-60",
                      option.isCurrent
                        ? "border-primary bg-primary/5"
                        : "border-border/60 hover:bg-accent/50",
                    )}
                  >
                    <span
                      className={cn(
                        "mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded-full border",
                        option.isCurrent ? "border-primary bg-primary" : "border-border",
                      )}
                    >
                      {option.isCurrent && (
                        <Check className="h-2.5 w-2.5 text-primary-foreground" />
                      )}
                    </span>

                    <span className="min-w-0 flex-1">
                      <span className="flex flex-wrap items-baseline gap-2">
                        <span className="text-sm font-medium">{option.displayName}</span>
                        <span className="font-mono text-[11px] text-muted-foreground">
                          {option.id}
                        </span>
                        <span
                          className={cn(
                            "rounded-full px-2 py-0.5 text-[11px]",
                            option.relativeCost <= 1
                              ? "bg-primary/10 text-primary"
                              : "bg-amber-500/15 text-amber-700 dark:text-amber-400",
                          )}
                        >
                          {option.relativeCost <= 1
                            ? "cheapest"
                            : `~${option.relativeCost}x cost`}
                        </span>
                      </span>
                      <span className="mt-1 block text-xs text-muted-foreground">
                        {option.description}
                      </span>
                    </span>
                  </button>
                </li>
              ))}
            </ul>

            <div className="flex items-start gap-2 rounded-md bg-muted px-3 py-2 text-xs text-muted-foreground">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              <span>
                Takes effect on the next video the worker picks up. Videos
                already rendering finish on the model they started with. A more
                expensive model multiplies the per-video cost on every plan —
                check the cost model before switching.
              </span>
            </div>

            {usingOverride && (
              <div className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-border/60 p-3">
                <p className="text-xs text-muted-foreground">
                  Reset to use the deployed value again (
                  <span className="font-mono">{setting.configuredFallback}</span>
                  ).
                </p>
                <Button
                  size="sm"
                  variant="outline"
                  className="gap-1.5 text-xs"
                  disabled={busy !== null}
                  onClick={reset}
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                  Reset
                </Button>
              </div>
            )}
          </div>
        )}
      </Panel>
    </div>
  );
}

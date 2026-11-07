"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { Download } from "lucide-react";
import {
  AUDIT_ACTION_PREFIXES,
  type AuditEvent,
  type AuditEventFilters,
  type AuditSecurityView,
  type AuditStreamView,
  type AuditTab,
} from "@acme/shared-types";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { api } from "@/lib/api";
import { NoResultsInRange, SortDateControls } from "@/components/lists/SortDateControls";
import { useListWindow } from "@/hooks/useListWindow";
import { hasDateRange, listWindowKey, listWindowParams, type ListWindow } from "@/lib/list-window";
import {
  ErrorNote,
  Pagination,
  Panel,
  TableEmpty,
  TableLoading,
} from "@/components/admin/AdminUi";
import { cn } from "@/lib/utils";

const TAKE = 50;

/**
 * Three lists, never one. "Opened a poster" and "deleted a user" are different
 * questions asked by different people, so they do not share a default view.
 */
const TABS: { value: AuditTab; label: string; description: string }[] = [
  {
    value: "Security",
    label: "Security audit",
    description:
      "Staff actions, sign-ins and anything refused. Kept indefinitely.",
  },
  {
    value: "Staff",
    label: "Staff activity",
    description: "What administrators changed and opened, including while managing a pharmacy.",
  },
  {
    value: "Pharmacy",
    label: "Pharmacy activity",
    description: "What pharmacy users changed and opened. Views are kept for 90 days.",
  },
];

const SECURITY_VIEWS: { value: AuditSecurityView; label: string }[] = [
  { value: "Default", label: "Security events" },
  { value: "People", label: "All changes by people" },
  { value: "System", label: "System (seeders, smoke)" },
];

const STREAMS: { value: AuditStreamView; label: string }[] = [
  { value: "Changes", label: "Changes" },
  { value: "Views", label: "Views" },
];

/** The text filters. Dates and sort are the shared toolbar's, in the URL. */
interface Filters {
  actor: string;
  pharmacyId: string;
  action: string;
  entityType: string;
  entityId: string;
}

const EMPTY: Filters = {
  actor: "",
  pharmacyId: "",
  action: "",
  entityType: "",
  entityId: "",
};

export default function AdminAuditPage() {
  const [tab, setTab] = useState<AuditTab>("Security");
  const [view, setView] = useState<AuditSecurityView>("Default");
  const [stream, setStream] = useState<AuditStreamView>("Changes");
  const [filters, setFilters] = useState<Filters>(EMPTY);
  const [skip, setSkip] = useState(0);
  const { listWindow, setListWindow, ready } = useListWindow();
  const windowKey = listWindowKey(listWindow);

  function changeWindow(next: ListWindow) {
    setSkip(0);
    setListWindow(next);
  }

  /** Every filter back to its default. Sort is a preference and stays. */
  function clearFilters() {
    setSkip(0);
    setFilters(EMPTY);
    setListWindow({ ...listWindow, from: null, to: null });
  }

  const [rows, setRows] = useState<AuditEvent[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const query = useMemo<AuditEventFilters>(() => {
    const q: AuditEventFilters = { tab, skip, take: TAKE };
    if (tab === "Security") q.view = view;
    else q.stream = stream;
    // Event time. Inclusive days in the toolbar, from inclusive / to exclusive
    // on the wire — lib/list-window does the day arithmetic.
    const w = listWindowParams(listWindow);
    q.sort = w.sort;
    if (w.from) q.from = w.from;
    if (w.to) q.to = w.to;
    for (const key of ["actor", "pharmacyId", "action", "entityType", "entityId"] as const) {
      const v = filters[key].trim();
      if (v) q[key] = v;
    }
    return q;
    // listWindow is covered by windowKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, view, stream, filters, skip, windowKey]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const page = await api.admin.auditEvents(query);
      setRows(page.items);
      setTotal(page.total);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load the audit log.");
    } finally {
      setLoading(false);
    }
  }, [query]);

  useEffect(() => {
    // Not before the URL is read, so a shared link fetches once.
    if (!ready) return;
    // Debounced, so typing an email does not fire a request per keystroke.
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load, ready]);

  const set = (key: keyof Filters, value: string) => {
    setSkip(0);
    setFilters((f) => ({ ...f, [key]: value }));
  };

  const current = TABS.find((t) => t.value === tab)!;
  const hasFilters =
    Object.values(filters).some((v) => v.trim() !== "") || hasDateRange(listWindow);

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <Tabs
        value={tab}
        onValueChange={(v) => {
          setSkip(0);
          setTab(v as AuditTab);
        }}
      >
        <TabsList>
          {TABS.map((t) => (
            <TabsTrigger key={t.value} value={t.value}>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>

      <Panel
        title={current.label}
        description={current.description}
        action={
          <Button asChild variant="outline" size="sm">
            {/* A navigation, not a fetch: the browser saves the file. */}
            <a href={api.admin.auditEventsExportUrl(query)} download>
              <Download className="mr-1.5 h-4 w-4" />
              Export CSV
            </a>
          </Button>
        }
      >
        <div className="flex flex-wrap items-center gap-2 border-b border-border/60 p-3">
          <Segmented
            label={tab === "Security" ? "Which events" : "Changes or views"}
            value={tab === "Security" ? view : stream}
            options={tab === "Security" ? SECURITY_VIEWS : STREAMS}
            onChange={(v) => {
              setSkip(0);
              if (tab === "Security") setView(v as AuditSecurityView);
              else setStream(v as AuditStreamView);
            }}
          />
          <SortDateControls value={listWindow} onChange={changeWindow} dateField="Event time" />
        </div>

        <div className="grid gap-2 border-b border-border/60 p-3 sm:grid-cols-2 lg:grid-cols-4">
          <label className="space-y-1 text-xs text-muted-foreground">
            Actor
            <Input
              value={filters.actor}
              onChange={(e) => set("actor", e.target.value)}
              placeholder="Email or user id"
              className="h-9"
            />
          </label>
          <label className="space-y-1 text-xs text-muted-foreground">
            Pharmacy
            <Input
              value={filters.pharmacyId}
              onChange={(e) => set("pharmacyId", e.target.value)}
              placeholder="Pharmacy id"
              className="h-9"
            />
          </label>
          <label className="space-y-1 text-xs text-muted-foreground">
            Action
            <Input
              value={filters.action}
              onChange={(e) => set("action", e.target.value)}
              placeholder="e.g. poster. or admin.impersonate"
              list="audit-action-prefixes"
              className="h-9"
            />
            <datalist id="audit-action-prefixes">
              {AUDIT_ACTION_PREFIXES.map((p) => (
                <option key={p} value={p} />
              ))}
            </datalist>
          </label>
          <label className="space-y-1 text-xs text-muted-foreground">
            Entity type
            <Input
              value={filters.entityType}
              onChange={(e) => set("entityType", e.target.value)}
              placeholder="e.g. poster, patient"
              className="h-9"
            />
          </label>
          <label className="space-y-1 text-xs text-muted-foreground">
            Entity id
            <Input
              value={filters.entityId}
              onChange={(e) => set("entityId", e.target.value)}
              placeholder="Exact id"
              className="h-9"
            />
          </label>
          <div className="flex items-end">
            <Button
              variant="ghost"
              size="sm"
              disabled={!hasFilters}
              onClick={clearFilters}
            >
              Clear filters
            </Button>
          </div>
        </div>

        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="border-b border-border/60 text-left text-xs text-muted-foreground">
              <tr>
                <th className="px-4 py-2 font-medium">When</th>
                <th className="px-4 py-2 font-medium">Who</th>
                <th className="px-4 py-2 font-medium">Pharmacy</th>
                <th className="px-4 py-2 font-medium">Action</th>
                <th className="px-4 py-2 font-medium">Entity</th>
                <th className="px-4 py-2 font-medium">Result</th>
                <th className="px-4 py-2 font-medium">Detail</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border/60">
              {loading ? (
                <TableLoading colSpan={7} />
              ) : rows.length === 0 && hasDateRange(listWindow) ? (
                <tr>
                  <td colSpan={7} className="p-4">
                    <NoResultsInRange onClear={clearFilters} className="border-0" />
                  </td>
                </tr>
              ) : rows.length === 0 ? (
                <TableEmpty colSpan={7} message="No events match these filters." />
              ) : (
                rows.map((e) => (
                  <tr key={e.id} className="align-top hover:bg-accent/40">
                    <td className="whitespace-nowrap px-4 py-2.5 text-xs text-muted-foreground">
                      {formatTimestamp(e.atUtc)}
                    </td>
                    <td className="px-4 py-2.5">
                      <div className="flex items-center gap-1.5">
                        <KindBadge kind={e.actorKind} />
                        <span className="truncate">{e.actorEmail ?? e.actorId}</span>
                      </div>
                      {e.actorRole && (
                        <div className="text-[11px] text-muted-foreground">{e.actorRole}</div>
                      )}
                    </td>
                    <td className="px-4 py-2.5 font-mono text-[11px]">
                      {e.pharmacyId ? (
                        <button
                          type="button"
                          className="text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
                          title="Show only this pharmacy"
                          onClick={() => set("pharmacyId", e.pharmacyId!)}
                        >
                          {short(e.pharmacyId)}
                        </button>
                      ) : (
                        "—"
                      )}
                    </td>
                    <td className="px-4 py-2.5 font-mono text-xs">{e.action}</td>
                    <td className="px-4 py-2.5 text-xs text-muted-foreground">
                      {e.entityType ?? "—"}
                      {e.entityId && <span className="ml-1 font-mono text-[11px]">{short(e.entityId)}</span>}
                    </td>
                    <td className="px-4 py-2.5">
                      <ResultBadge result={e.result} />
                    </td>
                    <td className="px-4 py-2.5 text-[11px] text-muted-foreground">
                      {Object.entries(e.meta).map(([k, v]) => (
                        <div key={k}>
                          {k}: {v}
                        </div>
                      ))}
                      {e.ip && <div title={e.requestId ?? undefined}>ip: {e.ip}</div>}
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        <Pagination skip={skip} take={TAKE} total={total} onChange={setSkip} />
      </Panel>

      <p className="text-xs text-muted-foreground">
        Events are append-only and cannot be edited or deleted from the application.
        Changes and security events are kept indefinitely; views for 90 days. Never
        recorded: passwords, tokens, card details, or lists of patients and recipients —
        only how many.
      </p>
    </div>
  );
}

function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: { value: T; label: string }[];
  onChange: (v: T) => void;
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex rounded-md border border-border/60 p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            "rounded px-2.5 py-1 text-xs font-medium",
            value === o.value ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:bg-accent",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

function KindBadge({ kind }: { kind: AuditEvent["actorKind"] }) {
  return (
    <span
      className={cn(
        "inline-flex shrink-0 rounded-full px-1.5 py-0.5 text-[10px] font-medium uppercase",
        kind === "admin" && "bg-primary/10 text-primary",
        kind === "pharmacy" && "bg-muted text-muted-foreground",
        kind === "system" && "bg-amber-500/10 text-amber-700 dark:text-amber-400",
      )}
    >
      {kind}
    </span>
  );
}

function ResultBadge({ result }: { result: AuditEvent["result"] }) {
  return (
    <span
      className={cn(
        "inline-flex rounded-full px-2 py-0.5 text-[11px] font-medium",
        result === "ok" && "bg-muted text-muted-foreground",
        result === "denied" && "bg-destructive/10 text-destructive",
        result === "failed" && "bg-amber-500/10 text-amber-700 dark:text-amber-400",
      )}
    >
      {result}
    </span>
  );
}

/** Ids are long; the first eight characters are enough to tell rows apart. */
function short(id: string): string {
  return id.length > 10 ? `${id.slice(0, 8)}…` : id;
}

/** Full timestamp — an audit log needs the time, not just the date. */
function formatTimestamp(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return "—";
  return d.toLocaleString(undefined, {
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

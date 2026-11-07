"use client";

import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import { AlertCircle, ArrowLeft, CheckCircle2, ChevronLeft, ChevronRight, Mail, MessageSquare, PlusCircle, RotateCcw, Search, Square } from "lucide-react";
import type {
  Campaign,
  CampaignAudiencePreview,
  CampaignChannel,
  CurrentSubscription,
  Patient,
  PatientGroup,
  PosterSummary,
  Video,
} from "@acme/shared-types";
import { isUnlimited } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { Textarea } from "@/components/ui/textarea";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { NoResultsInRange, SortDateControls } from "@/components/lists/SortDateControls";
import { useListWindow } from "@/hooks/useListWindow";
import { hasDateRange, listWindowKey, listWindowParams } from "@/lib/list-window";

const PAGE_SIZE = 10;

/**
 * Mirrors `CampaignService.MaxRecipientsPerCampaign` and
 * `ConfirmAboveRecipients`.
 *
 * The server enforces the cap; these only save somebody filling in a form that
 * is going to be refused. If the two ever disagree the server wins, which is
 * the right way round.
 */
const MAX_RECIPIENTS_PER_CAMPAIGN = 1000;
const CONFIRM_ABOVE_RECIPIENTS = 200;

export default function CampaignsPage() {
  const [campaigns, setCampaigns] = useState<Campaign[]>([]);
  const [subscription, setSubscription] = useState<CurrentSubscription | null>(null);
  const [posters, setPosters] = useState<PosterSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [open, setOpen] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [patientsList, setPatientsList] = useState<Patient[]>([]);
  const [groupsList, setGroupsList] = useState<PatientGroup[]>([]);
  const [videosList, setVideosList] = useState<Video[]>([]);

  // Pagination + filtering
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState<"All" | Campaign["status"]>("All");

  // Sort and date range: in the URL, applied by the API (sent date, else created).
  const { listWindow, setListWindow, ready } = useListWindow();
  const windowKey = listWindowKey(listWindow);
  // Read through a ref so the in-flight poll, set up once, still asks for the
  // range showing now rather than the one showing when it started.
  const windowRef = useRef(listWindow);
  windowRef.current = listWindow;
  const campaignSeq = useRef(0);
  const loadedOnce = useRef(false);

  /** Every filter back to its default. Sort is a preference and stays. */
  function clearFilters() {
    setSearch("");
    setStatusFilter("All");
    setPage(1);
    setListWindow({ ...listWindow, from: null, to: null });
  }

  async function loadCampaigns() {
    const mine = ++campaignSeq.current;
    const c = await api.listCampaigns(listWindowParams(windowRef.current));
    if (mine === campaignSeq.current) setCampaigns(c);
  }

  async function refresh() {
    const mine = ++campaignSeq.current;
    const [c, s, p, pl, gl, vl] = await Promise.all([
      api.listCampaigns(listWindowParams(windowRef.current)),
      api.getCurrentSubscription(),
      api.listPosters({ take: 100 }),
      api.listPatients({ take: 500 }).catch(() => ({ items: [], total: 0, skip: 0, take: 0 })),
      api.listPatientGroups().catch(() => [] as PatientGroup[]),
      api.listVideos().catch(() => [] as Video[]),
    ]);
    if (mine === campaignSeq.current) setCampaigns(c);
    setSubscription(s);
    setPosters(p.items);
    setPatientsList(pl.items);
    setGroupsList(gl);
    // Only Ready videos are attachable; Pending/Rendering would break the send.
    setVideosList(vl.filter(v => v.status === "Ready"));
  }

  useEffect(() => {
    // Wait for the URL's sort and dates, so a shared link fetches once.
    if (!ready) return;
    let cancelled = false;
    (async () => {
      try {
        await refresh();
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load");
      } finally {
        loadedOnce.current = true;
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready]);

  // A new sort or range reloads the campaigns only — posters, patients and
  // videos for the send dialog do not depend on it.
  useEffect(() => {
    if (!loadedOnce.current) return;
    setPage(1);
    loadCampaigns().catch((err) =>
      setError(err instanceof Error ? err.message : "Failed to load"),
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [windowKey]);

  /**
   * Refresh while something is in flight, and stop when nothing is.
   *
   * Sending moved to a background worker, so creating a campaign now returns
   * immediately with `Queued` and the counts fill in behind it. Without this the
   * page showed "Queued · 0/1000 sent" until somebody reloaded, which looks
   * exactly like a campaign that failed silently.
   *
   * Conditional on purpose. A timer that runs whenever the page is open is a
   * request every three seconds for as long as a tab is left on the screen, for
   * a list that changes only while a send is running.
   */
  const anyInFlight = campaigns.some(
    (c) => c.status === "Queued" || c.status === "Sending",
  );

  useEffect(() => {
    if (!anyInFlight) return;

    let cancelled = false;
    const id = setInterval(() => {
      // Failures are ignored rather than surfaced: this is a background poll,
      // and one dropped request should not replace the page with an error.
      void refresh().catch(() => {});
      if (cancelled) clearInterval(id);
    }, 3_000);

    return () => {
      cancelled = true;
      clearInterval(id);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [anyInFlight]);

  async function stop(id: string) {
    setBusyId(id);
    setError(null);
    try {
      await api.stopCampaign(id);
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Stop failed");
    } finally {
      setBusyId(null);
    }
  }

  async function retry(id: string) {
    setBusyId(id);
    setError(null);
    try {
      await api.retryCampaign(id);
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Retry failed");
    } finally {
      setBusyId(null);
    }
  }

  const limit = subscription?.plan.maxCampaignRecipientsPerMonth ?? 0;
  const used = subscription?.usage.campaignRecipientsThisMonth ?? 0;
  const remaining = limit === undefined || isUnlimited(limit) ? Infinity : Math.max(0, limit - used);
  const canSend = subscription
    ? subscription.plan.maxCampaignRecipientsPerMonth !== 0
    : false;

  // Already sorted and date-ranged by the API; search and status narrow it
  // here, then it is sliced into pages. No re-sort: the server's order (sent
  // date, else created) is the one the Sort control promises.
  const filteredCampaigns = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return campaigns
      .filter((c) => statusFilter === "All" || c.status === statusFilter)
      .filter((c) => !needle || c.name.toLowerCase().includes(needle));
  }, [campaigns, search, statusFilter]);

  const totalPages = Math.max(1, Math.ceil(filteredCampaigns.length / PAGE_SIZE));
  const clampedPage = Math.min(page, totalPages);
  const pagedCampaigns = useMemo(
    () => filteredCampaigns.slice((clampedPage - 1) * PAGE_SIZE, clampedPage * PAGE_SIZE),
    [filteredCampaigns, clampedPage],
  );

  return (
    <main className="container max-w-5xl py-10">
      <div className="mb-6">
        <Button asChild variant="ghost" size="sm">
          <Link href="/dashboard">
            <ArrowLeft className="mr-1.5 h-4 w-4" />
            Back to dashboard
          </Link>
        </Button>
      </div>

      <header className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Campaigns</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Send posters to patients by email or SMS.
          </p>
        </div>

        <Button
          onClick={() => setOpen(true)}
          disabled={!canSend || posters.length === 0}
          className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground"
        >
          <PlusCircle className="mr-1.5 h-4 w-4" />
          New campaign
        </Button>
      </header>

      {/* Usage card */}
      {subscription && (
        <Card className="mt-6">
          <CardContent className="flex flex-col gap-3 pt-6 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <p className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                Patients reached this month
              </p>
              <p className="mt-1 text-2xl font-bold tracking-tight">
                {used}
                <span className="text-base font-normal text-muted-foreground">
                  {" / "}
                  {isUnlimited(limit) ? "Unlimited" : limit}
                </span>
              </p>
            </div>
            {!canSend && (
              <Badge variant="outline" className="self-start">
                Campaigns require Starter or Pro
              </Badge>
            )}
            {!isUnlimited(limit) && limit > 0 && (
              <div className="flex-1 sm:max-w-sm">
                <div className="h-2 overflow-hidden rounded-full bg-secondary">
                  <div
                    className="h-full bg-gradient-to-r from-primary to-emerald-500"
                    style={{ width: `${Math.min(100, (used / limit) * 100)}%` }}
                  />
                </div>
              </div>
            )}
            <Button asChild variant="outline" size="sm">
              <Link href="/pricing">Upgrade</Link>
            </Button>
          </CardContent>
        </Card>
      )}

      {error && (
        <p className="mt-4 text-sm text-destructive" role="alert">
          {error}
        </p>
      )}

      {/* Filters — only rendered once there's something worth filtering */}
      {/* A date range that matches nothing still needs its controls, or it
          could not be undone. */}
      {!loading && (campaigns.length > 0 || hasDateRange(listWindow)) && (
        <div className="mt-6 flex flex-wrap items-center gap-2">
          <div className="relative w-full max-w-xs">
            <Search className="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
            <input
              type="text"
              placeholder="Search campaigns…"
              value={search}
              onChange={(e) => { setSearch(e.target.value); setPage(1); }}
              className="h-9 w-full rounded-md border bg-background pl-8 pr-3 text-sm"
            />
          </div>
          <select
            value={statusFilter}
            onChange={(e) => {
              setStatusFilter(e.target.value as typeof statusFilter);
              setPage(1);
            }}
            className="h-9 rounded-md border bg-background px-2 text-sm"
          >
            <option value="All">All statuses</option>
            <option value="Draft">Draft</option>
            {/*
              Queued is the state every campaign now starts in, so leaving it
              out of the filter hid the newest ones — the list would go empty
              on "Sending" for a campaign that had just been created. The type
              is Campaign["status"], which already had it; only the menu did
              not, so nothing failed the build.
            */}
            <option value="Queued">Queued</option>
            <option value="Sending">Sending</option>
            <option value="Sent">Sent</option>
            <option value="Failed">Failed</option>
            <option value="Stopped">Stopped</option>
          </select>
          <span className="text-xs text-muted-foreground">
            {filteredCampaigns.length} of {campaigns.length}
          </span>
          <SortDateControls value={listWindow} onChange={setListWindow} dateField="Sent (or created)" />
        </div>
      )}

      {/* List */}
      <section className="mt-4">
        {loading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : campaigns.length === 0 && hasDateRange(listWindow) ? (
          <NoResultsInRange onClear={clearFilters} />
        ) : campaigns.length === 0 ? (
          <Card>
            <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
              <h3 className="text-base font-semibold">No campaigns yet</h3>
              <p className="max-w-sm text-sm text-muted-foreground">
                Pick a poster, drop in a list of patient emails or phone numbers,
                and we'll send your campaign.
              </p>
              <Button
                onClick={() => setOpen(true)}
                disabled={!canSend || posters.length === 0}
                className="mt-2"
              >
                Send your first campaign
              </Button>
            </CardContent>
          </Card>
        ) : filteredCampaigns.length === 0 ? (
          <Card>
            <CardContent className="py-10 text-center text-sm text-muted-foreground">
              No campaigns match your filter.
            </CardContent>
          </Card>
        ) : (
          <ul className="space-y-3">
            {pagedCampaigns.map((c) => (
              <li
                key={c.id}
                className={cn(
                  "flex flex-col gap-3 rounded-xl border bg-card p-4 sm:flex-row sm:items-center sm:justify-between",
                  c.status === "Failed" ? "border-destructive/40" : "border-border/60",
                )}
              >
                <div className="flex min-w-0 items-center gap-3">
                  <span className="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 text-primary">
                    {c.channel === "Email" ? (
                      <Mail className="h-4 w-4" />
                    ) : (
                      <MessageSquare className="h-4 w-4" />
                    )}
                  </span>
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium">{c.name}</p>
                    <p className="text-xs text-muted-foreground">
                      {c.channel} · {c.sentCount}/{c.recipientCount} sent
                      {c.videoIds.length > 0 && (
                        <span> · {c.videoIds.length} video{c.videoIds.length === 1 ? "" : "s"}</span>
                      )}
                      {" "}·{" "}
                      {new Date(c.createdAtUtc).toLocaleDateString()}
                    </p>
                    {c.note && (
                      <p
                        className={cn(
                          "mt-1 flex items-start gap-1 text-xs",
                          c.status === "Failed" ? "text-destructive" : "text-muted-foreground",
                        )}
                        title={c.note}
                      >
                        {c.status === "Failed" ? (
                          <AlertCircle className="mt-0.5 h-3 w-3 shrink-0" />
                        ) : (
                          <CheckCircle2 className="mt-0.5 h-3 w-3 shrink-0 text-emerald-500" />
                        )}
                        <span className="line-clamp-2">{c.note}</span>
                      </p>
                    )}
                  </div>
                </div>

                <div className="flex shrink-0 items-center gap-2">
                  <StatusBadge status={c.status} />
                  {(c.status === "Queued" || c.status === "Sending" || c.status === "Draft") && (
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => stop(c.id)}
                      disabled={busyId === c.id}
                    >
                      <Square className="mr-1.5 h-3.5 w-3.5" />
                      Stop
                    </Button>
                  )}
                  {/*
                    Offered on anything with people left to reach, not only on
                    Failed.

                    A campaign that got to 700 of 1,000 is Sent, because 700
                    people did receive it. Gating this button on Failed meant
                    the one case the delivery report exists for had no button at
                    all: the API would happily send to the missing 300, and
                    there was no way to ask it to. The pharmacist's only option
                    was to build a new campaign and work out for themselves who
                    had already been contacted.

                    Stopped is excluded deliberately. Retry refuses it, because
                    somebody chose to halt that send.
                  */}
                  {(c.status === "Failed"
                    || (c.status === "Sent" && c.sentCount < c.recipientCount)) && (
                    <Button
                      size="sm"
                      onClick={() => retry(c.id)}
                      disabled={busyId === c.id}
                      className="bg-brand-gradient text-primary-foreground hover:opacity-90"
                    >
                      <RotateCcw className="mr-1.5 h-3.5 w-3.5" />
                      {busyId === c.id
                        ? "Retrying…"
                        : c.status === "Failed"
                          ? "Retry"
                          // Named, so it is clear this reaches the people who
                          // were missed rather than sending the whole list a
                          // second time.
                          : `Send to remaining ${c.recipientCount - c.sentCount}`}
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}

        {!loading && filteredCampaigns.length > PAGE_SIZE && (
          <div className="mt-4 flex items-center justify-between text-xs text-muted-foreground">
            <span>
              Page {clampedPage} of {totalPages}
            </span>
            <div className="flex gap-1.5">
              <Button
                size="sm"
                variant="outline"
                disabled={clampedPage === 1}
                onClick={() => setPage(clampedPage - 1)}
              >
                <ChevronLeft className="mr-1 h-3.5 w-3.5" />
                Previous
              </Button>
              <Button
                size="sm"
                variant="outline"
                disabled={clampedPage >= totalPages}
                onClick={() => setPage(clampedPage + 1)}
              >
                Next
                <ChevronRight className="ml-1 h-3.5 w-3.5" />
              </Button>
            </div>
          </div>
        )}
      </section>

      <NewCampaignDialog
        open={open}
        onOpenChange={setOpen}
        posters={posters}
        patients={patientsList}
        groups={groupsList}
        videos={videosList}
        remaining={remaining}
        onSent={async () => {
          await refresh();
        }}
      />
    </main>
  );
}

function StatusBadge({ status }: { status: Campaign["status"] }) {
  if (status === "Sent")
    return <Badge className="bg-emerald-500/15 text-emerald-700 dark:text-emerald-400">Sent</Badge>;
  if (status === "Failed") return <Badge variant="destructive">Failed</Badge>;
  if (status === "Stopped") return <Badge variant="secondary">Stopped</Badge>;
  // Two waiting states, and the difference is worth showing. "Queued" means
  // accepted and safe to walk away from; "Sending" means it is going out now.
  // Without the first, a large campaign looked like nothing had happened for
  // the second or two before a worker picked it up.
  if (status === "Queued") return <Badge variant="outline">Queued</Badge>;
  if (status === "Sending") return <Badge variant="outline">Sending…</Badge>;
  return <Badge variant="outline">Draft</Badge>;
}

function NewCampaignDialog({
  open,
  onOpenChange,
  posters,
  patients,
  groups,
  videos,
  remaining,
  onSent,
}: {
  open: boolean;
  onOpenChange: (b: boolean) => void;
  posters: PosterSummary[];
  patients: Patient[];
  groups: PatientGroup[];
  videos: Video[];
  remaining: number;
  onSent: () => Promise<void>;
}) {
  const [name, setName] = useState("");
  const [posterId, setPosterId] = useState<string>("");
  // SMS is temporarily disabled — the backend rejects it and the UI reflects
  // that by disabling the SMS ChannelChoice. Hard-code Email so nothing else
  // in the component has to change.
  const [channel] = useState<CampaignChannel>("Email");
  const [recipientsText, setRecipientsText] = useState("");
  const [patientIds, setPatientIds] = useState<string[]>([]);
  const [groupIds, setGroupIds] = useState<string[]>([]);
  const [videoIds, setVideoIds] = useState<string[]>([]);
  const [patientSearch, setPatientSearch] = useState("");
  const [preview, setPreview] = useState<CampaignAudiencePreview | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  /** Set once the sender has been warned about a large audience. */
  const [confirmed, setConfirmed] = useState(false);

  /**
   * The posters a campaign may actually go out with.
   *
   * Draft means somebody is still working on it; archived means it has been
   * retired. Neither should reach a patient's inbox, and the API refuses both —
   * this is the same rule stated in the UI so nobody has to discover it by
   * being rejected at the last step.
   */
  const sendablePosters = useMemo(
    () => posters.filter((p) => p.status === "Published"),
    [posters],
  );

  const posterIsSendable = useMemo(
    () => sendablePosters.some((p) => p.id === posterId),
    [sendablePosters, posterId],
  );

  useEffect(() => {
    // Preselect a poster that can actually be sent. Defaulting to posters[0]
    // would often land on a draft, so the wizard would open with an unsendable
    // selection and a disabled Send button for no visible reason.
    const first = sendablePosters[0];
    if (open && !posterId && first) {
      setPosterId(first.id);
      if (!name) setName(`${first.name} — ${new Date().toLocaleDateString()}`);
    }
  }, [open, posterId, sendablePosters, name]);

  useEffect(() => {
    if (!open) {
      setRecipientsText("");
      setPatientIds([]);
      setGroupIds([]);
      setVideoIds([]);
      setPreview(null);
      setError(null);
    }
  }, [open]);

  const manualRecipients = useMemo(
    () =>
      recipientsText
        .split(/[\s,;]+/)
        .map((r) => r.trim())
        .filter(Boolean),
    [recipientsText],
  );

  // Debounced live audience preview. Immediately clear stale preview so the
  // Send-to-N count falls back to the local approximation while we wait.
  useEffect(() => {
    if (!open) return;
    setPreview(null);
    setPreviewing(true);
    const handle = setTimeout(async () => {
      try {
        const p = await api.campaignAudiencePreview({
          channel,
          recipients: manualRecipients,
          patientIds,
          groupIds,
        });
        setPreview(p);
      } catch {
        // ignore preview errors
      } finally {
        setPreviewing(false);
      }
    }, 250);
    return () => clearTimeout(handle);
  }, [open, channel, manualRecipients, patientIds, groupIds]);

  // Rough local count — used to keep the Send button enabled and the "Send to N"
  // label responsive to clicks BEFORE the debounced audience-preview API arrives.
  // Once the preview lands, we prefer its exact deduped count.
  const groupPatientCount = useMemo(
    () => groups
      .filter(g => groupIds.includes(g.id))
      .reduce((acc, g) => acc + (g.patientCount ?? 0), 0),
    [groups, groupIds],
  );
  const localRecipientCount =
    patientIds.length + groupPatientCount + manualRecipients.length;
  const totalRecipients = preview?.totalUnique ?? localRecipientCount;
  const filteredPatients = useMemo(() => {
    const needle = patientSearch.trim().toLowerCase();
    const base = patients.filter(p => channel === "Email" ? p.email : p.phone);
    if (!needle) return base.slice(0, 50);
    return base.filter(p =>
      p.fullName.toLowerCase().includes(needle)
      || (p.email ?? "").toLowerCase().includes(needle)
      || (p.phone ?? "").toLowerCase().includes(needle),
    ).slice(0, 50);
  }, [patients, patientSearch, channel]);

  // Functional setState so we always toggle against the LATEST array — not a
  // stale value captured at render time. That fix makes rapid selection work.
  function toggleId(
    setter: React.Dispatch<React.SetStateAction<string[]>>,
    id: string,
  ) {
    setter((prev) =>
      prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id],
    );
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    if (totalRecipients === 0) {
      setError("Add at least one recipient (manual, patient, or group).");
      return;
    }
    // The API refuses this too, and that refusal is the one that counts. Saying
    // it here as well means the answer arrives before somebody has assembled an
    // audience of five hundred, rather than after.
    const chosen = posters.find((p) => p.id === posterId);
    if (!chosen || chosen.status !== "Published") {
      setError(
        chosen
          ? `'${chosen.name}' is ${chosen.status.toLowerCase()}. Open it and choose Publish before sending it to patients.`
          : "Choose a poster to send.",
      );
      return;
    }
    if (Number.isFinite(remaining) && totalRecipients > remaining) {
      setError(
        `That's ${totalRecipients} recipients but you have ${remaining} left on your plan.`,
      );
      return;
    }

    // The server refuses above 1,000; this says so before the work of filling
    // the form is wasted.
    if (totalRecipients > MAX_RECIPIENTS_PER_CAMPAIGN) {
      setError(
        `A single campaign can go to at most ${MAX_RECIPIENTS_PER_CAMPAIGN.toLocaleString()} people. ` +
          `This one has ${totalRecipients.toLocaleString()}. Split it, or narrow the group.`,
      );
      return;
    }

    // A large send cannot be recalled, so it should be a decision rather than
    // the result of a click. Only above the threshold — a confirmation on every
    // send is one people learn to dismiss without reading, which is worse than
    // not asking.
    if (totalRecipients > CONFIRM_ABOVE_RECIPIENTS && !confirmed) {
      setConfirmed(true);
      return;
    }

    setPending(true);
    try {
      await api.createCampaign({
        posterId,
        name: name.trim(),
        channel,
        recipients: manualRecipients,
        patientIds,
        groupIds,
        videoIds,
      });
      await onSent();
      onOpenChange(false);
      setName("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Send failed");
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[90vh] max-w-2xl flex-col">
        <DialogHeader className="shrink-0">
          <DialogTitle>New campaign</DialogTitle>
          <DialogDescription>
            Pick a poster, choose a channel, and build your audience from patients,
            groups, or manual entries.
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="flex min-h-0 flex-1 flex-col">
          <div className="min-h-0 flex-1 space-y-4 overflow-y-auto pr-1">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="name">Campaign name</Label>
              <Input
                id="name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Flu shots — September"
                required
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="posterId">Poster</Label>
              <select
                id="posterId"
                value={posterId}
                onChange={(e) => setPosterId(e.target.value)}
                className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
              >
                {posters.length === 0 && <option value="">No posters available</option>}
                {/*
                  Unpublished posters are listed and disabled, not hidden.
                  Somebody looking for the poster they just made needs to find
                  it and learn why it cannot be sent yet — a poster that has
                  simply vanished from the list looks like the app has lost it.
                */}
                {posters.map((p) => (
                  <option key={p.id} value={p.id} disabled={p.status !== "Published"}>
                    {p.status === "Published" ? p.name : `${p.name} — ${p.status.toLowerCase()}, publish it first`}
                  </option>
                ))}
              </select>

              {sendablePosters.length === 0 && posters.length > 0 && (
                <p className="text-xs text-muted-foreground">
                  None of your posters are published yet. Open a poster and
                  choose <strong>Publish</strong> to send it to patients.
                </p>
              )}
            </div>
          </div>

          <div className="space-y-2">
            <Label>Channel</Label>
            <div className="grid grid-cols-2 gap-2">
              <ChannelChoice
                active={channel === "Email"}
                onClick={() => { /* Email is the only enabled channel */ }}
                icon={Mail}
                label="Email"
                hint="PDF + video links"
              />
              <button
                type="button"
                disabled
                title="SMS campaigns are temporarily disabled"
                className="flex cursor-not-allowed items-start gap-2 rounded-md border border-dashed border-border bg-muted/20 p-3 text-left opacity-60"
              >
                <MessageSquare className="mt-0.5 h-4 w-4 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-medium">SMS</p>
                  <p className="text-[10px] text-muted-foreground">
                    Coming soon — email only for now
                  </p>
                </div>
              </button>
            </div>
          </div>

          {/* Videos — multi-select. Only Ready videos come through from the
              parent's refresh(); Pending/Rendering aren't attachable. Empty
              list is fine — the section just doesn't render. */}
          {videos.length > 0 && (
            <div className="space-y-2">
              <Label>Attach videos <span className="text-xs text-muted-foreground">(optional, up to 5)</span></Label>
              <div className="max-h-40 overflow-y-auto rounded-md border bg-background">
                {videos.map(v => {
                  const checked = videoIds.includes(v.id);
                  const disabled = !checked && videoIds.length >= 5;
                  return (
                    <label
                      key={v.id}
                      className={cn(
                        "flex cursor-pointer items-center gap-2 border-b px-3 py-2 text-sm last:border-b-0",
                        checked && "bg-primary/5",
                        disabled && "cursor-not-allowed opacity-50",
                      )}
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        disabled={disabled}
                        onChange={() => toggleId(setVideoIds, v.id)}
                        className="shrink-0"
                      />
                      {v.thumbnailUrl && (
                        /* eslint-disable-next-line @next/next/no-img-element */
                        <img
                          src={v.thumbnailUrl}
                          alt=""
                          className="h-10 w-8 rounded object-cover"
                        />
                      )}
                      <span className="flex min-w-0 flex-1 items-baseline gap-2">
                        <span className="truncate font-medium">{v.name}</span>
                        <span className="truncate text-xs text-muted-foreground">
                          {v.durationSeconds}s · {v.aspect}
                        </span>
                      </span>
                    </label>
                  );
                })}
              </div>
              {videoIds.length > 0 && (
                <p className="text-xs text-muted-foreground">
                  {videoIds.length} video{videoIds.length === 1 ? "" : "s"} — added as signed 48h links in the email
                </p>
              )}
            </div>
          )}

          {/* Groups */}
          {groups.length > 0 && (
            <div className="space-y-2">
              <Label>Groups</Label>
              <div className="max-h-32 overflow-y-auto rounded-md border bg-background/50 p-2">
                <div className="flex flex-wrap gap-2">
                  {groups.map(g => {
                    const selected = groupIds.includes(g.id);
                    return (
                      <button
                        key={g.id}
                        type="button"
                        onClick={() => toggleId(setGroupIds, g.id)}
                        className={cn(
                          "max-w-full truncate rounded-full border px-3 py-1 text-xs transition",
                          selected
                            ? "border-primary bg-primary/10 text-primary"
                            : "border-border text-muted-foreground hover:bg-accent",
                        )}
                      >
                        <span className="truncate">{g.name}</span> · {g.patientCount}
                      </button>
                    );
                  })}
                </div>
              </div>
              {groupIds.length > 0 && (
                <p className="text-xs text-muted-foreground">
                  {groupIds.length} group{groupIds.length === 1 ? "" : "s"} selected
                </p>
              )}
            </div>
          )}

          {/* Patients picker */}
          {patients.length > 0 && (
            <div className="space-y-2">
              <Label>Individual patients</Label>
              <Input
                placeholder="Search patients…"
                value={patientSearch}
                onChange={e => setPatientSearch(e.target.value)}
              />
              <div className="max-h-56 overflow-y-auto rounded-md border bg-background">
                {filteredPatients.length === 0 ? (
                  <p className="p-3 text-xs text-muted-foreground">No matching patients.</p>
                ) : filteredPatients.map(p => {
                  const checked = patientIds.includes(p.id);
                  return (
                    <label
                      key={p.id}
                      className={cn(
                        "flex cursor-pointer items-center gap-2 border-b px-3 py-2 text-sm last:border-b-0",
                        checked && "bg-primary/5",
                      )}
                    >
                      <input
                        type="checkbox"
                        checked={checked}
                        onChange={() => toggleId(setPatientIds, p.id)}
                        className="shrink-0"
                      />
                      <span className="flex min-w-0 flex-1 items-baseline gap-2">
                        <span className="truncate font-medium">{p.fullName}</span>
                        <span className="truncate text-xs text-muted-foreground">
                          {channel === "Email" ? p.email : p.phone}
                        </span>
                      </span>
                    </label>
                  );
                })}
              </div>
              {patientIds.length > 0 && (
                <p className="text-xs text-muted-foreground">
                  {patientIds.length} patient{patientIds.length === 1 ? "" : "s"} selected
                </p>
              )}
            </div>
          )}

          {/* Manual recipients */}
          <div className="space-y-2">
            <Label htmlFor="recipients">
              Additional recipients{" "}
              <span className="text-xs text-muted-foreground">
                (optional — one per line or comma-separated)
              </span>
            </Label>
            <Textarea
              id="recipients"
              value={recipientsText}
              onChange={(e) => setRecipientsText(e.target.value)}
              rows={3}
              placeholder={
                channel === "Email"
                  ? "patient1@example.com\npatient2@example.com"
                  : "+44 7700 900123\n+44 7700 900456"
              }
            />
          </div>

          {/* Audience preview */}
          <div className="rounded-lg border bg-muted/40 p-3 text-sm">
            <div className="flex items-center justify-between">
              <div>
                <p className="font-semibold">
                  {totalRecipients} unique recipient{totalRecipients === 1 ? "" : "s"}
                  {previewing && (
                    <span className="ml-1 font-normal text-muted-foreground">· checking…</span>
                  )}
                </p>
                {preview && (
                  <p className="text-xs text-muted-foreground">
                    {preview.fromGroups} group · {preview.fromPatients} patient ·{" "}
                    {preview.fromManual} manual
                    {preview.invalidCount > 0 && ` · ${preview.invalidCount} skipped (missing ${channel === "Email" ? "email" : "phone"})`}
                    {(preview.unsubscribedCount ?? 0) > 0 && ` · ${preview.unsubscribedCount} unsubscribed, left out`}
                  </p>
                )}
              </div>
              {Number.isFinite(remaining) && (
                <Badge variant="outline" className="self-start">
                  {remaining} left this month
                </Badge>
              )}
            </div>
            {preview && preview.sampleRecipients.length > 0 && (
              <p className="mt-2 truncate text-xs text-muted-foreground">
                Preview: {preview.sampleRecipients.join(", ")}
                {totalRecipients > preview.sampleRecipients.length && "…"}
              </p>
            )}
          </div>

          {error && (
            <p className="text-sm text-destructive" role="alert">
              {error}
            </p>
          )}
          </div>

          {confirmed && !pending && (
            <p
              role="alert"
              className="shrink-0 rounded-md border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm"
            >
              This goes to <strong>{totalRecipients.toLocaleString()} people</strong> and
              cannot be recalled. Press send again to confirm.
            </p>
          )}

          <DialogFooter className="shrink-0 border-t pt-4">
            <Button
              type="button"
              variant="ghost"
              onClick={() => {
                setConfirmed(false);
                onOpenChange(false);
              }}
            >
              Cancel
            </Button>
            <Button
              type="submit"
              disabled={pending || totalRecipients === 0 || !posterIsSendable}
              className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground"
            >
              {pending
                ? "Sending…"
                : confirmed
                  ? `Yes, send to ${totalRecipients.toLocaleString()}`
                  : `Send to ${totalRecipients.toLocaleString()}`}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function ChannelChoice({
  active,
  onClick,
  icon: Icon,
  label,
  hint,
}: {
  active: boolean;
  onClick: () => void;
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  hint: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "flex items-start gap-3 rounded-lg border p-3 text-left transition-all",
        active
          ? "border-primary bg-primary/5 ring-1 ring-primary/30"
          : "border-border/60 hover:border-border",
      )}
    >
      <span
        className={cn(
          "flex h-9 w-9 shrink-0 items-center justify-center rounded-md",
          active ? "bg-primary text-primary-foreground" : "bg-muted text-muted-foreground",
        )}
      >
        <Icon className="h-4 w-4" />
      </span>
      <span>
        <span className="block text-sm font-semibold">{label}</span>
        <span className="text-xs text-muted-foreground">{hint}</span>
      </span>
    </button>
  );
}

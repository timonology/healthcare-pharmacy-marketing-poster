"use client";

import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, Plus, X } from "lucide-react";
import type {
  AdsBudgetKind,
  AdsCampaignType,
  PublishSourceKind,
  SocialConnection,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";

/** Google's limits for responsive ads, which all three types use. */
const HEADLINE_MAX = 30;
const DESCRIPTION_MAX = 90;
const MIN_HEADLINES = 3;
const MAX_HEADLINES = 15;
const MIN_DESCRIPTIONS = 2;
const MAX_DESCRIPTIONS = 5;
const MAX_AREAS = 20;

const TYPES: { value: AdsCampaignType; label: string; note: string }[] = [
  {
    value: "Search",
    label: "Search",
    note: "Text ads when people search for things like “flu jab near me”. Works without conversion tracking.",
  },
  {
    value: "PerformanceMax",
    label: "Performance Max",
    note: "Google shows your poster across Search, Maps, YouTube and more. Needs your logo, and works best with conversion tracking.",
  },
];

const KEYWORD_MAX = 80;
const MAX_KEYWORDS = 20;
const LONG_HEADLINE_MAX = 90;
const BUSINESS_NAME_MAX = 25;

type Creative = { kind: PublishSourceKind; id: string; name: string };

/** Today in the browser's zone, as yyyy-MM-dd — a calendar day, not an instant. */
function today(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/**
 * Creates a Google Ads campaign from one of the pharmacy's posters.
 *
 * Everything the server refuses is checked here first, with the same numbers,
 * so the pharmacist fixes it while typing rather than after a round trip — but
 * the server stays the authority. The campaign is always created **paused**;
 * starting it is a separate, confirmed step on the campaign card.
 *
 * Areas are place names or postcode districts ("Leeds", "LS6"), matched to
 * Google's own locations when the campaign is created. At least one is
 * required: without one, an ad runs everywhere Google serves ads.
 */
export function CreateCampaignDialog({
  open,
  onClose,
  connections,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  /** Active Google Ads connections. The dialog is not offered without one. */
  connections: SocialConnection[];
  onCreated: () => void;
}) {
  const [creatives, setCreatives] = useState<Creative[] | null>(null);
  const [connectionId, setConnectionId] = useState("");
  const [name, setName] = useState("");
  const [type, setType] = useState<AdsCampaignType>("Search");
  const [creativeKey, setCreativeKey] = useState("");
  const [finalUrl, setFinalUrl] = useState("https://");
  const [budget, setBudget] = useState("5");
  const [budgetKind, setBudgetKind] = useState<AdsBudgetKind>("Daily");
  const [startDate, setStartDate] = useState(today());
  const [endDate, setEndDate] = useState("");
  const [areas, setAreas] = useState<string[]>([""]);
  const [headlines, setHeadlines] = useState<string[]>(["", "", ""]);
  const [descriptions, setDescriptions] = useState<string[]>(["", ""]);
  const [keywords, setKeywords] = useState<string[]>([""]);
  const [longHeadline, setLongHeadline] = useState("");
  const [businessName, setBusinessName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  /** The Ads account's own currency — every budget amount is in it. */
  const [currency, setCurrency] = useState<string | null>(null);

  // The parent rebuilds its connection list on every render; keying on the
  // ids stops each re-render from resetting the form and refetching.
  const connectionKey = connections.map((c) => c.id).join(",");

  useEffect(() => {
    if (!open) return;
    // A fresh form each time. A dialog reopened after creating a campaign
    // that still holds the last one's details is how the same campaign gets
    // created twice.
    setError(null);
    setConnectionId(connections[0]?.id ?? "");
    setCreatives(null);
    setName("");
    setType("Search");
    setCreativeKey("");
    setFinalUrl("https://");
    setBudget("5");
    setBudgetKind("Daily");
    setStartDate(today());
    setEndDate("");
    setAreas([""]);
    setHeadlines(["", "", ""]);
    setDescriptions(["", ""]);
    setKeywords([""]);
    setLongHeadline("");
    setBusinessName("");

    // Published posters only: the same rule as publishing to social —
    // nothing that has not been signed off goes out as an ad.
    api
      .listPosters({ status: "Published", take: 100 })
      .then((posters) =>
        setCreatives(posters.items.map((p) => ({ kind: "Poster" as const, id: p.id, name: p.name }))),
      )
      .catch((err: unknown) =>
        setError(err instanceof Error ? err.message : "Could not load your posters."),
      );
    // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed on connectionKey on purpose
  }, [open, connectionKey]);

  // The chosen account's currency, from Google. Unknown stays unlabelled; the
  // server checks the currency again and says so if it differs.
  useEffect(() => {
    if (!open || !connectionId) return;
    const connection = connections.find((c) => c.id === connectionId);
    setCurrency(null);
    let cancelled = false;
    api.ads
      .accounts(connectionId)
      .then((accounts) => {
        const digits = (v: string) => v.replace(/\D/g, "");
        const match = accounts.find(
          (a) => connection && digits(a.customerId) === digits(connection.externalAccountId),
        );
        if (!cancelled) setCurrency(match?.currencyCode ?? null);
      })
      .catch(() => {
        if (!cancelled) setCurrency(null);
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed on connectionKey on purpose
  }, [open, connectionId, connectionKey]);

  // Posters only for now: Performance Max would need a video on YouTube, and
  // Search shows no image at all (the poster names the campaign's theme).
  const offered = useMemo(
    () => (creatives ?? []).filter((c) => c.kind === "Poster"),
    [creatives],
  );
  const creative = offered.find((c) => `${c.kind}:${c.id}` === creativeKey) ?? null;

  const cleanAreas = areas.map((a) => a.trim()).filter(Boolean);
  const cleanHeadlines = headlines.map((h) => h.trim()).filter(Boolean);
  const cleanDescriptions = descriptions.map((d) => d.trim()).filter(Boolean);
  const cleanKeywords = keywords.map((k) => k.trim()).filter(Boolean);
  const isSearch = type === "Search";
  const budgetValue = Number(budget);

  /** The first thing stopping submission, in words — or null when ready. */
  const blocker =
    !connectionId ? "Choose a Google Ads account."
    : !name.trim() ? "Give the campaign a name."
    : !creative ? "Choose a poster or video."
    : !/^https:\/\/[^\s/]+\.[^\s]+$/.test(finalUrl.trim()) ? "Enter the landing page, starting https://"
    : !(budgetValue >= 1) ? "The budget must be at least 1.00."
    : budgetKind === "Daily" && budgetValue > 1000 ? "That daily budget is over 1,000 — check for an extra zero."
    : budgetKind === "Lifetime" && budgetValue > 20000 ? "That total is over 20,000 — check for an extra zero."
    : endDate && endDate < startDate ? "The end date is before the start date."
    : budgetKind === "Lifetime" && !endDate ? "A total budget needs an end date."
    : cleanAreas.length === 0 ? "Add at least one area."
    : cleanHeadlines.length < MIN_HEADLINES ? `Add at least ${MIN_HEADLINES} headlines.`
    : cleanDescriptions.length < MIN_DESCRIPTIONS ? `Add at least ${MIN_DESCRIPTIONS} descriptions.`
    : isSearch && cleanKeywords.length === 0 ? "Add at least one search term."
    : !isSearch && !longHeadline.trim() ? "Add a long headline."
    : !isSearch && !businessName.trim() ? "Add your business name as it should appear."
    : null;

  async function submit() {
    if (blocker || !creative) return;
    setBusy(true);
    setError(null);
    try {
      await api.ads.createCampaign({
        connectionId,
        name: name.trim(),
        campaignType: type,
        budgetKind,
        budget: Math.round(budgetValue * 100) / 100,
        startDate,
        endDate: endDate || null,
        finalUrl: finalUrl.trim(),
        creativeKind: creative.kind,
        creativeSourceId: creative.id,
        locations: cleanAreas.map((a) => ({
          geoTargetConstantId: null,
          displayName: a,
          latitude: null,
          longitude: null,
          radiusKm: null,
        })),
        headlines: cleanHeadlines,
        descriptions: cleanDescriptions,
        ...(currency ? { currencyCode: currency } : {}),
        ...(isSearch
          ? { keywords: cleanKeywords }
          : { longHeadline: longHeadline.trim(), businessName: businessName.trim() }),
      });
      onCreated();
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not create the campaign.");
    } finally {
      setBusy(false);
    }
  }

  if (!open) return null;

  return (
    <Dialog open onOpenChange={(v) => !v && onClose()}>
      <DialogContent className="sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>New Google Ads campaign</DialogTitle>
          <DialogDescription>
            Created paused. Nothing is spent until you start it.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-5">
          <div className="flex items-start gap-2 rounded-md border border-amber-500/30 bg-amber-500/10 p-2.5 text-[11px] text-amber-900 dark:text-amber-200">
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span>
              Advertise services, not prescription medicines — advertising
              prescription-only medicines to the public is not permitted in the UK.
            </span>
          </div>

          {/* ---- basics ---- */}
          <section className="grid gap-3 sm:grid-cols-2">
            {connections.length > 1 && (
              <div className="space-y-1.5 sm:col-span-2">
                <Label htmlFor="ads-account" className="text-xs">Google Ads account</Label>
                <select
                  id="ads-account"
                  value={connectionId}
                  onChange={(e) => setConnectionId(e.target.value)}
                  className="h-9 w-full rounded-md border border-input bg-background px-2 text-sm"
                >
                  {connections.map((c) => (
                    <option key={c.id} value={c.id}>
                      {c.displayName} ({c.externalAccountId})
                    </option>
                  ))}
                </select>
              </div>
            )}

            <div className="space-y-1.5 sm:col-span-2">
              <Label htmlFor="ads-name" className="text-xs">Campaign name</Label>
              <Input
                id="ads-name"
                value={name}
                maxLength={128}
                onChange={(e) => setName(e.target.value)}
                placeholder="Flu jabs — autumn"
              />
            </div>
          </section>

          {/* ---- type ---- */}
          <section className="space-y-1.5">
            <Label className="text-xs">Where it appears</Label>
            <div className="grid gap-1.5 sm:grid-cols-2">
              {TYPES.map((t) => (
                <button
                  key={t.value}
                  type="button"
                  onClick={() => setType(t.value)}
                  aria-pressed={type === t.value}
                  className={cn(
                    "rounded-md border p-2.5 text-left transition-colors",
                    type === t.value ? "border-primary bg-primary/5" : "border-border/60 hover:bg-accent/50",
                  )}
                >
                  <span className="block text-sm font-medium">{t.label}</span>
                  <span className="mt-0.5 block text-[11px] text-muted-foreground">{t.note}</span>
                </button>
              ))}
            </div>
          </section>

          {/* ---- creative + landing ---- */}
          <section className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor="ads-creative" className="text-xs">Poster</Label>
              <select
                id="ads-creative"
                value={creativeKey}
                onChange={(e) => setCreativeKey(e.target.value)}
                disabled={creatives === null}
                className="h-9 w-full rounded-md border border-input bg-background px-2 text-sm"
              >
                <option value="">
                  {creatives === null ? "Loading…" : offered.length ? "Choose…" : "Nothing published yet"}
                </option>
                {offered.map((c) => (
                  <option key={`${c.kind}:${c.id}`} value={`${c.kind}:${c.id}`}>
                    {c.name}
                  </option>
                ))}
              </select>
              <p className="text-[11px] text-muted-foreground">
                {isSearch
                  ? "Search ads are text; the poster names what the campaign is for."
                  : "Shown as the ad's image, fitted to Google's shapes."}
              </p>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="ads-url" className="text-xs">Landing page</Label>
              <Input
                id="ads-url"
                type="url"
                value={finalUrl}
                onChange={(e) => setFinalUrl(e.target.value)}
                placeholder="https://yourpharmacy.co.uk/flu"
              />
              <p className="text-[11px] text-muted-foreground">Where a click takes people.</p>
            </div>
          </section>

          {/* ---- budget + dates ---- */}
          <section className="grid gap-3 sm:grid-cols-3">
            <div className="space-y-1.5">
              <Label htmlFor="ads-budget" className="text-xs">
                Budget{currency ? ` (${currency})` : ""}
              </Label>
              <Input
                id="ads-budget"
                type="number"
                min={1}
                step="0.5"
                value={budget}
                onChange={(e) => setBudget(e.target.value)}
              />
              <div className="flex gap-1">
                {(["Daily", "Lifetime"] as AdsBudgetKind[]).map((k) => (
                  <Button
                    key={k}
                    type="button"
                    size="sm"
                    variant={budgetKind === k ? "default" : "outline"}
                    className="h-6 flex-1 px-1 text-[11px]"
                    onClick={() => setBudgetKind(k)}
                  >
                    {k === "Daily" ? "Per day" : "In total"}
                  </Button>
                ))}
              </div>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ads-start" className="text-xs">Start</Label>
              <Input
                id="ads-start"
                type="date"
                min={today()}
                value={startDate}
                onChange={(e) => setStartDate(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="ads-end" className="text-xs">End (optional)</Label>
              <Input
                id="ads-end"
                type="date"
                min={startDate}
                value={endDate}
                onChange={(e) => setEndDate(e.target.value)}
              />
            </div>
          </section>

          {/* ---- areas ---- */}
          <TextList
            label="Areas"
            hint="Towns or postcode districts — e.g. Leeds, LS6. People elsewhere will not see the ad."
            values={areas}
            onChange={setAreas}
            max={MAX_AREAS}
            maxLength={100}
            placeholder="LS6"
            idPrefix="ads-area"
          />

          {isSearch ? (
            <TextList
              label="Search terms"
              hint="What people type. Matched as phrases — “flu jab” also matches “flu jab near me”."
              values={keywords}
              onChange={setKeywords}
              max={MAX_KEYWORDS}
              maxLength={KEYWORD_MAX}
              placeholder="flu jab"
              idPrefix="ads-keyword"
            />
          ) : (
            <section className="grid gap-3 sm:grid-cols-3">
              <div className="space-y-1.5 sm:col-span-2">
                <Label htmlFor="ads-long-headline" className="text-xs">Long headline</Label>
                <Input
                  id="ads-long-headline"
                  value={longHeadline}
                  maxLength={LONG_HEADLINE_MAX}
                  onChange={(e) => setLongHeadline(e.target.value)}
                  placeholder="Flu jabs at your local pharmacy — walk in today"
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="ads-business" className="text-xs">Business name</Label>
                <Input
                  id="ads-business"
                  value={businessName}
                  maxLength={BUSINESS_NAME_MAX}
                  onChange={(e) => setBusinessName(e.target.value)}
                  placeholder="Main St Pharmacy"
                />
              </div>
              <p className="text-[11px] text-muted-foreground sm:col-span-3">
                Your Brand kit logo is used alongside the business name.
              </p>
            </section>
          )}

          {/* ---- copy ---- */}
          <TextList
            label="Headlines"
            hint={`${MIN_HEADLINES}–${MAX_HEADLINES}, up to ${HEADLINE_MAX} characters. Google mixes and matches them.`}
            values={headlines}
            onChange={setHeadlines}
            max={MAX_HEADLINES}
            maxLength={HEADLINE_MAX}
            placeholder="Flu jabs — walk in today"
            idPrefix="ads-headline"
            showCount
          />
          <TextList
            label="Descriptions"
            hint={`${MIN_DESCRIPTIONS}–${MAX_DESCRIPTIONS}, up to ${DESCRIPTION_MAX} characters.`}
            values={descriptions}
            onChange={setDescriptions}
            max={MAX_DESCRIPTIONS}
            maxLength={DESCRIPTION_MAX}
            placeholder="Free for over-65s and eligible groups. No appointment needed."
            idPrefix="ads-description"
            showCount
          />

          {(error || blocker) && (
            <p
              role={error ? "alert" : undefined}
              className={cn(
                "rounded-md px-3 py-2 text-xs",
                error ? "bg-destructive/10 text-destructive" : "bg-muted text-muted-foreground",
              )}
            >
              {error ?? blocker}
            </p>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button size="sm" disabled={busy || blocker !== null} onClick={submit}>
            {busy ? "Creating…" : "Create paused campaign"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** A short list of single-line entries, each removable, with an add button. */
function TextList({
  label,
  hint,
  values,
  onChange,
  max,
  maxLength,
  placeholder,
  idPrefix,
  showCount = false,
}: {
  label: string;
  hint: string;
  values: string[];
  onChange: (next: string[]) => void;
  max: number;
  maxLength: number;
  placeholder: string;
  idPrefix: string;
  showCount?: boolean;
}) {
  return (
    <section className="space-y-1.5">
      <Label htmlFor={`${idPrefix}-0`} className="text-xs">{label}</Label>
      <ul className="space-y-1.5">
        {values.map((value, i) => (
          <li key={i} className="flex items-center gap-1.5">
            <Input
              id={`${idPrefix}-${i}`}
              value={value}
              maxLength={maxLength}
              placeholder={i === 0 ? placeholder : undefined}
              onChange={(e) => onChange(values.map((v, j) => (j === i ? e.target.value : v)))}
              className="h-8 text-sm"
            />
            {showCount && (
              <span className="w-10 shrink-0 text-right text-[10px] tabular-nums text-muted-foreground">
                {value.length}/{maxLength}
              </span>
            )}
            {values.length > 1 && (
              <Button
                type="button"
                size="sm"
                variant="ghost"
                className="h-8 w-8 shrink-0 p-0 text-muted-foreground"
                aria-label={`Remove ${label.toLowerCase()} ${i + 1}`}
                onClick={() => onChange(values.filter((_, j) => j !== i))}
              >
                <X className="h-3.5 w-3.5" />
              </Button>
            )}
          </li>
        ))}
      </ul>
      <div className="flex items-center justify-between gap-2">
        <p className="text-[11px] text-muted-foreground">{hint}</p>
        {values.length < max && (
          <Button
            type="button"
            size="sm"
            variant="ghost"
            className="h-7 shrink-0 gap-1 text-xs"
            onClick={() => onChange([...values, ""])}
          >
            <Plus className="h-3.5 w-3.5" />
            Add
          </Button>
        )}
      </div>
    </section>
  );
}

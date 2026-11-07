"use client";

import type { TikTokCreatorInfo, TikTokPrivacyLevel } from "@acme/shared-types";
import { Label } from "@/components/ui/label";
import {
  commercialLabel,
  consentText,
  privacyDisabled,
  privacyLabel,
  type TikTokForm,
} from "@/lib/tiktok-post";
import { cn } from "@/lib/utils";

/**
 * TikTok's part of the share screen, laid out as its Content Sharing
 * Guidelines ask: whose account this posts to, an audience picked from
 * TikTok's own list with nothing pre-selected, interaction switches that
 * start off, the commercial-content declaration, and the consent line.
 */
export function TikTokPostSettings({
  info,
  loading,
  error,
  form,
  onChange,
}: {
  info: TikTokCreatorInfo | null;
  loading: boolean;
  error: string | null;
  form: TikTokForm;
  onChange: (next: TikTokForm) => void;
}) {
  const set = (patch: Partial<TikTokForm>) => onChange({ ...form, ...patch });

  if (loading) return <p className="text-xs text-muted-foreground">Asking TikTok for this account&rsquo;s settings…</p>;
  if (error || !info)
    return (
      <p role="alert" className="rounded-md bg-destructive/10 px-3 py-2 text-xs text-destructive">
        {error ?? "TikTok did not answer. Close this and try again."}
      </p>
    );

  return (
    <section className="space-y-3 rounded-md border border-border/60 p-3" aria-label="TikTok settings">
      <p className="text-xs">
        Posting to TikTok as <span className="font-medium">{info.nickname || info.username}</span>
        {info.username && <span className="text-muted-foreground"> (@{info.username})</span>}
      </p>

      <div className="space-y-1">
        <Label htmlFor="tiktok-privacy" className="text-xs">
          Who can see this post
        </Label>
        <select
          id="tiktok-privacy"
          className="h-9 w-full rounded-md border border-input bg-background px-2 text-sm"
          value={form.privacyLevel}
          onChange={(e) => set({ privacyLevel: e.target.value as TikTokPrivacyLevel | "" })}
        >
          <option value="" disabled>
            Choose…
          </option>
          {info.privacyLevelOptions.map((level) => (
            <option key={level} value={level} disabled={privacyDisabled(form, level)}>
              {privacyLabel(level)}
              {privacyDisabled(form, level) ? " — not for branded content" : ""}
            </option>
          ))}
        </select>
      </div>

      <fieldset className="space-y-1.5">
        <legend className="mb-1 text-xs font-medium">Allow others to</legend>
        <Toggle
          label="Comment"
          checked={form.allowComments}
          disabled={info.commentDisabled}
          note={info.commentDisabled ? "Turned off in your TikTok settings" : undefined}
          onChange={(v) => set({ allowComments: v })}
        />
        <Toggle
          label="Duet"
          checked={form.allowDuet}
          disabled={info.duetDisabled}
          note={info.duetDisabled ? "Turned off in your TikTok settings" : undefined}
          onChange={(v) => set({ allowDuet: v })}
        />
        <Toggle
          label="Stitch"
          checked={form.allowStitch}
          disabled={info.stitchDisabled}
          note={info.stitchDisabled ? "Turned off in your TikTok settings" : undefined}
          onChange={(v) => set({ allowStitch: v })}
        />
      </fieldset>

      <fieldset className="space-y-1.5">
        <Toggle
          label="Disclose commercial content"
          checked={form.discloseCommercial}
          note="Turn on if this promotes your pharmacy, a brand or a product."
          onChange={(v) =>
            set(v ? { discloseCommercial: true } : { discloseCommercial: false, promotesOwnBusiness: false, isPaidPartnership: false })
          }
        />
        {form.discloseCommercial && (
          <div className="ml-5 space-y-1.5">
            <Toggle
              label="Your brand"
              checked={form.promotesOwnBusiness}
              note="You are promoting yourself or your own business."
              onChange={(v) => set({ promotesOwnBusiness: v })}
            />
            <Toggle
              label="Branded content"
              checked={form.isPaidPartnership}
              note="You are promoting another brand or a third party."
              onChange={(v) =>
                set({
                  isPaidPartnership: v,
                  // Branded content cannot be private: clear an "Only me" choice.
                  privacyLevel: v && form.privacyLevel === "SELF_ONLY" ? "" : form.privacyLevel,
                })
              }
            />
            {commercialLabel(form) && (
              <p className="text-[11px] text-muted-foreground">{commercialLabel(form)}</p>
            )}
          </div>
        )}
      </fieldset>

      <p className="text-[11px] text-muted-foreground">
        {consentText(form).replace("TikTok's ", "TikTok’s ")}{" "}
        <a
          href="https://www.tiktok.com/legal/page/global/music-usage-confirmation/en"
          target="_blank"
          rel="noreferrer"
          className="underline"
        >
          Music Usage Confirmation
        </a>
        {form.discloseCommercial && form.isPaidPartnership && (
          <>
            {" · "}
            <a
              href="https://www.tiktok.com/legal/page/global/bc-policy/en"
              target="_blank"
              rel="noreferrer"
              className="underline"
            >
              Branded Content Policy
            </a>
          </>
        )}
        . After you publish, TikTok may take a few minutes to process the video before it appears.
      </p>
    </section>
  );
}

function Toggle({
  label,
  checked,
  disabled = false,
  note,
  onChange,
}: {
  label: string;
  checked: boolean;
  disabled?: boolean;
  note?: string;
  onChange: (value: boolean) => void;
}) {
  return (
    <label className={cn("flex items-start gap-2 text-xs", disabled && "cursor-not-allowed opacity-50")}>
      <input
        type="checkbox"
        className="mt-0.5 h-3.5 w-3.5 accent-primary"
        checked={checked && !disabled}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span>
        {label}
        {note && <span className="block text-[11px] text-muted-foreground">{note}</span>}
      </span>
    </label>
  );
}

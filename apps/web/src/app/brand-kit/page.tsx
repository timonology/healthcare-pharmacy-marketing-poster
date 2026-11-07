"use client";

import { useEffect, useRef, useState } from "react";
import type {
  BrandKit,
  UpsertBrandKitRequest,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { api } from "@/lib/api";
import { clearPendingSignup, readPendingSignup } from "@/lib/pending-signup";

const DEFAULTS: UpsertBrandKitRequest = {
  name: "My Pharmacy",
  colors: { primary: "#0f766e", secondary: "#1e293b", accent: "#f59e0b" },
  pharmacy: {
    name: "",
    licenseNumber: "",
    phone: "",
    address: "",
  },
  regulatoryFooter:
    "This information is provided for educational purposes only. Consult a licensed pharmacist before use.",
};

export default function BrandKitPage() {
  const [form, setForm] = useState<UpsertBrandKitRequest>(DEFAULTS);
  const [logoUrl, setLogoUrl] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        // Brand kit may not exist yet for new users — fall back to the
        // pharmacy profile + anything they entered on /register so the form
        // isn't empty.
        const [kit, me] = await Promise.all([
          api.getBrandKit(),
          api.getMe().catch(() => null),
        ]);
        if (cancelled) return;

        if (kit) {
          populate(kit);
        } else {
          const pending = readPendingSignup();
          const fcode = me?.profile.sonarFCode || pending?.sonarFCode || "";
          const postCode = me?.profile.postCode || pending?.postCode || "";
          const address = me?.profile.address ?? "";
          const addressLine = postCode && address && !address.toUpperCase().includes(postCode)
            ? `${address}, ${postCode}`
            : address || postCode;
          setForm((f) => ({
            name: me?.profile.pharmacyName || pending?.pharmacyName || f.name,
            colors: {
              ...f.colors,
              primary: pending?.brandColor || f.colors.primary,
            },
            pharmacy: {
              ...f.pharmacy,
              name: me?.profile.pharmacyName || pending?.pharmacyName || f.pharmacy.name,
              phone: me?.profile.contactPhone || pending?.phone || f.pharmacy.phone,
              address: addressLine || f.pharmacy.address,
              licenseNumber: fcode || f.pharmacy.licenseNumber,
            },
            regulatoryFooter: f.regulatoryFooter,
          }));
          // We consumed the pending data here — clear it so a later refresh
          // doesn't overwrite something the user has since edited.
          clearPendingSignup();
        }
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load");
      } finally {
        if (!cancelled) setLoaded(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  function populate(kit: BrandKit) {
    setForm({
      name: kit.name,
      colors: kit.colors,
      pharmacy: kit.pharmacy,
      regulatoryFooter: kit.regulatoryFooter,
    });
    setLogoUrl(kit.logoUrl);
    setSavedAt(kit.updatedAtUtc);
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setPending(true);
    setError(null);
    try {
      const kit = await api.upsertBrandKit(form);
      populate(kit);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Save failed");
    } finally {
      setPending(false);
    }
  }

  /**
   * Uploads a logo, creating the brand kit first if there isn't one yet.
   *
   * The server refuses a logo for an owner with no brand kit, because a kit
   * carries the pharmacy name and licence number that end up on printed,
   * patient-facing material — it will not invent those. That constraint is
   * right, but making the person satisfy it by hand was not: the file input
   * sat disabled behind "save the brand kit once before uploading a logo",
   * which is the page explaining its own storage model.
   *
   * Nothing was actually missing. The form is prefilled from the pharmacy
   * profile on load, so the values the kit needs are already on screen. Saving
   * them is a step the page can take by itself.
   *
   * If the form genuinely is incomplete the save fails, and the error names the
   * field — "Pharmacy name is required" — instead of the old message, which
   * named neither the problem nor the fix. (The licence number used to be the
   * example here. It is optional now: kits created by the booking-link
   * migration have none, and demanding one would lock those pharmacies out of
   * the page they were sent to.)
   */
  async function onUploadLogo(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setPending(true);
    setError(null);
    try {
      if (!savedAt) populate(await api.upsertBrandKit(form));
      const kit = await api.uploadLogo(file);
      populate(kit);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Upload failed");
    } finally {
      setPending(false);
      if (fileRef.current) fileRef.current.value = "";
    }
  }

  if (!loaded) {
    return <main className="container py-8">Loading…</main>;
  }

  return (
    <main className="container max-w-3xl py-8">
      <h1 className="text-3xl font-bold tracking-tight">Brand kit</h1>
      <p className="mt-1 text-sm text-muted-foreground">
        Logo, colors, and pharmacy details applied to every output.
      </p>

      <form onSubmit={onSubmit} className="mt-8 space-y-8">
        {/* ---- Logo ---- */}
        <section className="space-y-3 rounded-lg border p-6">
          <h2 className="text-lg font-semibold">Logo</h2>
          <div className="flex items-center gap-4">
            <div className="flex h-24 w-24 items-center justify-center rounded-md border bg-muted">
              {logoUrl ? (
                // eslint-disable-next-line @next/next/no-img-element
                <img
                  src={logoUrl}
                  alt="Logo"
                  className="max-h-full max-w-full object-contain"
                />
              ) : (
                <span className="text-xs text-muted-foreground">No logo</span>
              )}
            </div>
            <div className="space-y-2">
              <input
                ref={fileRef}
                type="file"
                accept="image/png,image/jpeg,image/svg+xml,image/webp"
                onChange={onUploadLogo}
                className="text-sm"
                disabled={pending}
              />
              <p className="text-xs text-muted-foreground">
                PNG, JPEG, SVG or WebP. Applied to posters, campaign emails and
                video overlays.
                {!savedAt && " Choosing one saves the details below at the same time."}
              </p>
            </div>
          </div>
        </section>

        {/* ---- Identity ---- */}
        <section className="space-y-4 rounded-lg border p-6">
          <h2 className="text-lg font-semibold">Identity</h2>
          <div className="space-y-2">
            <Label htmlFor="kitName">Brand kit name</Label>
            <Input
              id="kitName"
              value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              required
            />
          </div>
        </section>

        {/* ---- Colors ---- */}
        <section className="space-y-4 rounded-lg border p-6">
          <h2 className="text-lg font-semibold">Colors</h2>
          <div className="grid grid-cols-3 gap-4">
            {(["primary", "secondary", "accent"] as const).map((key) => (
              <div key={key} className="space-y-2">
                <Label htmlFor={`color-${key}`} className="capitalize">
                  {key}
                </Label>
                <div className="flex items-center gap-2">
                  <Input
                    id={`color-${key}`}
                    type="color"
                    value={form.colors[key]}
                    onChange={(e) =>
                      setForm({
                        ...form,
                        colors: { ...form.colors, [key]: e.target.value },
                      })
                    }
                    className="h-10 w-14 cursor-pointer p-1"
                  />
                  <Input
                    value={form.colors[key]}
                    onChange={(e) =>
                      setForm({
                        ...form,
                        colors: { ...form.colors, [key]: e.target.value },
                      })
                    }
                    pattern="^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$"
                  />
                </div>
              </div>
            ))}
          </div>
        </section>

        {/* ---- Pharmacy details ---- */}
        <section className="space-y-4 rounded-lg border p-6">
          <h2 className="text-lg font-semibold">Pharmacy details</h2>
          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="phName">Name</Label>
              <Input
                id="phName"
                value={form.pharmacy.name}
                onChange={(e) =>
                  setForm({
                    ...form,
                    pharmacy: { ...form.pharmacy, name: e.target.value },
                  })
                }
                required
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="phLicense">License number</Label>
              <Input
                id="phLicense"
                value={form.pharmacy.licenseNumber}
                onChange={(e) =>
                  setForm({
                    ...form,
                    pharmacy: {
                      ...form.pharmacy,
                      licenseNumber: e.target.value,
                    },
                  })
                }
              />
              {/* Not `required`. The API stopped demanding one when the booking
                  link moved here: pharmacies that had a link and no brand kit
                  had a kit created for them, and there was no honest
                  registration number to put in it. Requiring it in the browser
                  would lock exactly those pharmacies out of saving the page
                  they have just been sent to. */}
              <p className="text-xs text-muted-foreground">
                Printed in the footer of posters that carry one.
              </p>
            </div>
            <div className="space-y-2">
              <Label htmlFor="phPhone">Phone</Label>
              <Input
                id="phPhone"
                value={form.pharmacy.phone}
                onChange={(e) =>
                  setForm({
                    ...form,
                    pharmacy: { ...form.pharmacy, phone: e.target.value },
                  })
                }
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="phAddress">Address</Label>
              <Input
                id="phAddress"
                value={form.pharmacy.address}
                onChange={(e) =>
                  setForm({
                    ...form,
                    pharmacy: { ...form.pharmacy, address: e.target.value },
                  })
                }
              />
            </div>

            {/*
              The booking link lives here, not on the profile. This is what the
              booking QR encodes and what {{booking_url}} resolves to, and both
              of those are laid out from the brand kit — so this is where a
              designer looks for it.

              `?? ""` because the field is optional in the request: a controlled
              input handed undefined becomes uncontrolled, and React swaps it
              silently on the first keystroke.
            */}
            <div className="space-y-2 sm:col-span-2">
              <Label htmlFor="phBooking">Booking link</Label>
              <Input
                id="phBooking"
                type="url"
                inputMode="url"
                placeholder="https://booking.yourpharmacy.co.uk"
                value={form.pharmacy.bookingUrl ?? ""}
                onChange={(e) =>
                  setForm({
                    ...form,
                    pharmacy: { ...form.pharmacy, bookingUrl: e.target.value },
                  })
                }
              />
              <p className="text-xs text-muted-foreground">
                Where patients book an appointment. Put{" "}
                <code className="rounded bg-muted px-1 py-0.5">{"{{booking_url}}"}</code>{" "}
                on a poster and it fills in from here, and it is what the booking
                QR code points at. Change it and every poster you have already
                made points at the new one.
              </p>
            </div>
          </div>
        </section>

        {/* ---- Regulatory footer ---- */}
        <section className="space-y-4 rounded-lg border p-6">
          <h2 className="text-lg font-semibold">Regulatory footer</h2>
          <Textarea
            value={form.regulatoryFooter}
            onChange={(e) =>
              setForm({ ...form, regulatoryFooter: e.target.value })
            }
            rows={4}
            maxLength={2000}
            placeholder="Disclaimer or regulatory text appended to outputs."
          />
          <p className="text-xs text-muted-foreground">
            {form.regulatoryFooter.length} / 2000
          </p>
        </section>

        {error && (
          <p className="text-sm text-destructive" role="alert">
            {error}
          </p>
        )}

        <div className="flex items-center gap-4">
          <Button type="submit" disabled={pending}>
            {pending ? "Saving…" : "Save brand kit"}
          </Button>
          {savedAt && (
            <span className="text-xs text-muted-foreground">
              Last saved {new Date(savedAt).toLocaleString()}
            </span>
          )}
        </div>
      </form>
    </main>
  );
}

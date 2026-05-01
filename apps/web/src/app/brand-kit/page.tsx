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
        const kit = await api.getBrandKit();
        if (cancelled) return;
        if (kit) populate(kit);
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

  async function onUploadLogo(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    setPending(true);
    setError(null);
    try {
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
                disabled={pending || !savedAt}
              />
              {!savedAt && (
                <p className="text-xs text-muted-foreground">
                  Save the brand kit once before uploading a logo.
                </p>
              )}
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
                required
              />
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

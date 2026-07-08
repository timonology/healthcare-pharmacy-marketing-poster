"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { ArrowLeft, Building2, CheckCircle2, ShieldCheck } from "lucide-react";
import type { Me, UpsertProfileRequest } from "@acme/shared-types";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { Textarea } from "@/components/ui/textarea";
import { api } from "@/lib/api";

export default function ProfilePage() {
  const [me, setMe] = useState<Me | null>(null);
  const [form, setForm] = useState<UpsertProfileRequest>({
    pharmacyName: "",
    address: "",
    postCode: "",
    description: "",
    contactName: "",
    contactPhone: "",
    sonarFCode: "",
  });
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const fetched = await api.getMe();
        if (cancelled) return;
        setMe(fetched);
        setForm({
          pharmacyName: fetched.profile.pharmacyName,
          address: fetched.profile.address,
          postCode: fetched.profile.postCode,
          description: fetched.profile.description,
          contactName: fetched.profile.contactName,
          contactPhone: fetched.profile.contactPhone,
          sonarFCode: fetched.profile.sonarFCode ?? "",
        });
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : "Failed to load");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setPending(true);
    setError(null);
    try {
      const next = await api.updateProfile(form);
      setMe(next);
      setSavedAt(new Date().toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Save failed");
    } finally {
      setPending(false);
    }
  }

  if (!me) {
    return (
      <main className="container max-w-3xl py-12 text-sm text-muted-foreground">
        Loading profile…
      </main>
    );
  }

  return (
    <main className="container max-w-3xl py-10">
      <div className="mb-6">
        <Button asChild variant="ghost" size="sm">
          <Link href="/dashboard">
            <ArrowLeft className="mr-1.5 h-4 w-4" />
            Back to dashboard
          </Link>
        </Button>
      </div>

      <header className="flex items-end justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Pharmacy profile</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Used on every poster and in emails to your patients.
          </p>
        </div>
        {me.profile.sonarFCode && (
          <Badge variant="secondary" className="gap-1.5">
            <ShieldCheck className="h-3 w-3" />
            Sonar verified · {me.profile.sonarFCode}
          </Badge>
        )}
      </header>

      <form onSubmit={onSubmit} className="mt-8 space-y-6">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Building2 className="h-4 w-4 text-primary" />
              Pharmacy details
            </CardTitle>
            <CardDescription>
              Name, address, and a short description for marketing copy.
            </CardDescription>
          </CardHeader>
          <Separator />
          <CardContent className="space-y-4 pt-6">
            <div className="space-y-2">
              <Label htmlFor="pharmacyName">Pharmacy name</Label>
              <Input
                id="pharmacyName"
                value={form.pharmacyName}
                onChange={(e) => setForm({ ...form, pharmacyName: e.target.value })}
                required
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="address">Postal address</Label>
              <Textarea
                id="address"
                value={form.address}
                onChange={(e) => setForm({ ...form, address: e.target.value })}
                rows={3}
                required
              />
            </div>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <div className="space-y-2">
                <Label htmlFor="postCode">Postcode</Label>
                <Input
                  id="postCode"
                  value={form.postCode}
                  onChange={(e) => setForm({ ...form, postCode: e.target.value.toUpperCase() })}
                  placeholder="SW1A 1AA"
                  autoCapitalize="characters"
                  required
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="sonarFCode">ODS / F code</Label>
                <Input
                  id="sonarFCode"
                  value={form.sonarFCode ?? ""}
                  onChange={(e) => setForm({ ...form, sonarFCode: e.target.value.toUpperCase() })}
                  placeholder="FA123"
                  autoCapitalize="characters"
                />
              </div>
            </div>

            <div className="space-y-2">
              <Label htmlFor="description">
                Description{" "}
                <span className="text-xs text-muted-foreground">(optional)</span>
              </Label>
              <Textarea
                id="description"
                value={form.description}
                onChange={(e) => setForm({ ...form, description: e.target.value })}
                rows={4}
                maxLength={2000}
              />
              <p className="text-xs text-muted-foreground">
                {form.description.length} / 2000
              </p>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Primary contact</CardTitle>
            <CardDescription>
              Who patients hear from when we send your campaigns.
            </CardDescription>
          </CardHeader>
          <Separator />
          <CardContent className="grid grid-cols-1 gap-4 pt-6 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="contactName">Contact name</Label>
              <Input
                id="contactName"
                value={form.contactName}
                onChange={(e) => setForm({ ...form, contactName: e.target.value })}
                required
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="contactPhone">Mobile number</Label>
              <Input
                id="contactPhone"
                type="tel"
                value={form.contactPhone}
                onChange={(e) => setForm({ ...form, contactPhone: e.target.value })}
                required
              />
            </div>
          </CardContent>
        </Card>

        {error && (
          <p className="text-sm text-destructive" role="alert">
            {error}
          </p>
        )}

        <div className="flex items-center justify-between">
          {savedAt && (
            <p className="flex items-center gap-1.5 text-sm text-emerald-600 dark:text-emerald-400">
              <CheckCircle2 className="h-4 w-4" />
              Saved at {savedAt}
            </p>
          )}
          <div className="ml-auto flex gap-2">
            <Button asChild variant="outline">
              <Link href="/brand-kit">Brand kit →</Link>
            </Button>
            <Button
              type="submit"
              disabled={pending}
              className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground"
            >
              {pending ? "Saving…" : "Save changes"}
            </Button>
          </div>
        </div>
      </form>
    </main>
  );
}

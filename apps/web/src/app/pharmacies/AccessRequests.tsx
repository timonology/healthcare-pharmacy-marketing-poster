"use client";

import { useCallback, useEffect, useState } from "react";
import { Clock, KeyRound, ShieldCheck, ShieldX } from "lucide-react";
import type { AccessRequest } from "@acme/shared-types";
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
import { api } from "@/lib/api";

/**
 * Asking to work as a pharmacy you do not belong to.
 *
 * The route by which a locum, a relief pharmacist or a newly hired manager
 * gets access to a pharmacy nobody has invited them to. Marketing25 staff
 * decide, not the pharmacy, because the person asking may have no existing
 * relationship with anybody in the system.
 *
 * <b>The confirmation is deliberately vague.</b> Submitting always says the
 * same thing, whether or not that address has an account — otherwise this form
 * becomes a way to test which pharmacies are registered. The only error shown
 * is about the reason, which is the person's own input and safe to reflect.
 */
export function AccessRequests() {
  const [requests, setRequests] = useState<AccessRequest[] | null>(null);
  const [email, setEmail] = useState("");
  const [reason, setReason] = useState("");
  const [sending, setSending] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setRequests(await api.myAccessRequests().catch(() => []));
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setSending(true);
    try {
      await api.requestPharmacyAccess(email, reason);
      setSent(true);
      setEmail("");
      setReason("");
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not send that request.");
    } finally {
      setSending(false);
    }
  }

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <KeyRound className="h-4 w-4 text-muted-foreground" />
            Request access to another pharmacy
          </CardTitle>
          <CardDescription>
            For covering a shift at a pharmacy you don&rsquo;t already work as. A
            Marketing25 administrator reviews every request.
          </CardDescription>
        </CardHeader>

        <CardContent>
          {sent ? (
            <div className="space-y-3">
              <p className="text-sm">
                Request submitted. A Marketing25 administrator will review it, and it
                will appear below once decided.
              </p>
              <Button variant="outline" size="sm" onClick={() => setSent(false)}>
                Request another
              </Button>
            </div>
          ) : (
            <form onSubmit={submit} className="space-y-4">
              <div className="space-y-2">
                <Label htmlFor="pharmacyEmail">Pharmacy email address</Label>
                <Input
                  id="pharmacyEmail"
                  type="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  placeholder="manager@theirpharmacy.co.uk"
                />
                <p className="text-xs text-muted-foreground">
                  The address that pharmacy signed up with.
                </p>
              </div>

              <div className="space-y-2">
                <Label htmlFor="accessReason">Why do you need access?</Label>
                <textarea
                  id="accessReason"
                  required
                  minLength={10}
                  maxLength={500}
                  rows={3}
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  placeholder="Covering the Tuesday locum shift at this branch."
                  className="flex w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                />
                <p className="text-xs text-muted-foreground">
                  An administrator has to weigh this, so a sentence or two helps.
                </p>
              </div>

              {error && (
                <p className="text-sm text-destructive" role="alert">
                  {error}
                </p>
              )}

              <Button type="submit" disabled={sending}>
                {sending ? "Sending…" : "Send request"}
              </Button>
            </form>
          )}
        </CardContent>
      </Card>

      {requests && requests.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Your requests</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {requests.map((r) => (
              <div
                key={r.id}
                className="flex items-start justify-between gap-4 rounded-md border border-border/60 p-3"
              >
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium">{r.pharmacyName}</p>
                  <p className="truncate text-xs text-muted-foreground">{r.pharmacyEmail}</p>
                  <p className="mt-1 text-xs text-muted-foreground">{r.reason}</p>
                  {r.reviewNote && (
                    <p className="mt-1 text-xs">
                      <span className="text-muted-foreground">Reviewer: </span>
                      {r.reviewNote}
                    </p>
                  )}
                </div>
                <StatusBadge status={r.status} />
              </div>
            ))}
          </CardContent>
        </Card>
      )}
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  if (status === "Approved") {
    return (
      <Badge variant="secondary" className="shrink-0 gap-1.5">
        <ShieldCheck className="h-3 w-3" /> Approved
      </Badge>
    );
  }
  if (status === "Rejected") {
    return (
      <Badge variant="outline" className="shrink-0 gap-1.5 text-muted-foreground">
        <ShieldX className="h-3 w-3" /> Rejected
      </Badge>
    );
  }
  return (
    <Badge variant="outline" className="shrink-0 gap-1.5">
      <Clock className="h-3 w-3" /> Pending
    </Badge>
  );
}

"use client";

import { useMemo, useState } from "react";
import { CheckCircle2, Mail } from "lucide-react";
import { Button } from "@/components/ui/button";
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
import { Textarea } from "@/components/ui/textarea";
import { api } from "@/lib/api";

interface ShareEmailDialogProps {
  open: boolean;
  onOpenChange: (b: boolean) => void;
  posterId: string;
  posterName: string;
}

export function ShareEmailDialog({
  open,
  onOpenChange,
  posterId,
  posterName,
}: ShareEmailDialogProps) {
  const [recipientsText, setRecipientsText] = useState("");
  const [subject, setSubject] = useState("");
  const [message, setMessage] = useState(
    "Hi,\n\nWe wanted to share our latest pharmacy poster with you. It's attached as a PDF.\n\nKind regards,",
  );
  const [pending, setPending] = useState(false);
  const [result, setResult] = useState<{ sent: number; failed: number } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const recipients = useMemo(
    () =>
      recipientsText
        .split(/[\s,;]+/)
        .map((r) => r.trim())
        .filter(Boolean),
    [recipientsText],
  );

  async function send(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    if (recipients.length === 0) {
      setError("Add at least one recipient.");
      return;
    }
    setPending(true);
    try {
      const r = await api.sharePosterByEmail(posterId, {
        recipients,
        subject: subject.trim() || undefined,
        message: message.trim() || undefined,
      });
      setResult({ sent: r.sent, failed: r.failed });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Send failed");
    } finally {
      setPending(false);
    }
  }

  function reset(next: boolean) {
    if (!next) {
      setResult(null);
      setError(null);
    }
    onOpenChange(next);
  }

  return (
    <Dialog open={open} onOpenChange={reset}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Mail className="h-4 w-4 text-primary" />
            Share "{posterName}"
          </DialogTitle>
          <DialogDescription>
            We'll attach the poster as a PDF and email it from your pharmacy.
          </DialogDescription>
        </DialogHeader>

        {result ? (
          <div className="flex flex-col items-center gap-3 py-6 text-center">
            <CheckCircle2 className="h-10 w-10 text-emerald-500" />
            <p className="text-sm">
              Sent to <strong>{result.sent}</strong>{" "}
              recipient{result.sent === 1 ? "" : "s"}
              {result.failed > 0 && (
                <span className="text-muted-foreground">
                  {" "}
                  ({result.failed} failed)
                </span>
              )}
              .
            </p>
            <Button onClick={() => reset(false)}>Done</Button>
          </div>
        ) : (
          <form onSubmit={send} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="recipients">
                Recipient emails{" "}
                <span className="text-xs text-muted-foreground">
                  (comma- or newline-separated)
                </span>
              </Label>
              <Textarea
                id="recipients"
                value={recipientsText}
                onChange={(e) => setRecipientsText(e.target.value)}
                rows={3}
                placeholder="patient1@example.com, patient2@example.com"
                required
              />
              <p className="text-xs text-muted-foreground">
                {recipients.length} recipient{recipients.length === 1 ? "" : "s"}
              </p>
            </div>

            <div className="space-y-2">
              <Label htmlFor="subject">
                Subject <span className="text-xs text-muted-foreground">(optional)</span>
              </Label>
              <Input
                id="subject"
                value={subject}
                onChange={(e) => setSubject(e.target.value)}
                placeholder={`${posterName} from your pharmacy`}
              />
            </div>

            <div className="space-y-2">
              <Label htmlFor="message">Message</Label>
              <Textarea
                id="message"
                value={message}
                onChange={(e) => setMessage(e.target.value)}
                rows={5}
              />
            </div>

            {error && (
              <p className="text-sm text-destructive" role="alert">
                {error}
              </p>
            )}

            <DialogFooter>
              <Button type="button" variant="ghost" onClick={() => reset(false)}>
                Cancel
              </Button>
              <Button
                type="submit"
                disabled={pending || recipients.length === 0}
                className="bg-gradient-to-r from-primary to-emerald-500 text-primary-foreground"
              >
                {pending ? "Sending…" : `Send to ${recipients.length}`}
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

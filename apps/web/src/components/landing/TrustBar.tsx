import { BadgeCheck, MailCheck, Printer, ShieldCheck } from "lucide-react";

/**
 * Facts, not endorsements. This once claimed a customer count, HIPAA (a US
 * law with no bearing on a UK pharmacy) and certifications nobody had; every
 * line below is something the product does and a visitor can check.
 */
export function TrustBar() {
  return (
    <section className="border-y border-border/60 bg-muted/30">
      <div className="container mx-auto flex flex-col items-center gap-6 px-4 py-8 text-center md:flex-row md:justify-between md:px-6 md:text-left">
        <p className="text-sm font-medium text-muted-foreground">
          Built for <span className="text-foreground">UK community pharmacies</span>
        </p>
        <div className="flex flex-wrap items-center justify-center gap-x-6 gap-y-3">
          <Badge icon={<Printer className="h-3.5 w-3.5" />}>Print-ready A4</Badge>
          <Badge icon={<BadgeCheck className="h-3.5 w-3.5" />}>No design skills needed</Badge>
          <Badge icon={<MailCheck className="h-3.5 w-3.5" />}>Patient consent and unsubscribe built in</Badge>
          <Badge icon={<ShieldCheck className="h-3.5 w-3.5" />}>Hosted on Microsoft Azure, UK/EU</Badge>
        </div>
      </div>
    </section>
  );
}

function Badge({
  icon,
  children,
}: {
  icon?: React.ReactNode;
  children: React.ReactNode;
}) {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-border/60 bg-background px-3 py-1 text-xs font-medium text-muted-foreground">
      {icon}
      {children}
    </span>
  );
}

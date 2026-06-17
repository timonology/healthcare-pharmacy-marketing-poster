import { ShieldCheck, Stethoscope } from "lucide-react";

export function TrustBar() {
  return (
    <section className="border-y border-border/60 bg-muted/30">
      <div className="container mx-auto flex flex-col items-center gap-6 px-4 py-8 text-center md:flex-row md:justify-between md:px-6 md:text-left">
        <p className="text-sm font-medium text-muted-foreground">
          Trusted by{" "}
          <span className="text-foreground">500+ pharmacies</span> across the UK
        </p>

        <div className="flex flex-wrap items-center justify-center gap-x-6 gap-y-3">
          <Badge icon={<ShieldCheck className="h-3.5 w-3.5" />}>
            HIPAA-ready
          </Badge>
          <Badge icon={<Stethoscope className="h-3.5 w-3.5" />}>
            Regulatory compliant
          </Badge>
          <Badge>GDPR safe</Badge>
          <Badge>SOC 2 in progress</Badge>
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

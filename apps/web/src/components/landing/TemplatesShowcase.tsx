import Link from "next/link";
import { ArrowRight } from "lucide-react";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { Button } from "@/components/ui/button";
import { SHOWCASE_PREVIEWS } from "./house-previews.generated";

/**
 * One real house template per category, drawn from the built-in set.
 *
 * These used to be six coloured cards with a headline typed over them —
 * designs that did not exist in the gallery. Each card now is the template a
 * pharmacist would get, snapshotted by scripts/marketing/build-house-previews.py
 * and linking to its own page in the real gallery.
 */
export function TemplatesShowcase() {
  return (
    <section id="templates" className="py-20 md:py-28">
      <div className="container mx-auto px-4 md:px-6">
        <div className="flex flex-col items-start justify-between gap-4 md:flex-row md:items-end">
          <div className="max-w-2xl">
            <span className="text-xs font-semibold uppercase tracking-widest text-primary">
              Templates
            </span>
            <h2 className="mt-3 text-3xl font-bold tracking-tight md:text-4xl">
              Pharmacy designs, ready for your details
            </h2>
            <p className="mt-3 text-muted-foreground">
              Vaccinations, NHS services, health checks, seasonal advice and more.
              Every template is A4, print-ready and fully editable, and your logo,
              phone, address and booking QR code drop into place.
            </p>
          </div>
          <Button asChild variant="ghost" size="sm" className="text-primary">
            <Link href="/templates">
              Browse all templates
              <ArrowRight className="ml-1 h-4 w-4" />
            </Link>
          </Button>
        </div>

        <div className="mt-12 grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-6">
          {SHOWCASE_PREVIEWS.map((t) => (
            <Link
              key={t.id}
              href={`/templates/${t.id}`}
              className="group overflow-hidden rounded-xl border border-border/60 bg-card transition-all hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-lg focus:outline-none focus-visible:ring-2 focus-visible:ring-primary"
            >
              <div className="aspect-[1240/1754] w-full overflow-hidden bg-white">
                <CanvasPreview doc={t.canvas} className="h-full w-full" />
              </div>
              <div className="p-3">
                <p className="truncate text-xs font-medium">{t.name}</p>
                <p className="mt-0.5 text-[10px] text-muted-foreground">{t.category}</p>
              </div>
            </Link>
          ))}
        </div>
      </div>
    </section>
  );
}

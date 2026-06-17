import { TestimonialCard } from "./TestimonialCard";

const QUOTES = [
  {
    quote:
      "We replaced a £400/month design contract with this. The flu campaign poster took 12 minutes from start to print.",
    name: "Sarah Patel",
    role: "Owner, Patel Pharmacy",
    initials: "SP",
    accent: "blue" as const,
  },
  {
    quote:
      "Our regulatory footer is automatically applied to every poster. No more late-night last-minute legal checks.",
    name: "James O'Connor",
    role: "Pharmacy manager, MediCare Group",
    initials: "JO",
    accent: "cyan" as const,
  },
  {
    quote:
      "Finally something that understands what a pharmacy looks like. The templates feel professional, not generic.",
    name: "Aisha Rahman",
    role: "Independent pharmacist",
    initials: "AR",
    accent: "emerald" as const,
  },
];

export function Testimonials() {
  return (
    <section className="py-20 md:py-28">
      <div className="container mx-auto px-4 md:px-6">
        <div className="mx-auto max-w-2xl text-center">
          <span className="text-xs font-semibold uppercase tracking-widest text-primary">
            Loved by pharmacists
          </span>
          <h2 className="mt-3 text-3xl font-bold tracking-tight md:text-4xl">
            Don&apos;t take our word for it
          </h2>
        </div>

        <div className="mt-14 grid grid-cols-1 gap-5 md:grid-cols-3">
          {QUOTES.map((q) => (
            <TestimonialCard key={q.name} {...q} />
          ))}
        </div>
      </div>
    </section>
  );
}

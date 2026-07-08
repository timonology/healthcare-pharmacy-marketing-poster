const STEPS = [
  {
    n: 1,
    title: "Set up your brand kit",
    body: "Upload your logo, choose your colors, fill in your license details once. We'll apply them everywhere.",
  },
  {
    n: 2,
    title: "Pick a template",
    body: "Browse vaccination, awareness, promotion, and seasonal templates curated for pharmacies.",
  },
  {
    n: 3,
    title: "Customize with AI",
    body: "Edit text, swap images, or let the AI generate copy for you. Live auto-save keeps your work safe.",
  },
  {
    n: 4,
    title: "Print or share",
    body: "Export to high-resolution PDF or PNG. Print in-store, post on social, or send by email.",
  },
];

export function HowItWorks() {
  return (
    <section className="border-y border-border/60 bg-muted/20 py-20 md:py-28">
      <div className="container mx-auto px-4 md:px-6">
        <div className="mx-auto max-w-2xl text-center">
          <span className="text-xs font-semibold uppercase tracking-widest text-primary">
            How it works
          </span>
          <h2 className="mt-3 text-3xl font-bold tracking-tight md:text-4xl">
            From blank canvas to printed poster in four steps
          </h2>
        </div>

        <ol className="mx-auto mt-14 grid max-w-5xl grid-cols-1 gap-8 md:grid-cols-2 md:gap-10 lg:grid-cols-4">
          {STEPS.map((step, idx) => (
            <li key={step.n} className="relative">
              <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-gradient-to-br from-primary to-emerald-500 text-sm font-bold text-primary-foreground shadow-md">
                {step.n}
              </div>
              {idx < STEPS.length - 1 && (
                <span
                  aria-hidden
                  className="absolute left-12 top-5 hidden h-px w-[calc(100%-3rem)] bg-gradient-to-r from-border to-transparent lg:block"
                />
              )}
              <h3 className="mt-4 text-base font-semibold tracking-tight">
                {step.title}
              </h3>
              <p className="mt-2 text-sm text-muted-foreground">{step.body}</p>
            </li>
          ))}
        </ol>
      </div>
    </section>
  );
}

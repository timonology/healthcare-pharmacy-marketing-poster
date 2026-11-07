const STEPS = [
  {
    n: 1,
    title: "Set up your brand kit",
    body: "Add your logo, pharmacy name, phone, address and booking link once. Every poster picks them up.",
  },
  {
    n: 2,
    title: "Create",
    body: "Pick a template made for UK pharmacies, describe a poster for AI to design, or turn a topic into a short video.",
  },
  {
    n: 3,
    title: "Make it yours",
    body: "Change the words, swap the photo, move things around. Your work saves as you go.",
  },
  {
    n: 4,
    title: "Print, send and share",
    body: "Print a PDF, email your patients, post to Facebook, Instagram, LinkedIn, X, YouTube and TikTok, or play it on the screen in your pharmacy.",
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
            From idea to your patients in four steps
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

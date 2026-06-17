import {
  Circle,
  ImageIcon,
  MousePointer2,
  Square,
  Type,
} from "lucide-react";

const TOOLS = [
  { icon: MousePointer2, active: false },
  { icon: Square, active: false },
  { icon: Circle, active: false },
  { icon: Type, active: true },
  { icon: ImageIcon, active: false },
];

export function PosterMockup() {
  return (
    <div className="relative">
      <div
        aria-hidden
        className="absolute -inset-8 -z-10 rounded-[2rem] bg-gradient-to-br from-primary/20 via-cyan-400/10 to-emerald-400/15 blur-3xl"
      />

      <div className="overflow-hidden rounded-2xl border border-border/60 bg-card shadow-2xl ring-1 ring-black/5 dark:ring-white/10">
        {/* Window chrome */}
        <div className="flex items-center gap-1.5 border-b border-border/60 bg-muted/40 px-3 py-2.5">
          <span className="h-2.5 w-2.5 rounded-full bg-rose-400/80" />
          <span className="h-2.5 w-2.5 rounded-full bg-amber-400/80" />
          <span className="h-2.5 w-2.5 rounded-full bg-emerald-400/80" />
          <div className="ml-3 hidden flex-1 rounded-md bg-background/60 px-2 py-1 text-xs text-muted-foreground sm:block">
            pharmacyposter.app/posters/flu-2026
          </div>
        </div>

        <div className="flex">
          {/* Tool rail */}
          <aside className="flex w-12 flex-col items-center gap-1 border-r border-border/60 bg-muted/30 py-3">
            {TOOLS.map((tool, i) => {
              const Icon = tool.icon;
              return (
                <span
                  key={i}
                  className={
                    tool.active
                      ? "flex h-9 w-9 items-center justify-center rounded-md bg-primary text-primary-foreground shadow-sm"
                      : "flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground"
                  }
                >
                  <Icon className="h-4 w-4" />
                </span>
              );
            })}
          </aside>

          {/* Canvas area */}
          <div className="flex-1 bg-gradient-to-br from-slate-100 to-slate-200 p-6 dark:from-slate-800 dark:to-slate-900">
            <div className="mx-auto aspect-[3/4] w-full max-w-[280px] overflow-hidden rounded-md bg-cyan-50 shadow-xl">
              <div className="h-2 w-full bg-cyan-700" />
              <div className="space-y-3 p-5">
                <p className="text-[8px] font-semibold uppercase tracking-widest text-cyan-700">
                  Seasonal · Limited time
                </p>
                <h3 className="text-2xl font-bold leading-tight text-cyan-900">
                  Flu shots
                  <br />
                  available now
                </h3>
                <p className="text-[10px] leading-snug text-cyan-800/80">
                  Walk-ins welcome. Most insurance accepted. Quick 15-minute appointment.
                </p>
                <div className="rounded-md bg-cyan-700 px-3 py-2 text-center text-[11px] font-semibold text-white">
                  Visit our pharmacy today
                </div>
                <div className="grid grid-cols-3 gap-1.5">
                  <div className="aspect-square rounded bg-cyan-200" />
                  <div className="aspect-square rounded bg-cyan-300" />
                  <div className="aspect-square rounded bg-cyan-100" />
                </div>
                <p className="text-center text-[7px] text-cyan-800/60">
                  acmepharmacy.com · 555-0102
                </p>
              </div>
            </div>
          </div>

          {/* Properties rail */}
          <aside className="hidden w-44 border-l border-border/60 bg-card p-3 lg:block">
            <p className="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
              Properties
            </p>
            <div className="mt-3 space-y-2">
              <div className="space-y-1">
                <p className="text-[10px] text-muted-foreground">Text</p>
                <div className="h-7 rounded border border-border bg-background" />
              </div>
              <div className="grid grid-cols-2 gap-1.5">
                <div className="space-y-1">
                  <p className="text-[10px] text-muted-foreground">Size</p>
                  <div className="h-7 rounded border border-border bg-background" />
                </div>
                <div className="space-y-1">
                  <p className="text-[10px] text-muted-foreground">Color</p>
                  <div className="flex h-7 items-center gap-1.5 rounded border border-border bg-background px-1.5">
                    <span className="h-4 w-4 rounded bg-cyan-700" />
                    <span className="text-[10px] text-muted-foreground">#0e7490</span>
                  </div>
                </div>
              </div>
              <div className="space-y-1">
                <p className="text-[10px] text-muted-foreground">Opacity</p>
                <div className="h-1.5 rounded-full bg-secondary">
                  <div className="h-1.5 w-4/5 rounded-full bg-primary" />
                </div>
              </div>
            </div>
          </aside>
        </div>
      </div>

      {/* Floating "Saved" badge */}
      <div className="absolute -bottom-3 left-6 flex items-center gap-2 rounded-full border border-border/60 bg-card px-3 py-1.5 text-xs shadow-lg">
        <span className="relative flex h-2 w-2">
          <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-500 opacity-75" />
          <span className="relative inline-flex h-2 w-2 rounded-full bg-emerald-500" />
        </span>
        <span className="text-muted-foreground">Auto-saved just now</span>
      </div>
    </div>
  );
}

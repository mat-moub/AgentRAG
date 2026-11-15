/** The circular progress indicator shared by every long-running task: document indexing, wiki runs. */
export function ProgressRing({ percent, label }: { percent: number; label: string }) {
  return <div className="relative size-28 shrink-0">
    <progress className="sr-only" aria-label={label} max={100} value={percent} />
    <svg viewBox="0 0 112 112" className="size-full -rotate-90" aria-hidden="true">
      <circle cx="56" cy="56" r="50" fill="none" stroke="var(--color-line)" strokeWidth="4" />
      <circle cx="56" cy="56" r="50" fill="none" stroke="var(--color-accent)" strokeWidth="5" pathLength="100" strokeDasharray="100" strokeDashoffset={100 - percent} className="transition-[stroke-dashoffset] duration-[450ms] ease-out" />
    </svg>
    <span className="absolute inset-0 flex items-center justify-center text-2xl tabular-nums">{percent}<span className="ml-1 text-base">%</span></span>
  </div>
}

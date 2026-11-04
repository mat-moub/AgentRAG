import { ArrowRightIcon, CheckCircleIcon, CheckIcon, FileTextIcon, StackIcon } from '@phosphor-icons/react'
import type { IngestionProgress as Progress } from '../../api'
import { estimateIngestion, formatDuration } from './ingestionTiming'
import type { IngestionTiming } from './ingestionTiming'

export function IngestionProgress({ title, progress, timing, now, startedAt, advance }: {
  title: string; progress: Progress | null; timing: IngestionTiming; now: number; startedAt: number
  advance: { count: number; added: number }
}) {
  const completed = progress?.completedChunks ?? 0
  const total = progress?.totalChunks ?? 0
  const saving = progress?.stage === 'saving'
  const limited = progress?.stage === 'rateLimited'
  const retryAt = progress?.retryAt ? Date.parse(progress.retryAt) : 0
  const wait = Number.isFinite(retryAt) ? Math.max(0, Math.ceil((retryAt - now) / 1000)) : 0
  const percent = saving ? 95 : total ? Math.min(90, Math.round(5 + 85 * completed / total)) : 0
  const estimate = estimateIngestion(timing, progress, now)
  const status = limited
    ? wait > 0 ? `The embedding service is rate-limited. Retrying in ${wait}s (retry ${progress?.retryAttempt}).` : 'Retrying the embedding service…'
    : saving ? 'Saving your document…' : progress?.stage === 'embedding' ? 'Indexing in progress'
      : progress?.stage === 'chunking' ? 'Splitting your document into passages…' : 'Sending your document…'

  return <section className="space-y-7 motion-safe:animate-enter" aria-label="Preparing your document">
    <div><p className="eyebrow">Preparing your document</p><h3 className="mt-3 font-display text-3xl leading-tight tracking-tight sm:text-4xl">Soon in your library.</h3>
      <p className="mt-4 flex min-w-0 items-center gap-2 text-sm"><FileTextIcon size={20} className="shrink-0" aria-hidden /><span className="truncate" title={title}>{title}</span></p>
    </div>
    <div className="flex flex-col items-center gap-5 min-[400px]:flex-row min-[400px]:items-center">
      <div className="relative size-28 shrink-0">
        <progress className="sr-only" aria-label="Document indexing progress" max={100} value={percent} />
        <svg viewBox="0 0 112 112" className="size-full -rotate-90" aria-hidden="true">
          <circle cx="56" cy="56" r="50" fill="none" stroke="var(--color-line)" strokeWidth="4" />
          <circle cx="56" cy="56" r="50" fill="none" stroke="var(--color-accent)" strokeWidth="5" pathLength="100" strokeDasharray="100" strokeDashoffset={100 - percent} className="transition-[stroke-dashoffset] duration-[450ms] ease-out" />
        </svg>
        <span className="absolute inset-0 flex items-center justify-center text-2xl tabular-nums">{percent}<span className="ml-1 text-base">%</span></span>
      </div>
      <div className="min-w-0 flex-1 space-y-2 self-stretch min-[400px]:self-auto">
        <p role="status" className="text-sm font-semibold leading-6">{status}</p>
        <p className="flex flex-wrap items-center gap-x-2 text-sm text-muted tabular-nums">
          <span>{total ? `${completed} / ${total} passages prepared` : 'Preparing…'}</span>
          {advance.added > 0 && <span key={advance.count} aria-hidden="true" className="ingestion-added inline-flex items-center gap-1 text-xs font-medium text-accent"><CheckIcon size={13} />+{advance.added}</span>}
        </p>
        <p className="text-sm text-muted tabular-nums">{saving ? 'Finalizing…' : estimate ? `About ${formatDuration(estimate.seconds)} remaining` : 'Estimating time remaining…'}</p>
        <p className="text-xs text-muted tabular-nums">{Math.max(0, Math.floor((now - startedAt) / 1000))}s elapsed{estimate && !limited ? ` · ${estimate.rate.toFixed(1)} passages/s` : ''}</p>
      </div>
    </div>
    <ol aria-label="Indexing steps" className="flex flex-wrap items-center justify-between gap-x-2 gap-y-3 border-t border-line pt-5 text-xs sm:text-sm">
      <li className={`flex items-center gap-1.5 ${total ? 'text-accent' : 'text-muted'}`}><CheckCircleIcon size={19} weight={total ? 'fill' : 'regular'} aria-hidden />Split</li>
      <li aria-hidden="true" className="text-muted"><ArrowRightIcon size={16} /></li>
      <li aria-current={total && !saving ? 'step' : undefined} className={`flex items-center gap-1.5 ${total && !saving ? 'font-semibold' : 'text-muted'}`}><StackIcon size={19} aria-hidden />Indexing</li>
      <li aria-hidden="true" className="text-muted"><ArrowRightIcon size={16} /></li>
      <li aria-current={saving ? 'step' : undefined} className={`flex items-center gap-1.5 ${saving ? 'font-semibold' : 'text-muted'}`}><FileTextIcon size={19} aria-hidden />Saved</li>
    </ol>
    <p className="text-center text-xs leading-5 text-muted">{limited ? 'Completed passages are kept while we wait. Keep this window open.' : 'Keep this window open while your document is prepared.'}</p>
  </section>
}

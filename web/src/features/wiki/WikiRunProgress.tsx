import { useEffect, useState } from 'react'
import { ArrowRightIcon, CheckCircleIcon, CircleIcon } from '@phosphor-icons/react'
import type { WikiRun, WikiRunMode } from '../../api'
import { ProgressRing } from '../../components/ProgressRing'
import { formatDuration } from '../documents/ingestionTiming'
import { estimateWikiStage, secondsSince, serverOffset, wikiRunPercent, wikiRunStages, wikiStageCount, wikiStageLabels } from './wikiProgress'

const modeLabels: Record<WikiRunMode, string> = { incremental: 'Refresh', full: 'Rebuild', lint: 'Link check' }

/**
 * Progress of an active wiki run: where it is, how far its current stage has got, the time that stage still needs
 * at its pace so far, and the stages left. Counts and estimates come from the server and tick every second here.
 */
export function WikiRunProgress({ run }: { run: WikiRun }) {
  // Ticks every second on the server's clock: each version of the run gives the offset of the browser's clock.
  const [clock, setClock] = useState<number | null>(null)
  useEffect(() => {
    const offset = serverOffset(run, Date.now())
    const tick = () => setClock(Date.now() + offset)
    tick()
    const timer = window.setInterval(tick, 1000)
    return () => window.clearInterval(timer)
  }, [run])

  const server = clock ?? (run.now ? Date.parse(run.now) : 0)
  const queued = run.stage === 'queued'
  const steps = wikiRunStages(run.mode)
  const current = steps.indexOf(run.stage)
  const count = wikiStageCount(run)
  const estimate = estimateWikiStage(run, server)
  // Time already spent counts whole seconds, like a clock: rounding up would show 41s a millisecond after 40s.
  const whole = (seconds: number | null) => seconds === null ? null : Math.floor(seconds)
  const elapsed = whole(secondsSince(run.startedUtc, server))
  const inStage = whole(secondsSince(run.stageStartedUtc, server))
  const timing = queued ? 'Runs start one at a time.'
    : estimate !== null ? `About ${formatDuration(estimate)} left for this step`
      : count ? 'Estimating the time left for this step…'
        : inStage !== null ? `${formatDuration(inStage)} in this step` : null

  return <section className="w-full space-y-5" aria-label="Wiki run progress">
    <div className="flex flex-col items-center gap-5 min-[400px]:flex-row">
      <ProgressRing percent={wikiRunPercent(run)} label="Wiki run progress" />
      <div className="min-w-0 flex-1 space-y-1.5 self-stretch text-sm leading-6 min-[400px]:self-auto">
        <p role="status" className="font-semibold">
          {modeLabels[run.mode]}: {queued ? 'Waiting for the worker' : wikiStageLabels[run.stage] ?? run.stage}
        </p>
        {count && <p className="tabular-nums text-muted">{count}</p>}
        {timing && <p className="tabular-nums text-muted">{timing}</p>}
        {!queued && <p className="text-xs tabular-nums text-muted">
          {elapsed !== null && `${formatDuration(elapsed)} elapsed`}
          {current >= 0 && `${elapsed !== null ? ' · ' : ''}step ${current + 1} of ${steps.length}`}
        </p>}
      </div>
    </div>
    {!queued && <ol aria-label="Wiki run steps" className="flex flex-wrap items-center gap-x-2 gap-y-2 border-t border-line pt-4 text-xs sm:text-sm">
      {steps.map((step, index) => {
        const done = current > index
        const active = current === index
        return <li key={step} className="flex items-center gap-2">
          {index > 0 && <ArrowRightIcon size={14} className="text-muted" aria-hidden />}
          <span aria-current={active ? 'step' : undefined}
            className={`flex items-center gap-1.5 ${active ? 'font-semibold' : done ? 'text-accent' : 'text-muted'}`}>
            {done ? <CheckCircleIcon size={17} weight="fill" aria-hidden /> : <CircleIcon size={17} weight={active ? 'bold' : 'regular'} aria-hidden />}
            {wikiStageLabels[step]}
            {done && <span className="sr-only"> (done)</span>}
          </span>
        </li>
      })}
    </ol>}
    <p className="text-xs leading-5 text-muted">The run continues on the server: you can leave this page and come back.</p>
  </section>
}

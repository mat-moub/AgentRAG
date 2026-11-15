import type { WikiRun } from '../../api'

// Share of the whole run taken by each stage, so the percentage moves forward across stages instead of restarting.
const stages: Record<string, [start: number, end: number]> = {
  queued: [0, 0], listing: [0, 5], extracting: [5, 40], resolving: [40, 45],
  writing: [45, 80], linking: [80, 95], indexing: [95, 99], completed: [100, 100],
}

/** Overall progress of a run, from 0 to 100: the stage's share plus how far its items have got. */
export function wikiRunPercent(run: Pick<WikiRun, 'stage' | 'completed' | 'total'>) {
  const [start, end] = stages[run.stage] ?? [0, 0]
  const within = run.total > 0 ? Math.min(1, run.completed / run.total) : 0
  return Math.round(start + (end - start) * within)
}

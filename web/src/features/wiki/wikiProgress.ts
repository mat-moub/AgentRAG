import type { WikiRun, WikiRunMode } from '../../api'

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

export const wikiStageLabels: Record<string, string> = {
  listing: 'Listing sources', extracting: 'Reading sources', resolving: 'Resolving pages',
  writing: 'Writing pages', linking: 'Linking pages and code', indexing: 'Indexing pages',
}

/** The stages a run goes through, in order: a link check only indexes. */
export const wikiRunStages = (mode: WikiRunMode) =>
  mode === 'lint' ? ['listing', 'indexing'] : ['listing', 'extracting', 'resolving', 'writing', 'linking', 'indexing']

const units: Record<string, [singular: string, plural: string]> = {
  extracting: ['source read', 'sources read'], writing: ['page written', 'pages written'],
  linking: ['page searched in the code', 'pages searched in the code'],
}

/** What the count of the current stage means, such as "3 of 14 sources read"; null for stages without a count. */
export function wikiStageCount(run: Pick<WikiRun, 'stage' | 'completed' | 'total'>) {
  if (run.total <= 0) return null
  const [singular, plural] = units[run.stage] ?? ['done', 'done']
  return `${run.completed} of ${run.total} ${run.total === 1 ? singular : plural}`
}

/**
 * Milliseconds to add to the browser's clock to get the server's, from the server time sent with the run and the
 * browser time it arrived at, so a browser clock that is minutes off does not distort elapsed times.
 */
export const serverOffset = (run: Pick<WikiRun, 'now'>, receivedAt: number) => {
  const sent = run.now ? Date.parse(run.now) : Number.NaN
  return Number.isFinite(sent) ? sent - receivedAt : 0
}

/** Seconds since a server timestamp, or null when it is missing. */
export function secondsSince(utc: string | null | undefined, serverTime: number) {
  const start = utc ? Date.parse(utc) : Number.NaN
  return Number.isFinite(start) ? Math.max(0, (serverTime - start) / 1000) : null
}

/**
 * Seconds the current stage still needs at the pace it has kept since it started, counting only the items done in
 * this stage (sources extracted before a restart do not make it look faster). Null until one item is done.
 */
export function estimateWikiStage(run: Pick<WikiRun, 'completed' | 'total' | 'stageStartedUtc' | 'stageStartCompleted'>, serverTime: number) {
  const done = run.completed - (run.stageStartCompleted ?? 0)
  const elapsed = secondsSince(run.stageStartedUtc, serverTime)
  if (done <= 0 || run.total <= run.completed || elapsed === null || elapsed <= 0) return null
  return Math.ceil(elapsed / done * (run.total - run.completed))
}

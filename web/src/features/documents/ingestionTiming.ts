import type { IngestionProgress } from '../../api'

export interface IngestionTiming {
  startedAt: number | null
  waits: { start: number; end: number }[]
}

export const createIngestionTiming = (): IngestionTiming => ({ startedAt: null, waits: [] })

export function recordIngestionProgress(timing: IngestionTiming, progress: IngestionProgress, now: number) {
  if (progress.stage === 'embedding' && timing.startedAt === null) timing.startedAt = now
  if (progress.stage === 'rateLimited' && progress.retryAt) {
    const end = Date.parse(progress.retryAt)
    if (!Number.isFinite(end) || end <= now) return
    const previous = timing.waits.at(-1)
    if (previous && previous.end >= now) previous.end = Math.max(previous.end, end)
    else timing.waits.push({ start: now, end })
  }
}

export function estimateIngestion(timing: IngestionTiming, progress: IngestionProgress | null, now: number) {
  if (timing.startedAt === null || !progress || progress.completedChunks <= 0 || progress.totalChunks <= progress.completedChunks) return null
  const paused = timing.waits.reduce((sum, wait) => sum + Math.max(0, Math.min(now, wait.end) - Math.max(timing.startedAt!, wait.start)), 0)
  const activeSeconds = (now - timing.startedAt - paused) / 1000
  if (activeSeconds <= 0) return null
  const rate = progress.completedChunks / activeSeconds
  const retryAt = progress.retryAt ? Date.parse(progress.retryAt) : 0
  const wait = Number.isFinite(retryAt) ? Math.max(0, (retryAt - now) / 1000) : 0
  return { rate, seconds: Math.ceil((progress.totalChunks - progress.completedChunks) / rate + wait) }
}

export function formatDuration(seconds: number) {
  if (seconds < 60) return `${Math.max(1, Math.ceil(seconds))}s`
  const minutes = Math.ceil(seconds / 60)
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)}h ${minutes % 60} min`
}

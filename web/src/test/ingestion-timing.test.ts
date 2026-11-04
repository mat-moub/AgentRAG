import { describe, expect, it } from 'vitest'
import { createIngestionTiming, estimateIngestion, formatDuration, recordIngestionProgress } from '../features/documents/ingestionTiming'

describe('Indexing time estimates', () => {
  it('waits for measured progress and derives remaining time from actual throughput', () => {
    const timing = createIngestionTiming()
    const initial = { stage: 'embedding' as const, completedChunks: 0, totalChunks: 32 }
    recordIngestionProgress(timing, initial, 1000)
    expect(estimateIngestion(timing, initial, 2000)).toBeNull()
    const halfway = { ...initial, completedChunks: 16 }
    recordIngestionProgress(timing, halfway, 9000)
    expect(estimateIngestion(timing, halfway, 9000)).toEqual({ rate: 2, seconds: 8 })
    expect(estimateIngestion(timing, halfway, 17000)).toEqual({ rate: 1, seconds: 16 })
    expect(estimateIngestion(timing, { ...halfway, completedChunks: 32 }, 18000)).toBeNull()
  })

  it('excludes quota pauses from throughput and includes the outstanding wait in the estimate', () => {
    const timing = createIngestionTiming()
    recordIngestionProgress(timing, { stage: 'embedding', completedChunks: 0, totalChunks: 32 }, 1000)
    const waiting = { stage: 'rateLimited' as const, completedChunks: 16, totalChunks: 32, retryAt: new Date(19000).toISOString() }
    recordIngestionProgress(timing, waiting, 9000)
    expect(estimateIngestion(timing, waiting, 14000)).toEqual({ rate: 2, seconds: 13 })
    recordIngestionProgress(timing, waiting, 15000)
    expect(estimateIngestion(timing, waiting, 19000)).toEqual({ rate: 2, seconds: 8 })
    expect(estimateIngestion(timing, { ...waiting, stage: 'embedding', retryAt: null }, 23000)).toEqual({ rate: 16 / 12, seconds: 12 })
  })

  it('handles zero elapsed time, invalid retry dates, and a fresh ingestion', () => {
    const timing = createIngestionTiming()
    const progress = { stage: 'embedding' as const, completedChunks: 1, totalChunks: 3 }
    recordIngestionProgress(timing, progress, 0)
    expect(estimateIngestion(timing, progress, 0)).toBeNull()
    recordIngestionProgress(timing, { ...progress, stage: 'rateLimited', retryAt: 'invalid' }, 0)
    expect(estimateIngestion(timing, progress, 1000)).toEqual({ rate: 1, seconds: 2 })
    expect(estimateIngestion(createIngestionTiming(), progress, 1000)).toBeNull()
    expect(formatDuration(59)).toBe('59s')
    expect(formatDuration(61)).toBe('2 min')
  })
})

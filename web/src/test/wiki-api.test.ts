import { describe, expect, it, vi } from 'vitest'
import { watchWikiRun } from '../api'
import type { WikiRun, WikiRunEvent } from '../api'

const headers = { 'Content-Type': 'application/x-ndjson' }
const run = (patch: Partial<WikiRun>): WikiRun => ({
  id: 7, mode: 'incremental', status: 'running', stage: 'queued', completed: 0, total: 0,
  createdUtc: '2026-11-15T10:00:00Z', startedUtc: null, finishedUtc: null, error: null, stats: null, ...patch,
})
const lines = (...events: unknown[]) => events.map(event => JSON.stringify(event) + '\n').join('')

describe('watchWikiRun', () => {
  it('delivers events in order, skips heartbeats and resolves on completion', async () => {
    const events = [
      { type: 'progress', run: run({ stage: 'listing' }) },
      { type: 'heartbeat' },
      { type: 'progress', run: run({ stage: 'extracting', completed: 1, total: 4 }) },
      { type: 'completed', run: run({ status: 'completed', stage: 'completed', completed: 4, total: 4 }) },
      { type: 'progress', run: run({ stage: 'after the end' }) },
    ]
    const fetch = vi.fn().mockResolvedValue(new Response(lines(...events), { headers }))
    vi.stubGlobal('fetch', fetch)
    const received: WikiRunEvent[] = []
    const abort = new AbortController()
    const result = await watchWikiRun(3, 7, event => received.push(event), abort.signal)
    const [url, options] = fetch.mock.calls[0]
    expect(url).toContain('/api/wikis/3/runs/7/events')
    expect(options.method).toBe('GET')
    expect(options.signal).toBe(abort.signal)
    expect(received.map(event => event.type)).toEqual(['progress', 'progress', 'completed'])
    expect(result?.status).toBe('completed')
  })

  it('returns the last known run when the stream ends early', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(lines({ type: 'progress', run: run({ stage: 'writing' }) }), { headers })))
    expect((await watchWikiRun(3, 7))?.stage).toBe('writing')
  })
})

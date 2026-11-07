import { describe, expect, it, vi } from 'vitest'
import { addDoc } from '../api'

const document = { id: 1, title: 'Résumé', chunks: 1, createdUtc: '2026-11-04T12:00:00Z' }
const headers = { 'Content-Type': 'application/x-ndjson' }

describe('Document progress stream', () => {
  it('uploads files as multipart data and consumes the same progress stream', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ type: 'completed', document }) + '\n', { headers }))
    vi.stubGlobal('fetch', fetch)
    const file = new File(['PDF contents'], 'guide.pdf', { type: 'application/pdf' })
    const abort = new AbortController()
    expect(await addDoc('Guide', file, undefined, abort.signal)).toEqual(document)
    const [url, options] = fetch.mock.calls[0]
    expect(url).toContain('/api/documents/upload')
    expect(options.body).toBeInstanceOf(FormData)
    expect(options.body.get('title')).toBe('Guide')
    expect(options.body.get('file')).toBe(file)
    expect(options.headers).not.toHaveProperty('Content-Type')
    expect(options.signal).toBe(abort.signal)
  })

  it('decodes fragmented UTF-8 events, ignores heartbeats and accepts a final line without a newline', async () => {
    const bytes = new TextEncoder().encode(
      JSON.stringify({ type: 'heartbeat' }) + '\n' +
      JSON.stringify({ type: 'progress', progress: { stage: 'embedding', completedChunks: 0, totalChunks: 1 } }) + '\n' +
      JSON.stringify({ type: 'completed', document }),
    )
    const stream = new ReadableStream<Uint8Array>({ start(controller) {
      for (const byte of bytes) controller.enqueue(new Uint8Array([byte]))
      controller.close()
    } })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(stream, { headers })))
    const progress = vi.fn()
    expect(await addDoc('Résumé', 'Content', progress)).toEqual(document)
    expect(progress).toHaveBeenCalledExactlyOnceWith({ stage: 'embedding', completedChunks: 0, totalChunks: 1 })
  })

  it('does not report success when a connection ends before the terminal event', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"type":"heartbeat"}\n', { headers })))
    await expect(addDoc('Title', 'Content')).rejects.toThrow('Refresh your documents before trying again')
  })

  it('keeps reading heartbeats for more than twenty minutes without a client deadline', async () => {
    vi.useFakeTimers()
    try {
      let controller!: ReadableStreamDefaultController<Uint8Array>
      const stream = new ReadableStream<Uint8Array>({ start(value) { controller = value } })
      const fetch = vi.fn().mockResolvedValue(new Response(stream, { headers }))
      vi.stubGlobal('fetch', fetch)
      const encoder = new TextEncoder()
      const abort = new AbortController()
      let settled = false
      const ingestion = addDoc('Title', 'Content', undefined, abort.signal).finally(() => { settled = true })
      for (let tick = 0; tick < 132; tick++) {
        await vi.advanceTimersByTimeAsync(10_000)
        controller.enqueue(encoder.encode('{"type":"heartbeat"}\n'))
      }
      expect(settled).toBe(false)
      expect(abort.signal.aborted).toBe(false)
      expect(fetch).toHaveBeenCalledOnce()
      controller.enqueue(encoder.encode(JSON.stringify({ type: 'completed', document }) + '\n'))
      expect(await ingestion).toEqual(document)
    } finally { vi.useRealTimers() }
  })

  it('reports validation errors before trying to parse a progress stream', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'Title and content are required.' }), { status: 400 })))
    await expect(addDoc('', '')).rejects.toThrow('Title and content are required.')
  })
})

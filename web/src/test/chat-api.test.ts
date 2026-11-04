import { describe, expect, it, vi } from 'vitest'
import { chat } from '../api'

const headers = { 'Content-Type': 'application/x-ndjson' }

describe('Chat response stream', () => {
  it('decodes fragmented UTF-8 deltas, ignores metadata, and confirms completion', async () => {
    const bytes = new TextEncoder().encode([
      { type: 'started' }, { type: 'heartbeat' }, { type: 'delta', text: 'Résumé ' },
      { type: 'delta', text: '📚 [Guide]' }, { type: 'completed' },
    ].map(event => JSON.stringify(event)).join('\n'))
    const body = new ReadableStream<Uint8Array>({ start(controller) {
      for (const byte of bytes) controller.enqueue(new Uint8Array([byte]))
      controller.close()
    } })
    const fetch = vi.fn().mockResolvedValue(new Response(body, { headers }))
    vi.stubGlobal('fetch', fetch)
    const delta = vi.fn()
    await chat('Question', delta)
    expect(delta.mock.calls.map(call => call[0]).join('')).toBe('Résumé 📚 [Guide]')
    expect(fetch.mock.calls[0][0]).toBe('/api/chat/stream')
  })

  it('surfaces a terminal error after delivering partial text', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(
      '{"type":"delta","text":"Partial"}\n{"type":"error","error":"Please try again."}\n', { headers },
    )))
    const delta = vi.fn()
    await expect(chat('Question', delta)).rejects.toThrow('Please try again.')
    expect(delta).toHaveBeenCalledWith('Partial')
  })

  it('sends previous messages separately from the current question', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response('{"type":"completed"}\n', { headers }))
    vi.stubGlobal('fetch', fetch)
    const history = [{ role: 'user' as const, content: "Je m'appelle Mathieu." }, { role: 'assistant' as const, content: 'Enchanté, Mathieu !' }]
    await chat("Comment je m'appelle ?", vi.fn(), undefined, history)
    expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual({ message: "Comment je m'appelle ?", history })
  })

  it('passes cancellation to fetch and handles HTTP validation errors', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'Message is required.' }), { status: 400 }))
    vi.stubGlobal('fetch', fetch)
    const cancellation = new AbortController()
    await expect(chat('', vi.fn(), cancellation.signal)).rejects.toThrow('Message is required.')
    expect(fetch.mock.calls[0][1].signal).toBe(cancellation.signal)
  })
})

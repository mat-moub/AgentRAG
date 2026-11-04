import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Login } from '../features/auth/Login'
import { Chat } from '../features/chat/Chat'
import { Workspace } from '../features/workspace/Workspace'

const document = { id: 1, title: 'Product guide', chunks: 3, createdUtc: '2026-11-02T10:00:00Z' }
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })
const indexed = () => new Response(JSON.stringify({ type: 'completed', document }) + '\n', { headers: { 'Content-Type': 'application/x-ndjson' } })
const answer = (text: string) => new Response(JSON.stringify({ type: 'delta', text }) + '\n' + JSON.stringify({ type: 'completed' }) + '\n', { headers: { 'Content-Type': 'application/x-ndjson' } })

describe('Research workflows', () => {
  it('creates an account with an empty response, signs in and stores the bearer token', async () => {
    const user = userEvent.setup()
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 200 }))
      .mockResolvedValueOnce(json({ accessToken: 'test-token' }))
    vi.stubGlobal('fetch', fetch)
    const done = vi.fn()
    render(<Login onDone={done} />)
    await user.click(screen.getByRole('button', { name: 'Create an account' }))
    await user.type(screen.getByLabelText('Email address'), 'reader@example.com')
    await user.type(screen.getByLabelText('Password'), 'Research1!')
    await user.click(screen.getByRole('button', { name: 'Create account' }))
    await waitFor(() => expect(done).toHaveBeenCalledOnce())
    expect(fetch.mock.calls.map(call => call[0])).toEqual(['/api/auth/register', '/api/auth/login'])
    expect(localStorage.getItem('token')).toBe('test-token')
  })

  it('keeps the login form usable after server validation errors', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json({ errors: { Password: ['Password is too short.'] } }, 400)))
    render(<Login onDone={vi.fn()} />)
    await user.type(screen.getByLabelText('Email address'), 'reader@example.com')
    await user.type(screen.getByLabelText('Password'), 'short')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Password is too short.')
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled()
  })

  it('retries a failed chat without duplicating the user question and renders cited Markdown', async () => {
    const user = userEvent.setup()
    const fetch = vi.fn()
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(answer('**Start here.** See [Product guide].'))
    vi.stubGlobal('fetch', fetch)
    render(<Chat documentCount={1} documentsReady onAdd={vi.fn()} />)
    await user.type(screen.getByRole('textbox', { name: 'Ask a question about your documents' }), 'Where do I start?')
    await user.click(screen.getByRole('button', { name: 'Send question' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to reach the server')
    await user.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByLabelText('AgentRAG answer')).toHaveTextContent('Start here. See [Product guide].')
    expect(screen.getAllByLabelText('Your question')).toHaveLength(1)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('renders partial chat text immediately, then combines Markdown fragments into one answer', async () => {
    const user = userEvent.setup()
    let stream!: ReadableStreamDefaultController<Uint8Array>
    const encoder = new TextEncoder()
    const body = new ReadableStream<Uint8Array>({ start(controller) { stream = controller } })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, { headers: { 'Content-Type': 'application/x-ndjson' } })))
    render(<Chat documentCount={1} documentsReady onAdd={vi.fn()} />)
    await user.type(screen.getByRole('textbox', { name: 'Ask a question about your documents' }), 'Summarize')
    await user.click(screen.getByRole('button', { name: 'Send question' }))
    expect(screen.getByRole('status')).toHaveTextContent('Searching')
    await act(async () => stream.enqueue(encoder.encode(JSON.stringify({ type: 'delta', text: '**Start' }) + '\n')))
    expect(screen.getByLabelText('AgentRAG answer')).toHaveTextContent('Start')
    expect(screen.getByRole('status')).toHaveTextContent('Writing')
    expect(screen.getByRole('button', { name: 'Send question' })).toBeDisabled()
    await act(async () => stream.enqueue(encoder.encode(JSON.stringify({ type: 'delta', text: ' here.** See [Guide].' }) + '\n')))
    const reply = screen.getByLabelText('AgentRAG answer')
    expect(reply).toHaveTextContent('Start here. See [Guide].')
    expect(reply.querySelector('strong')).toHaveTextContent('Start here.')
    await act(async () => stream.enqueue(encoder.encode(JSON.stringify({ type: 'completed' }) + '\n')))
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument())
    expect(screen.getAllByLabelText('AgentRAG answer')).toHaveLength(1)
    expect(screen.getByRole('textbox', { name: 'Ask a question about your documents' })).toBeEnabled()
  })

  it('sends completed conversation history and excludes partial replies and duplicate questions on retry', async () => {
    const user = userEvent.setup()
    const fetch = vi.fn()
      .mockResolvedValueOnce(answer('Enchanté, Mathieu !'))
      .mockResolvedValueOnce(new Response('{"type":"delta","text":"Unfinished reply"}\n', { headers: { 'Content-Type': 'application/x-ndjson' } }))
      .mockResolvedValueOnce(answer('Tu t’appelles Mathieu.'))
      .mockResolvedValueOnce(answer('Oui, Mathieu.'))
    vi.stubGlobal('fetch', fetch)
    render(<Chat documentCount={1} documentsReady onAdd={vi.fn()} />)
    const input = screen.getByRole('textbox', { name: 'Ask a question about your documents' })
    const send = screen.getByRole('button', { name: 'Send question' })
    await user.type(input, 'Je m’appelle Mathieu.')
    await user.click(send)
    await waitFor(() => expect(input).toBeEnabled())
    expect(JSON.parse(fetch.mock.calls[0][1].body).history).toEqual([])
    await user.type(input, 'Comment je m’appelle ?')
    await user.click(send)
    await screen.findByRole('alert')
    const history = [
      { role: 'user', content: 'Je m’appelle Mathieu.' },
      { role: 'assistant', content: 'Enchanté, Mathieu !' },
    ]
    expect(JSON.parse(fetch.mock.calls[1][1].body)).toEqual({ message: 'Comment je m’appelle ?', history })
    await user.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(input).toBeEnabled())
    expect(JSON.parse(fetch.mock.calls[2][1].body)).toEqual({ message: 'Comment je m’appelle ?', history })
    await user.type(input, 'Tu te souviens ?')
    await user.click(send)
    await waitFor(() => expect(input).toBeEnabled())
    expect(JSON.parse(fetch.mock.calls[3][1].body).history).toEqual([
      ...history,
      { role: 'user', content: 'Comment je m’appelle ?' },
      { role: 'assistant', content: 'Tu t’appelles Mathieu.' },
    ])
  })

  it('marks interrupted answers as incomplete and replaces them when retrying without duplicating the question', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ type: 'delta', text: 'Partial answer' }) + '\n', { headers: { 'Content-Type': 'application/x-ndjson' } }))
      .mockResolvedValueOnce(answer('Complete answer [Guide]')))
    render(<Chat documentCount={1} documentsReady onAdd={vi.fn()} />)
    await user.type(screen.getByRole('textbox', { name: 'Ask a question about your documents' }), 'Summarize')
    await user.click(screen.getByRole('button', { name: 'Send question' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('before the answer was complete')
    expect(screen.getByLabelText('AgentRAG answer')).toHaveTextContent('Partial answer')
    expect(screen.getByText('Incomplete response')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(screen.getByLabelText('AgentRAG answer')).toHaveTextContent('Complete answer [Guide]'))
    expect(screen.getAllByLabelText('Your question')).toHaveLength(1)
    expect(screen.getAllByLabelText('AgentRAG answer')).toHaveLength(1)
    expect(screen.queryByText('Incomplete response')).not.toBeInTheDocument()
  })

  it('adds a document, waits for indexing, and requires confirmation before deletion', async () => {
    const user = userEvent.setup()
    const fetch = vi.fn()
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(indexed())
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
    render(<Workspace onLogout={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'Add your first document' }))
    const dialog = screen.getByRole('dialog')
    await user.type(within(dialog).getByLabelText('Document title'), 'Product guide')
    await user.type(within(dialog).getByLabelText('Content'), 'Start by adding your documents.')
    await user.click(within(dialog).getByRole('button', { name: 'Add document' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByText('Product guide')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Delete Product guide' }))
    expect(fetch).toHaveBeenCalledTimes(2)
    await user.click(screen.getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(screen.queryByText('Product guide')).not.toBeInTheDocument())
    expect(fetch.mock.calls[2][1].method).toBe('DELETE')
  })

  it('shows real passage progress and quota waits, then closes only after the document is saved', async () => {
    const user = userEvent.setup()
    let stream!: ReadableStreamDefaultController<Uint8Array>
    const encoder = new TextEncoder()
    const body = new ReadableStream<Uint8Array>({ start(controller) { stream = controller } })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(json([])).mockResolvedValueOnce(
      new Response(body, { headers: { 'Content-Type': 'application/x-ndjson' } }),
    ))
    render(<Workspace onLogout={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'Add your first document' }))
    const dialog = screen.getByRole('dialog')
    await user.type(within(dialog).getByLabelText('Document title'), 'Product guide')
    await user.type(within(dialog).getByLabelText('Content'), 'Document text.')
    await user.click(within(dialog).getByRole('button', { name: 'Add document' }))
    const leaveWhileIndexing = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(leaveWhileIndexing)
    expect(leaveWhileIndexing.defaultPrevented).toBe(true)
    await act(async () => stream.enqueue(encoder.encode(JSON.stringify({ type: 'progress', progress: { stage: 'embedding', completedChunks: 16, totalChunks: 32 } }) + '\n')))
    expect(within(dialog).getByText('16 / 32 passages prepared')).toBeInTheDocument()
    const addition = within(dialog).getByText('+16')
    expect(addition).toHaveClass('ingestion-added')
    expect(within(dialog).getByRole('progressbar')).toHaveAttribute('value', '48')
    await act(async () => stream.enqueue(encoder.encode(JSON.stringify({ type: 'progress', progress: { stage: 'rateLimited', completedChunks: 16, totalChunks: 32, retryAt: new Date(Date.now() + 60_000).toISOString(), retryAttempt: 1 } }) + '\n')))
    expect(within(dialog).getByRole('status')).toHaveTextContent('rate-limited')
    expect(within(dialog).getByRole('status')).toHaveTextContent('retry 1')
    expect(within(dialog).getByRole('button', { name: 'Close dialog' })).toBeDisabled()
    expect(within(dialog).getByText('+16')).toBe(addition)
    await act(async () => stream.enqueue(encoder.encode(JSON.stringify({ type: 'progress', progress: { stage: 'saving', completedChunks: 32, totalChunks: 32 } }) + '\n')))
    expect(within(dialog).getByRole('status')).toHaveTextContent('Saving')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    await act(async () => stream.enqueue(encoder.encode(JSON.stringify({ type: 'completed', document }) + '\n')))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByText('Product guide')).toBeInTheDocument()
    const leaveAfterIndexing = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(leaveAfterIndexing)
    expect(leaveAfterIndexing.defaultPrevented).toBe(false)
  })

  it('preserves the document form and shows an actionable error when indexing fails', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(json([])).mockResolvedValueOnce(
      new Response(JSON.stringify({ type: 'error', error: 'The embedding service is still rate-limited. Please try again later.' }) + '\n', { headers: { 'Content-Type': 'application/x-ndjson' } }),
    ))
    render(<Workspace onLogout={vi.fn()} />)
    await user.click(await screen.findByRole('button', { name: 'Add your first document' }))
    const dialog = screen.getByRole('dialog')
    await user.type(within(dialog).getByLabelText('Document title'), 'Product guide')
    await user.type(within(dialog).getByLabelText('Content'), 'Document text.')
    await user.click(within(dialog).getByRole('button', { name: 'Add document' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('rate-limited')
    expect(within(dialog).getByLabelText('Content')).toHaveValue('Document text.')
    expect(within(dialog).getByRole('button', { name: 'Add document' })).toBeEnabled()
    expect(screen.queryByText('Product guide')).not.toBeInTheDocument()
  })

  it('disables research when the knowledge base cannot load and allows a refresh', async () => {
    const user = userEvent.setup()
    vi.stubGlobal('fetch', vi.fn().mockRejectedValueOnce(new TypeError('offline')).mockResolvedValueOnce(json([document])))
    render(<Workspace onLogout={vi.fn()} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Unable to reach the server')
    expect(screen.getByRole('textbox', { name: 'Ask a question about your documents' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Refresh documents' }))
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Ask a question about your documents' })).toBeEnabled())
  })
})


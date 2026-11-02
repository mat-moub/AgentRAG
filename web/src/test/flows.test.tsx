import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { Login } from '../features/auth/Login'
import { Chat } from '../features/chat/Chat'
import { Workspace } from '../features/workspace/Workspace'

const document = { id: 1, title: 'Product guide', chunks: 3, createdUtc: '2026-11-02T10:00:00Z' }
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status })

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
      .mockResolvedValueOnce(json({ answer: '**Start here.** See [Product guide].' }))
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

  it('adds a document, waits for indexing, and requires confirmation before deletion', async () => {
    const user = userEvent.setup()
    const fetch = vi.fn()
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json(document))
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


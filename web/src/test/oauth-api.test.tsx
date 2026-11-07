import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { Login } from '../features/auth/Login'

const json = (value: unknown) => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } })

describe('Google session authentication', () => {
  beforeEach(() => { vi.resetModules(); localStorage.clear() })

  it('offers Google sign-in only when configured', () => {
    const { rerender } = render(<Login onDone={vi.fn()} />)
    expect(screen.queryByRole('link', { name: 'Continue with Google' })).not.toBeInTheDocument()
    rerender(<Login onDone={vi.fn()} googleEnabled />)
    expect(screen.getByRole('link', { name: 'Continue with Google' })).toHaveAttribute('href', expect.stringContaining('/account/google/start'))
  })

  it('restores a cookie session and protects mutations with its CSRF token', async () => {
    localStorage.setItem('token', 'stale-bearer')
    const fetch = vi.fn().mockResolvedValueOnce(json({ authenticated: true, googleEnabled: true, googleLinked: true, csrfToken: 'csrf' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
    const api = await import('../api')
    await api.loadSession()
    expect(api.isLoggedIn()).toBe(true)
    expect(localStorage.getItem('token')).toBeNull()
    await api.deleteDoc(1)
    expect(fetch.mock.calls[1][1]).toMatchObject({ credentials: 'include', headers: { 'X-CSRF-TOKEN': 'csrf' } })
    expect(fetch.mock.calls[1][1].headers.Authorization).toBeUndefined()
  })

  it('sends CSRF credentials on streaming requests and clears the session on logout', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(json({ authenticated: true, googleEnabled: true, googleLinked: true, csrfToken: 'csrf' }))
      .mockResolvedValueOnce(new Response('{"type":"completed"}\n', { headers: { 'Content-Type': 'application/x-ndjson' } }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetch)
    const api = await import('../api')
    await api.loadSession()
    await api.chat('Question', vi.fn())
    expect(fetch.mock.calls[1][1]).toMatchObject({ credentials: 'include', headers: { 'X-CSRF-TOKEN': 'csrf' } })
    await api.logout()
    expect(fetch.mock.calls[2][0]).toBe('/api/auth/logout')
    expect(api.isLoggedIn()).toBe(false)
  })

  it('keeps the session when server logout fails so the user can retry', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(json({ authenticated: true, googleEnabled: true, googleLinked: true, csrfToken: 'csrf' }))
      .mockResolvedValueOnce(new Response(null, { status: 500 }))
    vi.stubGlobal('fetch', fetch)
    const api = await import('../api')
    await api.loadSession()
    await expect(api.logout()).rejects.toThrow()
    expect(api.isLoggedIn()).toBe(true)
  })

  it('identifies unavailable services without blaming the password or exposing proxy HTML', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>private proxy details</html>', { status: 502 })))
    const api = await import('../api')
    await expect(api.login('reader@example.com', 'password')).rejects.toThrow('AgentRAG is temporarily unavailable')
  })

  it('explains an Identity credential rejection instead of displaying Failed', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'Failed' }), { status: 401 })))
    const api = await import('../api')
    await expect(api.login('reader@example.com', 'password')).rejects.toThrow('Please check your AgentRAG email and password, or continue with Google.')
  })

  it('submits values filled by a password manager without React change events', async () => {
    const fetch = vi.fn().mockResolvedValue(json({ accessToken: 'autofill-token' }))
    vi.stubGlobal('fetch', fetch)
    const done = vi.fn()
    const { container } = render(<Login onDone={done} />)
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!
    setter.call(screen.getByLabelText('Email address'), 'reader@example.com')
    setter.call(screen.getByLabelText('Password'), 'Research1!')
    fireEvent.submit(container.querySelector('form')!)
    await waitFor(() => expect(done).toHaveBeenCalledOnce())
    expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual({ email: 'reader@example.com', password: 'Research1!' })
  })
})

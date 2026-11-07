import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
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
})

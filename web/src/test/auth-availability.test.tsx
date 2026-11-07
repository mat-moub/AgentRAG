import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App'
import * as api from '../api'

vi.mock('../api', async importOriginal => ({
  ...await importOriginal<typeof import('../api')>(),
  isLoggedIn: () => false,
  loadSession: vi.fn(),
}))

describe('Sign-in availability', () => {
  beforeEach(() => { vi.mocked(api.loadSession).mockReset() })

  it('shows Google on the home page when the sign-in service enables it', async () => {
    vi.mocked(api.loadSession).mockResolvedValue({ authenticated: false, googleEnabled: true, googleLinked: false, csrfToken: 'csrf' })
    render(<App />)
    expect(await screen.findByRole('link', { name: 'Continue with Google' })).toHaveAttribute('href', api.googleSignInUrl)
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled()
  })

  it('offers all public information pages from the login screen without a session', async () => {
    vi.mocked(api.loadSession).mockResolvedValue({ authenticated: false, googleEnabled: false, googleLinked: false, csrfToken: 'csrf' })
    render(<App />)
    const navigation = within(await screen.findByRole('navigation', { name: 'Site information' }))
    for (const [label, path] of [['About', '/about'], ['Support', '/support'], ['Privacy policy', '/privacy'], ['Terms of service', '/terms']] as const) {
      const link = navigation.getByRole('link', { name: new RegExp(`^${label}\\s*\\(opens in a new tab\\)$`) })
      expect(link).toHaveAttribute('href', api.publicPageUrl(path))
      expect(link).toHaveAttribute('rel', 'noopener noreferrer')
    }
  })

  it('explains a failed session check and restores Google after retrying', async () => {
    vi.mocked(api.loadSession).mockRejectedValueOnce(new Error('AgentRAG is temporarily unavailable. Please try again shortly.'))
      .mockResolvedValueOnce({ authenticated: false, googleEnabled: true, googleLinked: false, csrfToken: 'csrf' })
    const user = userEvent.setup()
    render(<App />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Google sign-in could not be loaded')
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled()
    await user.click(screen.getByRole('button', { name: 'Retry connection' }))
    expect(await screen.findByRole('link', { name: 'Continue with Google' })).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

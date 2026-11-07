import { beforeEach, describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
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

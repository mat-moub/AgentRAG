import { useState } from 'react'
import type { SubmitEvent } from 'react'
import { ArrowRightIcon, EyeIcon, EyeSlashIcon } from '@phosphor-icons/react'
import { Brand } from '../../components/Brand'
import { Button, Field, IconButton, Notice } from '../../components/ui'
import * as api from '../../api'

export function Login({ onDone }: { onDone: () => void }) {
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [visible, setVisible] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [registered, setRegistered] = useState(false)
  const registering = mode === 'register'

  async function submit(e: SubmitEvent<HTMLFormElement>) {
    e.preventDefault()
    if (busy) return
    setBusy(true); setError('')
    try {
      if (registering && !registered) {
        await api.register(email.trim(), password)
        setRegistered(true)
      }
      await api.login(email.trim(), password)
      onDone()
    } catch (e) { setError(api.errorMessage(e)) }
    finally { setBusy(false) }
  }

  return <div className="flex min-h-dvh flex-col">
    <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50 focus:bg-canvas focus:p-3">Skip to content</a>
    <header className="mx-6 flex items-center justify-between border-b border-line py-5 sm:mx-10 sm:py-7 lg:mx-16">
      <Brand /><span className="hidden text-xs tracking-wide text-muted sm:block">Your knowledge, connected.</span>
    </header>
    <main id="main" className="mx-auto flex w-full max-w-6xl flex-1 flex-col justify-center px-6 py-12 sm:px-10 sm:py-16 lg:grid lg:grid-cols-2 lg:items-center lg:gap-16 lg:px-16">
      <section className="mx-auto mb-10 w-full max-w-sm lg:mb-0 lg:max-w-none" aria-labelledby="intro-title">
        <p className="eyebrow mb-5">A little less searching.</p>
        <h1 id="intro-title" className="font-display text-[clamp(2.65rem,8vw,4.4rem)] leading-[1.03] tracking-[-0.035em]">Your documents.<br />Clear answers.</h1>
        <p className="mt-5 max-w-xs text-[15px] leading-7 text-muted sm:text-base">Ask your knowledge.<br />Find the sources behind every answer.</p>
      </section>
      <section className="mx-auto w-full max-w-sm lg:max-w-md lg:border-l lg:border-line lg:pl-12" aria-labelledby="auth-title">
        <h2 id="auth-title" className="mb-2 text-2xl font-semibold tracking-tight">{registering ? 'Create your account' : 'Welcome back'}</h2>
        <p className="mb-7 text-sm leading-6 text-muted">{registering ? 'Make room for what you know.' : 'Sign in to your research space.'}</p>
        <form onSubmit={submit} className="space-y-5">
          <Field label="Email address" type="email" autoComplete="email" placeholder="you@company.com" required maxLength={254} value={email} onChange={e => { setEmail(e.target.value); setRegistered(false) }} disabled={busy} />
          <div className="relative">
            <Field label="Password" type={visible ? 'text' : 'password'} autoComplete={registering ? 'new-password' : 'current-password'} placeholder="Enter your password" required value={password} onChange={e => { setPassword(e.target.value); setRegistered(false) }} disabled={busy} hint={registering ? 'Use at least 6 characters, with uppercase, lowercase, a number and a symbol.' : undefined} style={{ paddingRight: '3.5rem' }} />
            <IconButton label={visible ? 'Hide password' : 'Show password'} onClick={() => setVisible(!visible)} className="absolute top-8 right-1" aria-pressed={visible}>{visible ? <EyeSlashIcon size={20} /> : <EyeIcon size={20} />}</IconButton>
          </div>
          {registered && <p role="status" className="text-sm text-accent">Your account is created. Sign in to continue.</p>}
          {error && <Notice>{error}</Notice>}
          <Button type="submit" className="w-full" busy={busy}>{registering && !registered ? 'Create account' : 'Sign in'}{!busy && <ArrowRightIcon size={18} aria-hidden />}</Button>
        </form>
        <p className="mt-6 text-sm leading-6 text-muted">{registering ? 'Already have an account?' : 'New to AgentRAG?'}{' '}<button type="button" className="min-h-11 font-medium text-ink underline decoration-line underline-offset-4 hover:decoration-ink disabled:opacity-50" disabled={busy} onClick={() => { setMode(registering ? 'login' : 'register'); setError(''); setRegistered(false) }}>{registering ? 'Sign in' : 'Create an account'}</button></p>
      </section>
    </main>
    <footer className="px-6 pb-6 text-center text-xs leading-5 text-muted sm:pb-8">Open source. Built around your knowledge.</footer>
  </div>
}

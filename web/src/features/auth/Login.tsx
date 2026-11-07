import { useState } from 'react'
import type { SubmitEvent } from 'react'
import { ArrowRightIcon, EyeIcon, EyeSlashIcon } from '@phosphor-icons/react'
import { Brand } from '../../components/Brand'
import { SiteFooter } from '../../components/SiteFooter'
import { Button, Field, IconButton, Notice } from '../../components/ui'
import * as api from '../../api'

export function Login({ onDone, googleEnabled = false, sessionError = '', onRetrySession }: {
  onDone: () => void; googleEnabled?: boolean; sessionError?: string; onRetrySession?: () => void
}) {
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
    // Password managers can fill the DOM without updating React's input state.
    const fields = new FormData(e.currentTarget)
    const submittedEmail = String(fields.get('email') ?? '').trim()
    const submittedPassword = String(fields.get('password') ?? '')
    setBusy(true); setError('')
    try {
      if (registering && !registered) {
        await api.register(submittedEmail, submittedPassword)
        setRegistered(true)
      }
      await api.login(submittedEmail, submittedPassword)
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
        {sessionError && <div className="mb-6 space-y-2"><Notice>{sessionError} Google sign-in could not be loaded.</Notice><Button variant="secondary" onClick={onRetrySession}>Retry connection</Button></div>}
        {googleEnabled && <><a href={api.googleSignInUrl} className="flex min-h-11 w-full items-center justify-center gap-2.5 rounded-control border border-line bg-white px-4 py-2.5 text-sm font-medium text-[#1f1f1f] hover:bg-[#f2f2f2]"><img src="/images/google-g.png" alt="" aria-hidden="true" width={20} height={20} className="size-5 shrink-0" />Continue with Google</a><p className="my-5 text-center text-xs text-muted">or sign in with your AgentRAG password</p></>}
        <form onSubmit={submit} className="space-y-5">
          <Field label="Email address" name="email" type="email" autoComplete="username" placeholder="you@company.com" required maxLength={254} value={email} onChange={e => { setEmail(e.target.value); setRegistered(false) }} disabled={busy} />
          <div className="relative">
            <Field label="Password" name="password" type={visible ? 'text' : 'password'} autoComplete={registering ? 'new-password' : 'current-password'} placeholder="Enter your password" required value={password} onChange={e => { setPassword(e.target.value); setRegistered(false) }} disabled={busy} hint={registering ? 'Use at least 6 characters, with uppercase, lowercase, a number and a symbol.' : undefined} style={{ paddingRight: '3.5rem' }} />
            <IconButton label={visible ? 'Hide password' : 'Show password'} onClick={() => setVisible(!visible)} className="absolute top-8 right-1" aria-pressed={visible}>{visible ? <EyeSlashIcon size={20} /> : <EyeIcon size={20} />}</IconButton>
          </div>
          {registered && <p role="status" className="text-sm text-accent">Your account is created. Sign in to continue.</p>}
          {error && <Notice>{error}</Notice>}
          <Button type="submit" className="w-full" busy={busy}>{registering && !registered ? 'Create account' : 'Sign in'}{!busy && <ArrowRightIcon size={18} aria-hidden />}</Button>
        </form>
        <p className="mt-6 text-sm leading-6 text-muted">{registering ? 'Already have an account?' : 'New to AgentRAG?'}{' '}<button type="button" className="min-h-11 font-medium text-ink underline decoration-line underline-offset-4 hover:decoration-ink disabled:opacity-50" disabled={busy} onClick={() => { setMode(registering ? 'login' : 'register'); setError(''); setRegistered(false) }}>{registering ? 'Sign in' : 'Create an account'}</button></p>
      </section>
    </main>
    <section className="mx-auto w-full max-w-6xl px-6 pb-16 sm:px-10 lg:px-16" aria-labelledby="preview-title">
      <p className="eyebrow mb-3">A closer look</p>
      <h2 id="preview-title" className="font-display text-3xl tracking-tight">Explore your knowledge</h2>
      <p className="mt-3 text-sm leading-6 text-muted">Bring your documents together and discover the passages that matter.</p>
      <div className="mt-8 grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
        {[
          { number: '01', title: 'Prepare your documents', description: 'Add your sources and follow their progress as they are indexed and made searchable.', image: '/images/preview-indexing.png', alt: 'Document indexing progress' },
          { number: '02', title: 'Find relevant passages', description: 'Search for an exact phrase or discover passages with a similar meaning.', image: '/images/preview-search.png', alt: 'Search results showing a matching passage' },
          { number: '03', title: 'See connections', description: 'Explore your knowledge visually and select a point to read its passage.', image: '/images/preview-map.png', alt: 'Map of related document passages' },
        ].map(item => <article key={item.title} className="overflow-hidden rounded-xl border border-line bg-white">
          <div className="flex h-48 items-center justify-center bg-[#f7f6f4] p-4 sm:h-52">
            <img src={item.image} alt={item.alt} className="max-h-full w-full object-contain" loading="lazy" />
          </div>
          <div className="p-5 sm:p-6">
            <span className="text-xs font-semibold tracking-widest text-accent">{item.number}</span>
            <h3 className="mt-2 text-lg font-semibold">{item.title}</h3>
            <p className="mt-2 text-sm leading-6 text-muted">{item.description}</p>
          </div>
        </article>)}
      </div>
    </section>
    <SiteFooter />
  </div>
}

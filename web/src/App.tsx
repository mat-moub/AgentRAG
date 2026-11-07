import { useEffect, useState } from 'react'
import { Login } from './features/auth/Login'
import { Workspace } from './features/workspace/Workspace'
import * as api from './api'
import { Notice } from './components/ui'

export default function App() {
  const [loggedIn, setLoggedIn] = useState(api.isLoggedIn())
  const [ready, setReady] = useState(false)
  const [session, setSession] = useState<api.Session | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    let active = true
    api.loadSession().then(value => {
      if (active) { setSession(value); setLoggedIn(api.isLoggedIn()) }
    }).catch(() => { /* Existing bearer login remains available when session discovery fails. */ })
      .finally(() => { if (active) setReady(true) })
    const expired = () => setLoggedIn(false)
    window.addEventListener('agentrag:unauthorized', expired)
    return () => { active = false; window.removeEventListener('agentrag:unauthorized', expired) }
  }, [])
  if (!ready) return <p role="status" className="p-6">Loading your session…</p>
  return <>{error && <Notice>{error}</Notice>}{loggedIn
    ? <Workspace googleEnabled={session?.googleEnabled} googleLinked={session?.googleLinked} onLogout={() => {
      api.logout().then(() => { setError(''); setLoggedIn(false) }).catch(e => setError(api.errorMessage(e)))
    }} />
    : <Login googleEnabled={session?.googleEnabled} onDone={() => setLoggedIn(true)} />}</>
}

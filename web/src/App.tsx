import { useEffect, useState } from 'react'
import { Login } from './features/auth/Login'
import { Workspace } from './features/workspace/Workspace'
import * as api from './api'

export default function App() {
  const [loggedIn, setLoggedIn] = useState(api.isLoggedIn())
  useEffect(() => {
    const expired = () => setLoggedIn(false)
    window.addEventListener('agentrag:unauthorized', expired)
    return () => window.removeEventListener('agentrag:unauthorized', expired)
  }, [])
  return loggedIn
    ? <Workspace onLogout={() => { api.logout(); setLoggedIn(false) }} />
    : <Login onDone={() => setLoggedIn(true)} />
}

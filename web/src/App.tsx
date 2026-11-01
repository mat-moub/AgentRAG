import { useEffect, useState } from 'react'
import * as api from './api'

function Login({ onDone }: { onDone: () => void }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')

  const submit = async (register: boolean) => {
    try {
      setError('')
      if (register) await api.register(email, password)
      await api.login(email, password)
      onDone()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <div className="card">
      <h2>Sign in</h2>
      <input placeholder="Email" value={email} onChange={e => setEmail(e.target.value)} />
      <input placeholder="Password" type="password" value={password} onChange={e => setPassword(e.target.value)} />
      <button onClick={() => submit(false)}>Login</button>
      <button onClick={() => submit(true)}>Register</button>
      {error && <pre className="error">{error}</pre>}
    </div>
  )
}

function Documents() {
  const [docs, setDocs] = useState<api.Doc[]>([])
  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const [error, setError] = useState('')

  const refresh = () => api.listDocs().then(setDocs).catch(e => setError(e.message))
  useEffect(() => { refresh() }, [])

  const add = async () => {
    try {
      setError('')
      await api.addDoc(title, content)
      setTitle(''); setContent('')
      refresh()
    } catch (e) { setError((e as Error).message) }
  }

  return (
    <div className="card">
      <h2>Knowledge base</h2>
      <input placeholder="Title" value={title} onChange={e => setTitle(e.target.value)} />
      <textarea placeholder="Paste document text…" rows={5} value={content} onChange={e => setContent(e.target.value)} />
      <button onClick={add}>Add document</button>
      {error && <pre className="error">{error}</pre>}
      <ul>
        {docs.map(d => (
          <li key={d.id}>
            {d.title} ({d.chunks} chunks){' '}
            <button onClick={() => api.deleteDoc(d.id).then(refresh)}>Delete</button>
          </li>
        ))}
      </ul>
    </div>
  )
}

function Chat() {
  const [messages, setMessages] = useState<{ role: 'user' | 'agent'; text: string }[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)

  const send = async () => {
    const text = input.trim()
    if (!text) return
    setInput(''); setBusy(true)
    setMessages(m => [...m, { role: 'user', text }])
    try {
      const r = await api.chat(text)
      setMessages(m => [...m, { role: 'agent', text: r.answer }])
    } catch (e) {
      setMessages(m => [...m, { role: 'agent', text: `Error: ${(e as Error).message}` }])
    } finally { setBusy(false) }
  }

  return (
    <div className="card">
      <h2>Chat</h2>
      <div className="messages">
        {messages.map((m, i) => <p key={i} className={m.role}><b>{m.role}:</b> {m.text}</p>)}
      </div>
      <input value={input} disabled={busy} placeholder="Ask about your documents…"
        onChange={e => setInput(e.target.value)} onKeyDown={e => e.key === 'Enter' && send()} />
      <button disabled={busy} onClick={send}>Send</button>
    </div>
  )
}

export default function App() {
  const [loggedIn, setLoggedIn] = useState(api.isLoggedIn())
  if (!loggedIn) return <Login onDone={() => setLoggedIn(true)} />
  return (
    <main>
      <header>
        <h1>Agent RAG</h1>
        <button onClick={() => { api.logout(); setLoggedIn(false) }}>Logout</button>
      </header>
      <Documents />
      <Chat />
    </main>
  )
}

const BASE = import.meta.env.VITE_API_URL ?? ''
const AUTH_BASE = import.meta.env.VITE_AUTH_URL ?? (import.meta.env.DEV ? 'http://127.0.0.1:5000' : BASE)
export const googleSignInUrl = AUTH_BASE + '/account/google/start'
export const googleLinkUrl = AUTH_BASE + '/account/link'
let csrfToken: string | null = null
let cookieSession = false

export interface Session { authenticated: boolean; googleEnabled: boolean; googleLinked: boolean; csrfToken: string }
export async function loadSession(): Promise<Session> {
  const session = await request<Session>('/api/auth/session')
  csrfToken = session.csrfToken
  cookieSession = session.authenticated
  if (cookieSession) { token = null; localStorage.removeItem('token') }
  return session
}

export interface Passage { id: number; documentId: number; title: string; index: number; content: string; x: number; y: number; score: number | null; exact: boolean }
export const explorePassages = (query: string, documentId?: number, signal?: AbortSignal) =>
  request<{ total: number; passages: Passage[] }>('/api/explore', { method: 'POST', body: JSON.stringify({ query, documentId }), signal })

let token: string | null = localStorage.getItem('token')

export const isLoggedIn = () => cookieSession || !!token
export const logout = async () => {
  if (cookieSession) await request<void>('/api/auth/logout', { method: 'POST' })
  cookieSession = false
  token = null
  localStorage.removeItem('token')
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(BASE + path, {
    ...init,
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: 'Bearer ' + token } : {}),
      ...(!token && csrfToken ? { 'X-CSRF-TOKEN': csrfToken } : {}),
    },
  })
  await checkResponse(res)
  const text = await res.text()
  return text ? JSON.parse(text) as T : undefined as T
}

async function checkResponse(res: Response) {
  if (!res.ok) {
    if (res.status === 401 && isLoggedIn()) {
      token = null
      cookieSession = false
      localStorage.removeItem('token')
      window.dispatchEvent(new Event('agentrag:unauthorized'))
    }
    let message = res.status === 401 ? 'Please check your email and password, then sign in again.' : 'Something went wrong. Please try again.'
    try {
      const problem = await res.json()
      const errors = Object.values(problem.errors ?? {}).flat().filter((value): value is string => typeof value === 'string')
      message = errors.length ? errors.join(' ') : problem.detail || message
    } catch { /* Never expose server HTML or stack traces in the interface. */ }
    throw new Error(message)
  }
}

export const errorMessage = (error: unknown) => error instanceof TypeError
  ? 'Unable to reach the server. Check your connection and try again.'
  : error instanceof Error ? error.message : 'Something went wrong. Please try again.'

export const register = (email: string, password: string) =>
  request('/api/auth/register', { method: 'POST', body: JSON.stringify({ email, password }) })

export async function login(email: string, password: string) {
  const r = await request<{ accessToken: string }>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  })
  token = r.accessToken
  localStorage.setItem('token', token)
}

export interface Doc { id: number; title: string; createdUtc: string; chunks: number }
export const listDocs = () => request<Doc[]>('/api/documents')
export interface IngestionProgress {
  stage: 'chunking' | 'embedding' | 'rateLimited' | 'saving'
  completedChunks: number
  totalChunks: number
  retryAt?: string | null
  retryAttempt?: number
}

type IngestionEvent =
  | { type: 'progress'; progress: IngestionProgress }
  | { type: 'completed'; document: Doc }
  | { type: 'error'; error: string }
  | { type: 'heartbeat' }

export async function addDoc(title: string, content: string | File, onProgress?: (progress: IngestionProgress) => void, signal?: AbortSignal): Promise<Doc> {
  const isFile = content instanceof File
  const body = isFile ? new FormData() : { title, content }
  if (body instanceof FormData) {
    body.append('title', title)
    body.append('file', content as File)
  }
  for await (const event of streamEvents<IngestionEvent>(isFile ? '/api/documents/upload' : '/api/documents/ingest', body, signal)) {
    if (event.type === 'progress') onProgress?.(event.progress)
    if (event.type === 'error') throw new Error(event.error)
    if (event.type === 'completed') return event.document
  }
  throw new Error('The connection ended before completion was confirmed. Refresh your documents before trying again.')
}

async function* streamEvents<T>(path: string, body: unknown, signal?: AbortSignal): AsyncGenerator<T> {
  const res = await fetch(BASE + path, {
    method: 'POST',
    credentials: 'include',
    headers: { ...(body instanceof FormData ? {} : { 'Content-Type': 'application/json' }), ...(token ? { Authorization: 'Bearer ' + token } : {}), ...(!token && csrfToken ? { 'X-CSRF-TOKEN': csrfToken } : {}) },
    body: body instanceof FormData ? body : JSON.stringify(body),
    signal,
  })
  await checkResponse(res)
  if (!res.body || !res.headers.get('Content-Type')?.includes('application/x-ndjson'))
    throw new Error('The server did not return a response stream. Please refresh and try again.')
  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let pending = ''
  try {
    while (true) {
      const { value, done } = await reader.read()
      pending += decoder.decode(value, { stream: !done })
      let newline: number
      while ((newline = pending.indexOf('\n')) >= 0) {
        const line = pending.slice(0, newline)
        pending = pending.slice(newline + 1)
        if (line.trim()) yield JSON.parse(line) as T
      }
      if (done) {
        if (pending.trim()) yield JSON.parse(pending) as T
        return
      }
    }
  } finally {
    await reader.cancel().catch(() => {})
    reader.releaseLock()
  }
}
export const deleteDoc = (id: number) => request<void>(`/api/documents/${id}`, { method: 'DELETE' })

type ChatEvent =
  | { type: 'delta'; text: string }
  | { type: 'error'; error: string }
  | { type: 'started' | 'heartbeat' | 'completed' }

export interface ChatMessage { role: 'user' | 'assistant'; content: string }

export async function chat(message: string, onDelta: (text: string) => void, signal?: AbortSignal, history: ChatMessage[] = []): Promise<void> {
  for await (const event of streamEvents<ChatEvent>('/api/chat/stream', { message, history }, signal)) {
    if (event.type === 'delta') onDelta(event.text)
    if (event.type === 'error') throw new Error(event.error)
    if (event.type === 'completed') return
  }
  throw new Error('The connection ended before the answer was complete. Please try again.')
}

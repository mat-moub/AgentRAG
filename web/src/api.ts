const BASE = import.meta.env.VITE_API_URL ?? ''

let token: string | null = localStorage.getItem('token')

export const isLoggedIn = () => !!token
export const logout = () => {
  token = null
  localStorage.removeItem('token')
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(BASE + path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: 'Bearer ' + token } : {}),
    },
  })
  const text = await res.text()
  if (!res.ok) {
    if (res.status === 401 && token) {
      logout()
      window.dispatchEvent(new Event('agentrag:unauthorized'))
    }
    let message = res.status === 401 ? 'Please check your email and password, then sign in again.' : 'Something went wrong. Please try again.'
    try {
      const problem = JSON.parse(text)
      const errors = Object.values(problem.errors ?? {}).flat().filter((value): value is string => typeof value === 'string')
      message = errors.length ? errors.join(' ') : problem.detail || message
    } catch { /* Never expose server HTML or stack traces in the interface. */ }
    throw new Error(message)
  }
  return text ? JSON.parse(text) as T : undefined as T
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
export const addDoc = (title: string, content: string) =>
  request<Doc>('/api/documents', { method: 'POST', body: JSON.stringify({ title, content }) })
export const deleteDoc = (id: number) => request<void>(`/api/documents/${id}`, { method: 'DELETE' })
export const chat = (message: string) =>
  request<{ answer: string }>('/api/chat', { method: 'POST', body: JSON.stringify({ message }) })

const BASE = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'

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
  if (!res.ok) throw new Error((await res.text()) || res.statusText)
  return res.status === 204 || res.headers.get('content-length') === '0'
    ? (undefined as T)
    : res.json()
}

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

const BASE = import.meta.env.VITE_API_URL ?? ''
const AUTH_BASE = (import.meta.env.VITE_AUTH_URL ?? (import.meta.env.DEV ? 'http://127.0.0.1:5000' : BASE)).replace(/\/$/, '')
export const publicPageUrl = (path: '/about' | '/support' | '/privacy' | '/terms') => AUTH_BASE + path
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
export const explorePassages = (query: string, documentId?: number, signal?: AbortSignal, folderId?: number) =>
  request<{ total: number; passages: Passage[] }>('/api/explore', { method: 'POST', body: JSON.stringify({ query, documentId, folderId }), signal })

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
  await checkResponse(res, path !== '/api/library' && !path.startsWith('/api/wikis'))
  const text = await res.text()
  return text ? JSON.parse(text) as T : undefined as T
}

async function checkResponse(res: Response, invalidateAccess = true) {
  if (!res.ok) {
    if (res.status === 401 && isLoggedIn()) {
      token = null
      cookieSession = false
      localStorage.removeItem('token')
      window.dispatchEvent(new Event('agentrag:unauthorized'))
    }
    let message = res.status === 401 ? 'Please check your email and password, then sign in again.'
      : [502, 503, 504].includes(res.status) ? 'AgentRAG is temporarily unavailable. Please try again shortly.'
        : 'Something went wrong. Please try again.'
    try {
      const problem = await res.json()
      const errors = Object.values(problem.errors ?? {}).flat().filter((value): value is string => typeof value === 'string')
      message = res.status === 401 && ['Failed', 'NotAllowed'].includes(problem.detail)
        ? 'Please check your AgentRAG email and password, or continue with Google.'
        : res.status === 401 && problem.detail === 'LockedOut'
          ? 'Too many sign-in attempts. Please wait before trying again.'
          : errors.length ? errors.join(' ') : problem.detail || message
    } catch { /* Never expose server HTML or stack traces in the interface. */ }
    if (invalidateAccess && ([403, 404].includes(res.status) || (res.status === 409 && message === 'Your sources or access changed. Start a new conversation.')))
      window.dispatchEvent(new Event('agentrag:access-changed'))
    throw Object.assign(new Error(message), { status: res.status })
  }
}

/** HTTP status of a failed request, when the error came from the server. */
export const errorStatus = (error: unknown) => (error as { status?: number } | null)?.status
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

export interface Doc { id: number; title: string; createdUtc: string; chunks: number; folderId?: number | null; canWrite?: boolean; addedBy?: string | null; indexed?: boolean }
type DocumentContentEvent =
  | { type: 'started'; id: number; title: string; chunks: number; indexed?: boolean }
  | { type: 'text'; text: string }
  | { type: 'error'; error: string; accessChanged?: boolean }
  | { type: 'heartbeat' | 'completed' }

export interface DocumentContentHandlers { onStart?: (info: { title: string; chunks: number; indexed: boolean }) => void; onText: (text: string) => void }

/** Streams a document's text page by page so large documents render progressively. */
export async function streamDocumentContent(id: number, handlers: DocumentContentHandlers, signal?: AbortSignal): Promise<void> {
  for await (const event of streamEvents<DocumentContentEvent>(`/api/documents/${id}/content`, undefined, signal, 'GET')) {
    if (event.type === 'started') handlers.onStart?.({ title: event.title, chunks: event.chunks, indexed: event.indexed !== false })
    if (event.type === 'text') handlers.onText(event.text)
    if (event.type === 'error') {
      if (event.accessChanged) window.dispatchEvent(new Event('agentrag:access-changed'))
      throw new Error(event.error)
    }
    if (event.type === 'completed') return
  }
  throw new Error('The connection ended before the whole document was loaded. Please try again.')
}
export interface Folder {
  id: number; name: string; parentId: number | null; path: string; role: 'owner' | 'read' | 'write'
  shareRootId: number | null; shareRootName: string | null
  canWrite: boolean; canEdit: boolean; canManage: boolean; documentCount: number; folderCount: number
}
/** A connected Git repository (GitHub or Azure DevOps). Its files are the documents of `folderId`. The access token is never sent back. */
export interface Repository {
  id: number; folderId: number; repository: string; branch: string; provider?: 'github' | 'azuredevops'
  lastSyncedCommit: string | null; lastSyncedUtc: string | null; lastError: string | null
  documents: number; canSync: boolean
}
export interface SyncProgress { stage: 'listing' | 'fetching' | 'completed'; completed: number; total: number; path?: string | null }
export interface SyncSummary { commit: string; added: number; updated: number; removed: number; unchanged: number; skipped: number }
type RepositorySyncEvent =
  | { type: 'progress'; progress: SyncProgress }
  | { type: 'completed'; summary: SyncSummary }
  | { type: 'error'; error: string }
  | { type: 'heartbeat' }
export const repositoryHostName = (provider?: string) => provider === 'azuredevops' ? 'Azure DevOps' : 'GitHub'
/** Git host recognised in what the user typed, mirroring the server's rules closely enough to show the right token hint. */
export const repositoryHostOf = (address: string): 'github' | 'azuredevops' | null => {
  const text = address.trim().toLowerCase().replace(/^https?:\/\//, '').replace(/^[^/@]*@/, '')
  if (text.startsWith('dev.azure.com/') || /^[a-z0-9-]+\.visualstudio\.com\//.test(text)) return 'azuredevops'
  if (text.startsWith('github.com/') || /^[a-z0-9_.-]+\/[a-z0-9_.-]+$/.test(text)) return 'github'
  return null
}
export const repositoryStatus = (repository: Repository) => repository.lastSyncedUtc
  ? `Synchronised ${new Date(repository.lastSyncedUtc).toLocaleString()} at commit ${repository.lastSyncedCommit?.slice(0, 7)}`
  : 'Not synchronised yet'
export const connectRepository = (repository: string, accessToken: string, branch: string, parentFolderId: number | null) =>
  request<Repository>('/api/repositories', { method: 'POST', body: JSON.stringify({ repository, token: accessToken, branch: branch || null, parentFolderId }) })
export const replaceRepositoryToken = (id: number, accessToken: string) => request<void>(`/api/repositories/${id}/token`, { method: 'PUT', body: JSON.stringify({ token: accessToken }) })
export const disconnectRepository = (id: number) => request<void>(`/api/repositories/${id}`, { method: 'DELETE' })

/** Fetches the files changed since the last synchronisation, reporting progress as they are saved. */
export async function syncRepository(id: number, onProgress?: (progress: SyncProgress) => void, signal?: AbortSignal): Promise<SyncSummary> {
  for await (const event of streamEvents<RepositorySyncEvent>(`/api/repositories/${id}/sync`, {}, signal)) {
    if (event.type === 'progress') onProgress?.(event.progress)
    if (event.type === 'error') throw new Error(event.error)
    if (event.type === 'completed') return event.summary
  }
  throw new Error('The connection ended before the synchronisation finished. Files already saved are kept: synchronise again to continue.')
}
export interface Library { folders: Folder[]; documents: Doc[]; revision: string; repositories?: Repository[] }
export interface FolderMember { userId: string; email: string; role: 'read' | 'write' }
export const listLibrary = (signal?: AbortSignal) => request<Library>('/api/library', { signal })
export const createFolder = (name: string, parentId: number | null) => request('/api/library/folders', { method: 'POST', body: JSON.stringify({ name, parentId }) })
export const renameItem = (kind: 'folders' | 'documents', id: number, name: string) => request<void>(`${kind === 'folders' ? '/api/library/folders' : '/api/documents'}/${id}`, { method: 'PATCH', body: JSON.stringify({ name }) })
export const moveItem = (kind: 'folders' | 'documents', id: number, folderId: number | null, confirmAccessChange: boolean) => request<void>(`${kind === 'folders' ? '/api/library/folders' : '/api/documents'}/${id}/move`, { method: 'POST', body: JSON.stringify({ folderId, confirmAccessChange }) })
export const deleteFolder = (id: number) => request<void>(`/api/library/folders/${id}`, { method: 'DELETE' })
export const listMembers = (id: number, signal?: AbortSignal) => request<FolderMember[]>(`/api/library/folders/${id}/members`, { signal })
export const addMember = (id: number, email: string, role: string) => request<void>(`/api/library/folders/${id}/members`, { method: 'POST', body: JSON.stringify({ email, role }) })
export const changeMember = (id: number, userId: string, role: string) => request<void>(`/api/library/folders/${id}/members/${encodeURIComponent(userId)}`, { method: 'PATCH', body: JSON.stringify({ role }) })
export const removeMember = (id: number, userId: string) => request<void>(`/api/library/folders/${id}/members/${encodeURIComponent(userId)}`, { method: 'DELETE' })
export const stopSharing = (id: number) => request<void>(`/api/library/folders/${id}/sharing`, { method: 'DELETE' })
export const leaveFolder = (id: number) => request<void>(`/api/library/folders/${id}/membership`, { method: 'DELETE' })
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
  | { type: 'error'; error: string; accessChanged?: boolean }
  | { type: 'heartbeat' }

export async function addDoc(title: string, content: string | File, onProgress?: (progress: IngestionProgress) => void, signal?: AbortSignal, folderId?: number | null, index = true): Promise<Doc> {
  const isFile = content instanceof File
  const body = isFile ? new FormData() : { title, content, folderId, index }
  if (body instanceof FormData) {
    body.append('title', title)
    body.append('file', content as File)
    if (folderId != null) body.append('folderId', String(folderId))
    body.append('index', String(index))
  }
  for await (const event of streamEvents<IngestionEvent>(isFile ? '/api/documents/upload' : '/api/documents/ingest', body, signal)) {
    if (event.type === 'progress') onProgress?.(event.progress)
    if (event.type === 'error') {
      if (event.accessChanged) window.dispatchEvent(new Event('agentrag:access-changed'))
      throw new Error(event.error)
    }
    if (event.type === 'completed') return event.document
  }
  throw new Error('The connection ended before completion was confirmed. Refresh your documents before trying again.')
}

async function* streamEvents<T>(path: string, body: unknown, signal?: AbortSignal, method: 'GET' | 'POST' = 'POST'): AsyncGenerator<T> {
  const hasBody = method !== 'GET'
  const res = await fetch(BASE + path, {
    method,
    credentials: 'include',
    headers: { ...(!hasBody || body instanceof FormData ? {} : { 'Content-Type': 'application/json' }), ...(token ? { Authorization: 'Bearer ' + token } : {}), ...(!token && csrfToken ? { 'X-CSRF-TOKEN': csrfToken } : {}) },
    // oxlint-disable-next-line unicorn/no-invalid-fetch-options -- body is undefined whenever the method is GET
    body: !hasBody ? undefined : body instanceof FormData ? body : JSON.stringify(body),
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
  | { type: 'error'; error: string; accessChanged?: boolean }
  | { type: 'started' | 'heartbeat' | 'completed' }

export interface ChatMessage { role: 'user' | 'assistant'; content: string }

export async function chat(message: string, onDelta: (text: string) => void, signal?: AbortSignal, history: ChatMessage[] = [], folderId?: number, libraryRevision?: string): Promise<void> {
  for await (const event of streamEvents<ChatEvent>('/api/chat/stream', { message, history, folderId, libraryRevision }, signal)) {
    if (event.type === 'delta') onDelta(event.text)
    if (event.type === 'error') {
      if (event.accessChanged) window.dispatchEvent(new Event('agentrag:access-changed'))
      throw new Error(event.error)
    }
    if (event.type === 'completed') return
  }
  throw new Error('The connection ended before the answer was complete. Please try again.')
}

export type WikiRunMode = 'incremental' | 'full' | 'lint'
export type WikiRunStatus = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled'
export type WikiPageKind = 'overview' | 'feature' | 'concept' | 'rule' | 'module' | 'component' | 'data-entity' | 'api' | 'source'
export type WikiLinkKind = 'related' | 'part-of' | 'depends-on' | 'implements' | 'mentions'
export interface WikiRunStats {
  sourcesAdded?: number; sourcesChanged?: number; sourcesRemoved?: number; sourcesExcluded?: number; sourcesExtracted?: number; sourcesFailed?: number
  pagesCreated?: number; pagesUpdated?: number; pagesRemoved?: number; pagesFailed?: number; pages?: number; links?: number
  implementationLinks?: number; pagesLinkedToCode?: number; withoutImplementation?: string[]; brokenLinks?: number; orphanPages?: string[]
  unresolvedCitations?: number; inputTokens?: number; outputTokens?: number
  /** Seconds spent in each stage, keyed by stage name. */
  stageSeconds?: Record<string, number>
}
export interface WikiRun {
  id: number; mode: WikiRunMode; status: WikiRunStatus; stage: string; completed: number; total: number
  createdUtc: string; startedUtc: string | null; finishedUtc: string | null; error: string | null; stats: WikiRunStats | null
  /** When the current stage started, and how many items were already done then: together they give its pace. */
  stageStartedUtc?: string | null; stageStartCompleted?: number
  /** Server time of the answer, so elapsed times do not depend on the browser's clock. */
  now?: string | null
}
export interface Wiki {
  id: number; folderId: number; folder: string; language: string; guidelines: string | null; pages: number
  canRefresh: boolean; canDelete: boolean; lastRun: WikiRun | null; refreshedUtc: string | null
}
export interface WikiPageSummary { slug: string; title: string; kind: WikiPageKind; summary: string; stale: boolean; updatedUtc: string }
export interface WikiLink { slug: string; title: string; kind: WikiPageKind; link: WikiLinkKind; note: string | null }
export interface WikiSource { documentId: number | null; title: string; repositoryId: number | null; path: string | null }
export interface WikiPageDetail {
  wikiId: number; slug: string; title: string; kind: WikiPageKind; aliases: string[]; summary: string; content: string
  stale: boolean; updatedUtc: string; links: WikiLink[]; backlinks: WikiLink[]; sources: WikiSource[]
}
export interface WikiPageHit { slug: string; title: string; kind: WikiPageKind; summary: string; match: 'literal' | 'semantic'; excerpt: string | null }
export interface WikiGraphNode { id: string; label: string; kind: string; summary: string | null; degree: number }
export interface WikiGraphEdge { source: string; target: string; kind: string }
export interface WikiGraph { nodes: WikiGraphNode[]; edges: WikiGraphEdge[]; truncated: boolean }
export interface WikiStatus { pages: number; stalePages: number; sources: number; pendingSources: number; excludedSources: number }
export type WikiRunEvent = { type: 'progress' | 'completed'; run: WikiRun } | { type: 'heartbeat'; run?: WikiRun | null }
export const wikiRunActive = (run?: WikiRun | null) => run?.status === 'queued' || run?.status === 'running'

const wikiPath = (id: number, rest = '') => `/api/wikis/${id}${rest}`
const query = (params: Record<string, string | number | boolean | undefined | null>) => {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  const text = search.toString()
  return text ? '?' + text : ''
}
export const listWikis = (signal?: AbortSignal) => request<Wiki[]>('/api/wikis', { signal })
export const getWiki = (id: number, signal?: AbortSignal) => request<Wiki>(wikiPath(id), { signal })
export const createWiki = (folderId: number, language?: string, guidelines?: string) =>
  request<Wiki>('/api/wikis', { method: 'POST', body: JSON.stringify({ folderId, language, guidelines }) })
export const updateWiki = (id: number, changes: { language?: string; guidelines?: string }) =>
  request<Wiki>(wikiPath(id), { method: 'PATCH', body: JSON.stringify(changes) })
export const deleteWiki = (id: number) => request<void>(wikiPath(id), { method: 'DELETE' })
export const getWikiStatus = (id: number, signal?: AbortSignal) => request<WikiStatus>(wikiPath(id, '/status'), { signal })
export const startWikiRun = (id: number, mode?: WikiRunMode) => request<WikiRun>(wikiPath(id, '/runs'), { method: 'POST', body: JSON.stringify({ mode }) })
export const listWikiRuns = (id: number, signal?: AbortSignal) => request<WikiRun[]>(wikiPath(id, '/runs'), { signal })
export const cancelWikiRun = (id: number, runId: number) => request<WikiRun>(wikiPath(id, `/runs/${runId}/cancel`), { method: 'POST' })
export const listWikiPages = (id: number, filter: { kind?: string; q?: string } = {}, signal?: AbortSignal) => request<WikiPageSummary[]>(wikiPath(id, '/pages' + query(filter)), { signal })
export const getWikiPage = (id: number, slug: string, signal?: AbortSignal) => request<WikiPageDetail>(wikiPath(id, `/pages/${encodeURIComponent(slug)}`), { signal })
export const searchWiki = (id: number, q: string, top?: number, signal?: AbortSignal) => request<WikiPageHit[]>(wikiPath(id, '/search' + query({ q, top })), { signal })
export const getWikiGraph = (id: number, options: { center?: string; depth?: number; kinds?: string[]; includeSources?: boolean } = {}, signal?: AbortSignal) =>
  request<WikiGraph>(wikiPath(id, '/graph' + query({ center: options.center, depth: options.depth, kinds: options.kinds?.join(','), includeSources: options.includeSources })), { signal })
/** The document that currently holds a file of a connected repository. */
export const findWikiFile = (repositoryId: number, path: string, signal?: AbortSignal) =>
  request<{ documentId: number }>('/api/wikis/files' + query({ repositoryId, path }), { signal })

/** Follows a wiki run until it ends, reporting each event. Closing the stream does not stop the run on the server. */
export async function watchWikiRun(wikiId: number, runId: number, onEvent?: (event: WikiRunEvent) => void, signal?: AbortSignal): Promise<WikiRun | undefined> {
  let last: WikiRun | undefined
  for await (const event of streamEvents<WikiRunEvent>(wikiPath(wikiId, `/runs/${runId}/events`), undefined, signal, 'GET')) {
    if (event.type === 'heartbeat') continue
    if (event.run) last = event.run
    onEvent?.(event)
    if (event.type === 'completed') return event.run
  }
  return last
}

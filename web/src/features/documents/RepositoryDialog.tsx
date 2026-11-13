import { useEffect, useRef, useState } from 'react'
import { Button, Dialog, Field, Notice } from '../../components/ui'
import * as api from '../../api'
import type { LibraryAction } from './Library'
import type { LibraryState } from './useLibrary'

/** Connect, synchronise or disconnect a GitHub repository whose files are mirrored into a library folder. */
export function RepositoryDialog({ action, state, onAction, onClose }: {
  action: LibraryAction; state: LibraryState; onAction: (action: LibraryAction) => void; onClose: () => void
}) {
  const repository = state.repositories?.find(r => r.folderId === action.folder?.id)
  const [name, setName] = useState('')
  const [branch, setBranch] = useState('')
  const [token, setToken] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [progress, setProgress] = useState<api.SyncProgress | null>(null)
  const [summary, setSummary] = useState<api.SyncSummary | null>(null)
  const running = useRef<AbortController | null>(null)
  useEffect(() => () => running.current?.abort(), [])

  // Closing the dialog stops a running synchronisation; files already saved are kept.
  const close = () => { running.current?.abort(); onClose() }
  const errorView = error && <Notice>{error}</Notice>

  async function connect() {
    setBusy(true); setError('')
    try {
      const created = await api.connectRepository(name.trim(), token.trim(), branch.trim(), action.parentId ?? null)
      await state.refresh()
      onAction({ kind: 'sync', folder: { id: created.folderId } as api.Folder })
    } catch (e) { setError(api.errorMessage(e)); setBusy(false) }
  }

  async function synchronise(id: number) {
    const controller = new AbortController()
    running.current = controller
    setBusy(true); setError(''); setSummary(null); setProgress({ stage: 'listing', completed: 0, total: 0 })
    try {
      setSummary(await api.syncRepository(id, setProgress, controller.signal))
    } catch (e) { if (!controller.signal.aborted) setError(api.errorMessage(e)) }
    finally {
      if (!controller.signal.aborted) { setBusy(false); setProgress(null) }
      await state.refresh()
    }
  }

  async function run(operation: () => Promise<unknown>) {
    setBusy(true); setError('')
    try { await operation(); await state.refresh(); onClose() }
    catch (e) { setError(api.errorMessage(e)); setBusy(false) }
  }

  if (action.kind === 'connect') return <Dialog title="Connect a GitHub repository" description="Its text files become searchable documents in a new folder. Source code is searched by exact text; documentation files are also indexed for semantic search." onClose={close} dismissible={!busy}>
    <form className="space-y-5" onSubmit={e => { e.preventDefault(); void connect() }}>
      <p className="break-words text-xs text-muted">Location: {state.folders.find(f => f.id === action.parentId)?.path ?? 'My library · Private'}</p>
      <Field label="Repository" hint="owner/name or its github.com address" autoFocus required maxLength={300} value={name} onChange={e => setName(e.target.value)} disabled={busy} />
      <Field label="Branch" hint="Leave empty for the default branch" maxLength={255} value={branch} onChange={e => setBranch(e.target.value)} disabled={busy} />
      <Field label="Access token" hint="A fine-grained GitHub token limited to this repository, with read-only access to Contents. It is stored encrypted and never shown again." type="password" autoComplete="off" required maxLength={500} value={token} onChange={e => setToken(e.target.value)} disabled={busy} />
      {errorView}
      <div className="flex justify-end gap-2"><Button variant="secondary" onClick={close} disabled={busy}>Cancel</Button><Button type="submit" busy={busy} disabled={!name.trim() || !token.trim()}>Connect</Button></div>
    </form>
  </Dialog>

  if (!repository) return <Dialog title="Repository unavailable" onClose={onClose}><p className="text-sm leading-6 text-muted">This repository was disconnected or your access changed.</p><Button className="mt-5" onClick={onClose}>Close</Button></Dialog>

  if (action.kind === 'disconnect') return <Dialog title={`Disconnect ${repository.repository}?`} onClose={close} dismissible={!busy}>
    <p className="mb-5 text-sm leading-6 text-muted">The {repository.documents} files synchronised from this repository and its stored access token will be removed from AgentRAG. The folder stays, and nothing changes on GitHub. This cannot be undone.</p>
    {errorView}<div className="mt-5 flex justify-end gap-2"><Button variant="secondary" onClick={close} disabled={busy}>Cancel</Button><Button variant="danger" busy={busy} onClick={() => void run(() => api.disconnectRepository(repository.id))}>Disconnect</Button></div>
  </Dialog>

  if (action.kind === 'token') return <Dialog title={`Replace the token for ${repository.repository}`} onClose={close} dismissible={!busy}>
    <form className="space-y-5" onSubmit={e => { e.preventDefault(); void run(() => api.replaceRepositoryToken(repository.id, token.trim())) }}>
      <Field label="New access token" hint="Read-only access to Contents is enough. The token is checked against the repository before it replaces the current one." type="password" autoComplete="off" autoFocus required maxLength={500} value={token} onChange={e => setToken(e.target.value)} disabled={busy} />
      {errorView}
      <div className="flex justify-end gap-2"><Button variant="secondary" onClick={close} disabled={busy}>Cancel</Button><Button type="submit" busy={busy} disabled={!token.trim()}>Replace token</Button></div>
    </form>
  </Dialog>

  return <Dialog title={`Synchronise ${repository.repository}`} description={`Branch ${repository.branch} · ${api.repositoryStatus(repository)}`} onClose={close}>
    <p className="text-sm leading-6 text-muted">Only files changed since the last synchronisation are fetched. The first one downloads the whole repository and can take a few minutes.</p>
    {progress && <p role="status" className="mt-4 break-words text-sm leading-6">{progress.stage === 'listing' ? 'Listing repository files…' : `Fetching file ${Math.min(progress.completed + 1, progress.total)} of ${progress.total}`}{progress.path && <><br /><span className="text-xs text-muted">{progress.path}</span></>}</p>}
    {summary && <p role="status" className="mt-4 text-sm leading-6">Up to date at commit {summary.commit.slice(0, 7)}: {summary.added} added, {summary.updated} updated, {summary.removed} removed, {summary.unchanged} unchanged{summary.skipped > 0 && `, ${summary.skipped} skipped (binary)`}.</p>}
    {!busy && !summary && repository.lastError && !error && <div className="mt-4"><Notice>Last attempt: {repository.lastError}</Notice></div>}
    {error && <div className="mt-4">{errorView}</div>}
    <div className="mt-5 flex justify-end gap-2"><Button variant="secondary" onClick={close}>{busy ? 'Stop' : 'Close'}</Button>{!summary && <Button busy={busy} onClick={() => void synchronise(repository.id)}>Synchronise now</Button>}</div>
  </Dialog>
}

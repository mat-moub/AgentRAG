import { useEffect, useRef, useState } from 'react'
import type { SubmitEvent } from 'react'
import { ArrowClockwiseIcon, FileTextIcon, PlusIcon, TrashIcon } from '@phosphor-icons/react'
import { Button, Dialog, Field, IconButton, Notice } from '../../components/ui'
import { errorMessage } from '../../api'
import type { IngestionProgress } from '../../api'
import type { DocumentsState } from './useDocuments'
import { IngestionProgress as IngestionProgressView } from './IngestionProgress'
import { createIngestionTiming, recordIngestionProgress } from './ingestionTiming'

export function DocumentList({ state, onAdd }: { state: DocumentsState; onAdd: () => void }) {
  const [confirmId, setConfirmId] = useState<number | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState('')

  async function remove(id: number) {
    if (deleting) return
    setDeleting(true); setError('')
    try { await state.remove(id); setConfirmId(null) }
    catch (e) { setError(errorMessage(e)) }
    finally { setDeleting(false) }
  }

  return <div className="flex min-h-0 flex-col">
    <div className="mb-3 flex items-center justify-between gap-2">
      <h2 className="text-sm font-semibold">Your documents <span className="ml-1 text-xs font-normal text-muted">{state.documents.length}</span></h2>
      <IconButton label="Refresh documents" disabled={state.loading || deleting} onClick={() => void state.refresh()}><ArrowClockwiseIcon size={17} className={state.loading ? 'motion-safe:animate-spin' : ''} /></IconButton>
    </div>
    {state.error && <div className="mb-4"><Notice>{state.error}</Notice></div>}
    {error && <div className="mb-4"><Notice>{error}</Notice></div>}
    {state.loading ? <p role="status" className="py-4 text-sm text-muted">Loading documents…</p>
      : state.documents.length ? <ul className="divide-y divide-line">
        {state.documents.map(document => <li key={document.id} className="py-3">
          <div className="flex items-center gap-2">
            <FileTextIcon size={20} className="shrink-0 text-muted" aria-hidden />
            <div className="min-w-0 flex-1"><p className="truncate text-sm" title={document.title}>{document.title}</p><p className="mt-0.5 text-xs text-muted">{document.chunks} {document.chunks === 1 ? 'passage' : 'passages'}</p></div>
            <IconButton label={`Delete ${document.title}`} disabled={deleting} onClick={() => { setConfirmId(document.id); setError('') }}><TrashIcon size={17} /></IconButton>
          </div>
          {confirmId === document.id && <div className="mt-2 space-y-2 rounded-control bg-subtle p-3">
            <p className="text-xs leading-5">Delete this document and its passages? This cannot be undone.</p>
            <div className="flex gap-2"><Button variant="danger" busy={deleting} onClick={() => void remove(document.id)}>Delete</Button><Button variant="ghost" disabled={deleting} onClick={() => setConfirmId(null)}>Cancel</Button></div>
          </div>}
        </li>)}
      </ul> : !state.error && <p className="py-4 text-sm leading-6 text-muted">A home for your knowledge.<br />Add a document to get started.</p>}
    <Button variant="ghost" className="mt-4 w-full justify-start rounded-none border-0 border-t border-line px-0" onClick={onAdd} disabled={state.loading || !!state.error}><PlusIcon size={19} aria-hidden />Add a document</Button>
  </div>
}

export function AddDocument({ onClose, onAdd }: { onClose: () => void; onAdd: DocumentsState['add'] }) {
  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [progress, setProgress] = useState<IngestionProgress | null>(null)
  const [startedAt, setStartedAt] = useState(0)
  const [now, setNow] = useState(0)
  const [advance, setAdvance] = useState({ count: 0, added: 0 })
  const [timing, setTiming] = useState(createIngestionTiming)
  const completed = useRef(0)
  const request = useRef<AbortController | null>(null)
  useEffect(() => () => request.current?.abort(), [])
  useEffect(() => {
    if (!busy) return
    const preventUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', preventUnload)
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => {
      window.clearInterval(timer)
      window.removeEventListener('beforeunload', preventUnload)
    }
  }, [busy])

  function reportProgress(next: IngestionProgress) {
    const receivedAt = Date.now()
    setTiming(previous => {
      const updated = { ...previous, waits: previous.waits.map(wait => ({ ...wait })) }
      recordIngestionProgress(updated, next, receivedAt)
      return updated
    })
    if (next.completedChunks > completed.current) {
      const added = next.completedChunks - completed.current
      setAdvance(previous => ({ count: previous.count + 1, added }))
      completed.current = next.completedChunks
    }
    setProgress(next)
    setNow(receivedAt)
  }

  async function submit(e: SubmitEvent<HTMLFormElement>) {
    e.preventDefault()
    if (busy || !title.trim() || !content.trim()) return
    setBusy(true); setError('')
    setProgress(null); setStartedAt(Date.now()); setNow(Date.now())
    setTiming(createIngestionTiming())
    completed.current = 0
    setAdvance({ count: 0, added: 0 })
    request.current = new AbortController()
    try { await onAdd(title.trim(), content.trim(), reportProgress, request.current.signal); onClose() }
    catch (e) { setError(errorMessage(e)); setBusy(false) }
  }

  return <Dialog title={busy ? 'Adding your document' : 'Add a document'} description={busy ? undefined : 'Give it a title and paste the text you want to research.'} dismissible={!busy} onClose={() => { if (!busy) onClose() }}>
    {busy && <IngestionProgressView title={title} progress={progress} timing={timing} now={now} startedAt={startedAt} advance={advance} />}
    <form onSubmit={submit} className="space-y-5" hidden={busy}>
      <Field label="Document title" placeholder="e.g. Product guide" autoFocus required value={title} onChange={e => setTitle(e.target.value)} disabled={busy} />
      <div className="space-y-2"><label htmlFor="document-content" className="block text-sm font-medium">Content</label><textarea id="document-content" className="field min-h-48 resize-y" rows={8} placeholder="Paste your document text here…" required value={content} onChange={e => setContent(e.target.value)} disabled={busy} /><p className="text-xs leading-5 text-muted">Text is split into searchable passages when added.</p></div>
      {error && <Notice>{error}</Notice>}
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end"><Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button><Button type="submit" busy={busy} disabled={!title.trim() || !content.trim()}>Add document</Button></div>
    </form>
  </Dialog>
}

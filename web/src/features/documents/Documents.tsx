import { useEffect, useRef, useState } from 'react'
import type { SubmitEvent } from 'react'
import { ArrowClockwiseIcon, FileTextIcon, PlusIcon, TrashIcon, UploadSimpleIcon } from '@phosphor-icons/react'
import { Button, Dialog, Field, IconButton, Notice } from '../../components/ui'
import { FolderSelect } from '../../components/FolderSelect'
import { errorMessage } from '../../api'
import type { Folder, IngestionProgress } from '../../api'
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

export function AddDocument({ onClose, onAdd, folders, initialFolderId = null }: { onClose: () => void; onAdd: DocumentsState['add']; folders?: Folder[]; initialFolderId?: number | null }) {
  const [folderId, setFolderId] = useState<number | null>(initialFolderId)
  const destinationUnavailable = folderId !== null && folders !== undefined && !folders.some(f => f.id === folderId && f.canWrite)
  const [title, setTitle] = useState('')
  const [content, setContent] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [index, setIndex] = useState(true)
  const fileInput = useRef<HTMLInputElement>(null)
  const dragDepth = useRef(0)
  const [dragging, setDragging] = useState(false)
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

  function selectFiles(files: FileList | null) {
    if (busy || !files?.length) return
    setError('')
    if (files.length > 1) {
      setError('Please add one file at a time.')
      return
    }
    const selected = files[0]
    if (!/\.(pdf|txt|md|csv)$/i.test(selected.name) || selected.size === 0 || selected.size > 10 * 1024 * 1024) {
      setError('Choose a non-empty PDF, TXT, Markdown or CSV file, up to 10 MB.')
      return
    }
    setFile(selected)
    if (!title.trim()) setTitle(selected.name.replace(/\.[^.]+$/, ''))
  }

  async function submit(e: SubmitEvent<HTMLFormElement>) {
    e.preventDefault()
    if (busy || destinationUnavailable || !title.trim() || (!file && !content.trim())) return
    setBusy(true); setError('')
    setProgress(null); setStartedAt(Date.now()); setNow(Date.now())
    setTiming(createIngestionTiming())
    completed.current = 0
    setAdvance({ count: 0, added: 0 })
    request.current = new AbortController()
    try {
      await onAdd(title.trim(), file ?? content.trim(), reportProgress, request.current.signal, folders ? folderId : undefined, index)
      onClose()
    }
    catch (e) { setError(errorMessage(e)); setBusy(false) }
  }

  return <Dialog title={busy ? 'Adding your document' : 'Add a document'} description={busy ? undefined : 'Upload a file or paste the text you want to research.'} dismissible={!busy} onClose={() => { if (!busy) onClose() }}>
    {busy && <IngestionProgressView title={title} progress={progress} timing={timing} now={now} startedAt={startedAt} advance={advance} />}
    <form onSubmit={submit} className="space-y-5" hidden={busy}
      onDragEnter={e => {
        if (busy || !e.dataTransfer.types.includes('Files')) return
        e.preventDefault()
        dragDepth.current += 1
        setDragging(true)
      }}
      onDragOver={e => {
        if (!e.dataTransfer.types.includes('Files')) return
        e.preventDefault()
        e.dataTransfer.dropEffect = busy ? 'none' : 'copy'
      }}
      onDragLeave={e => {
        e.preventDefault()
        dragDepth.current = Math.max(0, dragDepth.current - 1)
        if (dragDepth.current === 0) setDragging(false)
      }}
      onDrop={e => {
        if (!e.dataTransfer.types.includes('Files')) return
        e.preventDefault()
        dragDepth.current = 0
        setDragging(false)
        selectFiles(e.dataTransfer.files)
      }}>
      <Field label="Document title" placeholder="e.g. Product guide" autoFocus required value={title} onChange={e => setTitle(e.target.value)} disabled={busy} />
      {folders && <div className="space-y-2"><label htmlFor="document-destination" className="block text-sm font-medium">Destination</label><FolderSelect id="document-destination" label="Destination" folders={folders.filter(f => f.canWrite)} value={folderId} onValueChange={setFolderId} disabled={busy} unavailableLabel="Destination no longer writable" aria-describedby="document-destination-help" /><p id="document-destination-help" className="text-xs leading-5 text-muted">{destinationUnavailable ? 'Your access changed. Choose a new destination to continue.' : folderId && folders.find(f => f.id === folderId)?.shareRootId ? 'Everyone with access to this folder can consult the document.' : 'Only you can access this document.'}</p></div>}
      <div className="space-y-2">
        <p id="document-file-label" className="text-sm font-medium">File</p>
        <input ref={fileInput} id="document-file" type="file" hidden aria-label="File" accept=".pdf,.txt,.md,.csv" disabled={busy} aria-describedby="document-file-help" onChange={e => {
          selectFiles(e.target.files)
          e.target.value = ''
        }} />
        <section aria-labelledby="document-file-label" className={`rounded-control border border-dashed p-4 transition-colors ${dragging ? 'border-accent bg-accent/10 ring-2 ring-accent/20' : 'border-muted/40 bg-surface'}`}>
          <div className="flex flex-col gap-3 min-[480px]:flex-row min-[480px]:items-center">
            <div className="flex min-w-0 flex-1 items-center gap-3">
              {file ? <FileTextIcon size={24} className="shrink-0 text-accent" aria-hidden /> : <UploadSimpleIcon size={24} className="shrink-0 text-muted" aria-hidden />}
              <div className="min-w-0 flex-1" role="status">
                <p className="break-words text-sm font-medium">{dragging ? 'Drop your file to select it' : file ? file.name : 'Drag and drop a file here'}</p>
                <p className="mt-1 text-xs leading-5 text-muted">{file ? `${file.size < 1024 * 1024 ? `${Math.max(1, Math.round(file.size / 1024))} KB` : `${(file.size / (1024 * 1024)).toFixed(1)} MB`} · Ready to add` : 'or select one from your device'}</p>
              </div>
            </div>
            <div className="flex shrink-0 flex-wrap gap-2">
              <Button variant="secondary" disabled={busy} aria-describedby="document-file-help" onClick={() => fileInput.current?.click()}>{file ? 'Choose another file' : 'Choose a file'}</Button>
              {file && <Button variant="ghost" disabled={busy} onClick={() => { setFile(null); setError('') }}>Remove file</Button>}
            </div>
          </div>
        </section>
        <p id="document-file-help" className="text-xs leading-5 text-muted">PDF, TXT, Markdown or CSV · Up to 10 MB. Scanned PDFs need OCR first.</p>
      </div>
      {!file && <div className="space-y-2"><label htmlFor="document-content" className="block text-sm font-medium">Content</label><textarea id="document-content" className="field min-h-32 resize-y" rows={5} placeholder="Paste your document text here…" required value={content} onChange={e => setContent(e.target.value)} disabled={busy} /><p className="text-xs leading-5 text-muted">{index ? 'Text is split into searchable passages when added.' : 'Text is stored for reading only.'}</p></div>}
      <div className="flex items-start gap-3">
        <input id="document-index" type="checkbox" className="mt-1 size-4 shrink-0 accent-primary" checked={index} onChange={e => setIndex(e.target.checked)} disabled={busy} aria-describedby="document-index-help" />
        <div><label htmlFor="document-index" className="text-sm font-medium">Index for search and answers</label><p id="document-index-help" className="text-xs leading-5 text-muted">{index ? 'Creates vector passages used by Conversation and Explore. Large documents take longer to add.' : 'The full text is saved and can be read, but it is not used in answers or Explore.'}</p></div>
      </div>
      {error && <Notice>{error}</Notice>}
      <div className="flex flex-col-reverse gap-2 min-[480px]:flex-row min-[480px]:justify-end"><Button variant="secondary" onClick={onClose} disabled={busy}>Cancel</Button><Button type="submit" busy={busy} disabled={destinationUnavailable || !title.trim() || (!file && !content.trim())}>Add document</Button></div>
    </form>
  </Dialog>
}

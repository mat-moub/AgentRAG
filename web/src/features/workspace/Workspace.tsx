import { useCallback, useEffect, useRef, useState } from 'react'
import { ChatCircleTextIcon, FileTextIcon, FilesIcon, MagnifyingGlassIcon, SignOutIcon } from '@phosphor-icons/react'
import { Brand } from '../../components/Brand'
import { SiteInformationLinks } from '../../components/SiteFooter'
import { Button, Dialog, IconButton } from '../../components/ui'
import { FolderSelect } from '../../components/FolderSelect'
import { Chat } from '../chat/Chat'
import { AddDocument } from '../documents/Documents'
import { DocumentReader } from '../documents/DocumentReader'
import { LibraryManager, LibraryTree } from '../documents/Library'
import type { LibraryAction } from '../documents/Library'
import { useLibrary } from '../documents/useLibrary'
import { Explore } from './Explore'
import { googleLinkUrl } from '../../api'
import type { Doc } from '../../api'

export function Workspace({ onLogout, googleEnabled = false, googleLinked = false }: { onLogout: () => void; googleEnabled?: boolean; googleLinked?: boolean }) {
  const library = useLibrary()
  const [view, setView] = useState<'chat' | 'explore' | 'document'>('chat')
  const previousView = useRef<'chat' | 'explore'>('chat')
  const [exploreOpened, setExploreOpened] = useState(false)
  const [panel, setPanel] = useState<'documents' | 'add' | 'information' | null>(null)
  const [action, setAction] = useState<LibraryAction | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(() => {
    const value = Number(new URLSearchParams(window.location.search).get('folder'))
    return Number.isSafeInteger(value) && value > 0 ? value : null
  })
  const [scopeId, setScopeId] = useState<number | null>(null)
  const [readingDocumentId, setReadingDocumentId] = useState<number | null>(null)
  const [addFolderId, setAddFolderId] = useState<number | null>(null)
  const [scopeChanged, setScopeChanged] = useState(false)
  const returnFocus = useRef<HTMLElement | null>(null)
  const scope = library.folders.find(f => f.id === scopeId)
  const effectiveScopeId = scope?.id
  const scopeLabel = scope?.path ?? 'All accessible sources'
  const scopedFolderIds = new Set<number>()
  if (scope) {
    scopedFolderIds.add(scope.id)
    let changed = true
    while (changed) {
      changed = false
      for (const folder of library.folders) if (folder.parentId !== null && scopedFolderIds.has(folder.parentId) && !scopedFolderIds.has(folder.id)) { scopedFolderIds.add(folder.id); changed = true }
    }
  }
  const documents = scope ? library.documents.filter(d => d.folderId != null && scopedFolderIds.has(d.folderId)) : library.documents
  // Documents stored for reading only have no passages, so they cannot feed answers or Explore.
  const searchable = documents.filter(d => d.indexed !== false)
  const scopeUnavailable = scopeId !== null && !scope
  function rememberFocus() {
    if (action) return
    const active = document.activeElement
    returnFocus.current = active instanceof HTMLElement && !active.closest('dialog') ? active : document.querySelector<HTMLElement>('[aria-label="Open documents"]')
  }
  const restoreFocus = useCallback(() => {
    window.requestAnimationFrame(() => {
      const target = returnFocus.current
      if (target?.isConnected) target.focus()
    })
  }, [])
  const readingDocument = library.documents.find(document => document.id === readingDocumentId)
  const closeDocument = useCallback(() => {
    setReadingDocumentId(null)
    setView(current => current === 'document' ? previousView.current : current)
    restoreFocus()
  }, [restoreFocus])
  // A deleted, moved-out or revoked document closes its page.
  useEffect(() => {
    if (readingDocumentId !== null && !library.loading && !readingDocument) closeDocument()
  }, [readingDocument, readingDocumentId, library.loading, closeDocument])
  function add(id: number | null) { rememberFocus(); setAddFolderId(id); setAction(null); setPanel('add') }
  function manage(next: LibraryAction) { rememberFocus(); setPanel(null); setAction(next) }
  function open(document: Doc) {
    rememberFocus()
    if (view !== 'document') previousView.current = view
    setReadingDocumentId(document.id)
    setView('document')
    setPanel(null)
  }
  const tree = <LibraryTree state={library} selectedId={selectedId} onSelect={setSelectedId} onAction={manage} onAdd={add} onOpen={open} />
  const researchKey = `${effectiveScopeId ?? 'all'}:${library.resetCount}:${scopeUnavailable}`
  return <div className="grid h-dvh min-h-0 grid-cols-1 grid-rows-[auto_minmax(0,1fr)] overflow-hidden lg:grid-cols-[18rem_minmax(0,1fr)]">
    <a href="#research" className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50 focus:bg-canvas focus:p-3">Skip to research</a>
    <header className="flex min-h-20 flex-wrap items-center justify-between gap-x-1 border-b border-line px-3 py-2 sm:px-5 lg:border-r lg:border-b-0 lg:bg-subtle/35 lg:px-4 lg:pt-5 lg:pb-6">
      <Brand />
      <div className="flex items-center">
        <IconButton label="Open documents" className="lg:hidden" onClick={() => setPanel('documents')}><FilesIcon size={22} /></IconButton>
        <IconButton label="About and help" aria-haspopup="dialog" onClick={() => setPanel('information')}><span aria-hidden="true" className="flex size-5 items-center justify-center rounded-full border border-current text-sm font-semibold">?</span></IconButton>
        <IconButton label="Sign out" onClick={onLogout}><SignOutIcon size={21} /></IconButton>
      </div>
    </header>
    <aside className="hidden min-h-0 flex-col border-r border-line bg-subtle/35 lg:row-start-2 lg:flex" aria-label="Knowledge base"><div className="min-h-0 flex-1 overflow-y-auto px-4">{tree}</div></aside>
    <main id="research" className="flex min-h-0 min-w-0 flex-col lg:col-start-2 lg:row-span-2 lg:row-start-1">
      <nav aria-label="Research views" className="flex items-center justify-center gap-1 border-b border-line px-3 py-2 sm:justify-start sm:gap-2 sm:px-10">
        <Button variant={view === 'chat' ? 'primary' : 'ghost'} className="min-w-0 flex-1 gap-1 px-2 text-[11px] whitespace-nowrap sm:flex-none sm:gap-2 sm:px-4 sm:text-sm" aria-pressed={view === 'chat'} onClick={() => setView('chat')}><ChatCircleTextIcon size={18} aria-hidden />Conversation</Button>
        <Button variant={view === 'explore' ? 'primary' : 'ghost'} className="min-w-0 flex-1 gap-1 px-2 text-[11px] whitespace-nowrap sm:flex-none sm:gap-2 sm:px-4 sm:text-sm" aria-pressed={view === 'explore'} onClick={() => { setExploreOpened(true); setView('explore') }}><MagnifyingGlassIcon size={18} aria-hidden />Explore documents</Button>
        {readingDocument && <Button variant={view === 'document' ? 'primary' : 'ghost'} className="min-w-0 flex-1 gap-1 px-2 text-[11px] whitespace-nowrap sm:flex-none sm:gap-2 sm:px-4 sm:text-sm" aria-pressed={view === 'document'} onClick={() => setView('document')}><FileTextIcon size={18} aria-hidden />Document</Button>}
      </nav>
      {view === 'document' && readingDocument && <div className="flex min-h-0 flex-1 animate-enter flex-col"><DocumentReader key={readingDocument.id} document={readingDocument} onClose={closeDocument} /></div>}
      <div className={view === 'document' ? 'hidden' : 'border-b border-line px-5 py-3 sm:px-10'}><div className="flex flex-wrap items-center gap-2"><label htmlFor="source-scope" className="text-xs font-medium text-muted">Sources</label><FolderSelect id="source-scope" label="Sources" className="flex-1 sm:max-w-md" value={scopeId} folders={library.folders} disabled={library.loading || !!library.error} rootLabel="All accessible sources" rootDescription="Your documents and folders shared with you" onValueChange={next => { setScopeId(next); setScopeChanged(true) }} /></div>{(scopeChanged || library.resetCount > 0) && <p role="status" className="mt-2 text-xs leading-5 text-muted">{scopeUnavailable ? 'Your access changed. Choose available sources to continue.' : 'New conversation: the sources or access changed.'}</p>}</div>
      {scopeUnavailable ? <p role="status" className={view === 'document' ? 'hidden' : 'p-5 text-sm text-muted'}>This folder was removed or is no longer shared with you.</p> : <>
        <div className={view === 'chat' ? 'flex min-h-0 flex-1 animate-enter flex-col' : 'hidden'}><Chat key={researchKey} documentCount={searchable.length} documentsReady={!library.loading && !library.error} onAdd={() => add(scope?.canWrite ? scope.id : null)} folderId={effectiveScopeId} libraryRevision={library.revision} scopeLabel={scopeLabel} /></div>
        {exploreOpened && !library.error && !library.loading && <div className={view === 'explore' ? 'flex min-h-0 flex-1 animate-enter flex-col' : 'hidden'}><Explore key={researchKey} documents={searchable} folderId={effectiveScopeId} scopeLabel={scopeLabel} /></div>}
      </>}
    </main>
    {panel === 'information' && <Dialog title="About & help" onClose={() => setPanel(null)}><nav aria-label="Site information" className="flex flex-col gap-1"><SiteInformationLinks className="flex min-h-11 items-center rounded-control px-3 py-2 text-sm underline decoration-line underline-offset-4 hover:bg-subtle hover:text-accent" /></nav>{googleEnabled && !googleLinked && <a href={googleLinkUrl} className="mt-4 flex min-h-11 items-center border-t border-line px-3 pt-4 text-sm underline underline-offset-4">Associate Google</a>}</Dialog>}
    {panel === 'documents' && <Dialog title="Your library" description="Organize the documents behind your answers." onClose={() => setPanel(null)}>{tree}</Dialog>}
    {panel === 'add' && <AddDocument onClose={() => { setPanel(null); restoreFocus() }} onAdd={library.add} folders={library.folders} initialFolderId={addFolderId} />}
    {action && <LibraryManager key={`${action.kind}:${action.folder?.id ?? action.document?.id ?? action.parentId ?? ''}`} action={action} state={library} onAction={manage} onClose={() => { setAction(null); restoreFocus() }} onAdd={add} />}
  </div>
}

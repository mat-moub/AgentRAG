import { useState } from 'react'
import { ArrowSquareOutIcon, ChatCircleTextIcon, FilesIcon, MagnifyingGlassIcon, SignOutIcon } from '@phosphor-icons/react'
import { Brand } from '../../components/Brand'
import { SiteInformationLinks } from '../../components/SiteFooter'
import { Button, Dialog, IconButton } from '../../components/ui'
import { Chat } from '../chat/Chat'
import { AddDocument, DocumentList } from '../documents/Documents'
import { useDocuments } from '../documents/useDocuments'
import { Explore } from './Explore'
import { googleLinkUrl } from '../../api'

export function Workspace({ onLogout, googleEnabled = false, googleLinked = false }: { onLogout: () => void; googleEnabled?: boolean; googleLinked?: boolean }) {
  const documents = useDocuments()
  const [view, setView] = useState<'chat' | 'explore'>('chat')
  const [exploreOpened, setExploreOpened] = useState(false)
  const [panel, setPanel] = useState<'documents' | 'add' | 'information' | null>(null)
  return <div className="flex h-dvh min-h-0 flex-col overflow-hidden lg:flex-row">
    <a href="#research" className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50 focus:bg-canvas focus:p-3">Skip to research</a>
    <aside className="hidden w-72 shrink-0 flex-col border-r border-line bg-subtle/35 lg:flex" aria-label="Knowledge base">
      <div className="px-6 pt-7 pb-8"><Brand /></div>
      <div className="min-h-0 flex-1 overflow-y-auto px-5"><DocumentList state={documents} onAdd={() => setPanel('add')} /></div>
      <div className="mx-5 border-t border-line py-5"><p className="eyebrow mb-2">Your workspace</p><p className="text-xs leading-5 text-muted">A little less noise.<br />A little more knowledge.</p></div>
    </aside>
    <main id="research" className="flex min-h-0 min-w-0 flex-1 flex-col">
      <header className="flex min-h-20 shrink-0 flex-wrap items-center justify-between gap-x-2 gap-y-0 border-b border-line px-3 py-2 sm:gap-3 sm:px-10 sm:py-0">
        <div className="lg:hidden"><Brand /></div>
        <div className="hidden lg:block"><h2 className="text-sm font-semibold">Document research</h2><p className="mt-1 text-xs text-muted">{documents.loading ? 'Loading your knowledge base…' : documents.error ? 'Knowledge base unavailable' : `${documents.documents.length} ${documents.documents.length === 1 ? 'document' : 'documents'} in your knowledge base`}</p></div>
        <div className="flex items-center gap-0 sm:gap-1">{googleEnabled && (googleLinked ? <span className="hidden text-xs text-muted sm:inline">Google connected</span> : <a href={googleLinkUrl} className="hidden min-h-11 content-center px-2 text-xs underline underline-offset-4 sm:inline-block">Associate Google</a>)}<IconButton label="Open documents" className="lg:hidden" onClick={() => setPanel('documents')}><FilesIcon size={22} /></IconButton><IconButton label="About and help" aria-haspopup="dialog" onClick={() => setPanel('information')}><span aria-hidden="true" className="flex size-5 items-center justify-center rounded-full border border-current text-sm font-semibold">?</span></IconButton><IconButton label="Sign out" onClick={onLogout}><SignOutIcon size={21} /></IconButton></div>
        {googleEnabled && <div className="w-full pb-1 text-xs sm:hidden">{googleLinked ? <span className="text-muted">Google connected</span> : <a href={googleLinkUrl} className="inline-flex min-h-9 items-center underline underline-offset-4">Associate Google</a>}</div>}
      </header>
      <div className="flex items-center justify-between border-b border-line px-5 py-2 lg:hidden"><p className="eyebrow">Document research</p><button type="button" onClick={() => setPanel('documents')} className="min-h-9 text-xs text-muted underline decoration-line underline-offset-4">{documents.loading ? 'Loading…' : documents.error ? 'Retry documents' : `${documents.documents.length} ${documents.documents.length === 1 ? 'document' : 'documents'}`}</button></div>
      <nav aria-label="Research views" className="flex items-center justify-center gap-1 border-b border-line px-3 py-2 sm:justify-start sm:gap-2 sm:px-10">
        <Button variant={view === 'chat' ? 'primary' : 'ghost'} className="min-w-0 flex-1 gap-1 px-2 text-[11px] whitespace-nowrap sm:flex-none sm:gap-2 sm:px-4 sm:text-sm" aria-pressed={view === 'chat'} onClick={() => setView('chat')}><ChatCircleTextIcon size={18} aria-hidden />Conversation</Button>
        <Button variant={view === 'explore' ? 'primary' : 'ghost'} className="min-w-0 flex-1 gap-1 px-2 text-[11px] whitespace-nowrap sm:flex-none sm:gap-2 sm:px-4 sm:text-sm" aria-pressed={view === 'explore'} onClick={() => { setExploreOpened(true); setView('explore') }}><MagnifyingGlassIcon size={18} aria-hidden />Explore documents</Button>
      </nav>
      <div className={view === 'chat' ? 'flex min-h-0 flex-1 animate-enter flex-col' : 'hidden'}><Chat documentCount={documents.documents.length} documentsReady={!documents.loading && !documents.error} onAdd={() => setPanel('add')} /></div>
      {exploreOpened && <div className={view === 'explore' ? 'flex min-h-0 flex-1 animate-enter flex-col' : 'hidden'}><Explore key={documents.documents.map(d => `${d.id}:${d.chunks}`).join(',')} documents={documents.documents} /></div>}
    </main>
    {panel === 'information' && <Dialog title="About & help" onClose={() => setPanel(null)}><nav aria-label="Site information" className="flex flex-col gap-1"><SiteInformationLinks className="flex min-h-11 items-center rounded-control px-3 py-2 text-sm underline decoration-line underline-offset-4 hover:bg-subtle hover:text-accent" /></nav></Dialog>}
    {panel === 'documents' && <Dialog title="Your knowledge base" description="The documents behind your answers." onClose={() => setPanel(null)}><DocumentList state={documents} onAdd={() => setPanel('add')} /><Button variant="ghost" className="mt-5 w-full justify-start px-0" onClick={() => window.open('https://github.com/mat-moub/AgentRAG', '_blank', 'noopener,noreferrer')}><ArrowSquareOutIcon size={18} />View the source</Button></Dialog>}
    {panel === 'add' && <AddDocument onClose={() => setPanel(null)} onAdd={documents.add} />}
  </div>
}

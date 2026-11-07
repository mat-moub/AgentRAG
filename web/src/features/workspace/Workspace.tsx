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
    <aside className="hidden min-h-0 flex-col border-r border-line bg-subtle/35 lg:row-start-2 lg:flex" aria-label="Knowledge base">
      <div className="min-h-0 flex-1 overflow-y-auto px-5"><DocumentList state={documents} onAdd={() => setPanel('add')} /></div>
    </aside>
    <main id="research" className="flex min-h-0 min-w-0 flex-col lg:col-start-2 lg:row-span-2 lg:row-start-1">
      <nav aria-label="Research views" className="flex items-center justify-center gap-1 border-b border-line px-3 py-2 sm:justify-start sm:gap-2 sm:px-10">
        <Button variant={view === 'chat' ? 'primary' : 'ghost'} className="min-w-0 flex-1 gap-1 px-2 text-[11px] whitespace-nowrap sm:flex-none sm:gap-2 sm:px-4 sm:text-sm" aria-pressed={view === 'chat'} onClick={() => setView('chat')}><ChatCircleTextIcon size={18} aria-hidden />Conversation</Button>
        <Button variant={view === 'explore' ? 'primary' : 'ghost'} className="min-w-0 flex-1 gap-1 px-2 text-[11px] whitespace-nowrap sm:flex-none sm:gap-2 sm:px-4 sm:text-sm" aria-pressed={view === 'explore'} onClick={() => { setExploreOpened(true); setView('explore') }}><MagnifyingGlassIcon size={18} aria-hidden />Explore documents</Button>
      </nav>
      <div className={view === 'chat' ? 'flex min-h-0 flex-1 animate-enter flex-col' : 'hidden'}><Chat documentCount={documents.documents.length} documentsReady={!documents.loading && !documents.error} onAdd={() => setPanel('add')} /></div>
      {exploreOpened && <div className={view === 'explore' ? 'flex min-h-0 flex-1 animate-enter flex-col' : 'hidden'}><Explore key={documents.documents.map(d => `${d.id}:${d.chunks}`).join(',')} documents={documents.documents} /></div>}
    </main>
    {panel === 'information' && <Dialog title="About & help" onClose={() => setPanel(null)}><nav aria-label="Site information" className="flex flex-col gap-1"><SiteInformationLinks className="flex min-h-11 items-center rounded-control px-3 py-2 text-sm underline decoration-line underline-offset-4 hover:bg-subtle hover:text-accent" /></nav>{googleEnabled && !googleLinked && <a href={googleLinkUrl} className="mt-4 flex min-h-11 items-center border-t border-line px-3 pt-4 text-sm underline underline-offset-4">Associate Google</a>}</Dialog>}
    {panel === 'documents' && <Dialog title="Your knowledge base" description="The documents behind your answers." onClose={() => setPanel(null)}><DocumentList state={documents} onAdd={() => setPanel('add')} /><Button variant="ghost" className="mt-5 w-full justify-start px-0" onClick={() => window.open('https://github.com/mat-moub/AgentRAG', '_blank', 'noopener,noreferrer')}><ArrowSquareOutIcon size={18} />View the source</Button></Dialog>}
    {panel === 'add' && <AddDocument onClose={() => setPanel(null)} onAdd={documents.add} />}
  </div>
}

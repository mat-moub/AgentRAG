import { useState } from 'react'
import { ArrowSquareOutIcon, FilesIcon, SignOutIcon } from '@phosphor-icons/react'
import { Brand } from '../../components/Brand'
import { Button, Dialog, IconButton } from '../../components/ui'
import { Chat } from '../chat/Chat'
import { AddDocument, DocumentList } from '../documents/Documents'
import { useDocuments } from '../documents/useDocuments'

export function Workspace({ onLogout }: { onLogout: () => void }) {
  const documents = useDocuments()
  const [panel, setPanel] = useState<'documents' | 'add' | null>(null)
  return <div className="flex h-dvh min-h-0 flex-col overflow-hidden lg:flex-row">
    <a href="#research" className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50 focus:bg-canvas focus:p-3">Skip to research</a>
    <aside className="hidden w-72 shrink-0 flex-col border-r border-line bg-subtle/35 lg:flex" aria-label="Knowledge base">
      <div className="px-6 pt-7 pb-8"><Brand /></div>
      <div className="min-h-0 flex-1 overflow-y-auto px-5"><DocumentList state={documents} onAdd={() => setPanel('add')} /></div>
      <div className="mx-5 border-t border-line py-5"><p className="eyebrow mb-2">Your workspace</p><p className="text-xs leading-5 text-muted">A little less noise.<br />A little more knowledge.</p></div>
    </aside>
    <main id="research" className="flex min-h-0 min-w-0 flex-1 flex-col">
      <header className="flex min-h-20 shrink-0 items-center justify-between gap-3 border-b border-line px-5 sm:px-10">
        <div className="lg:hidden"><Brand /></div>
        <div className="hidden lg:block"><h2 className="text-sm font-semibold">Document research</h2><p className="mt-1 text-xs text-muted">{documents.loading ? 'Loading your knowledge base…' : documents.error ? 'Knowledge base unavailable' : `${documents.documents.length} ${documents.documents.length === 1 ? 'document' : 'documents'} in your knowledge base`}</p></div>
        <div className="flex items-center gap-1"><IconButton label="Open documents" className="lg:hidden" onClick={() => setPanel('documents')}><FilesIcon size={22} /></IconButton><IconButton label="Sign out" onClick={onLogout}><SignOutIcon size={21} /></IconButton></div>
      </header>
      <div className="flex items-center justify-between border-b border-line px-5 py-2 lg:hidden"><p className="eyebrow">Document research</p><button type="button" onClick={() => setPanel('documents')} className="min-h-9 text-xs text-muted underline decoration-line underline-offset-4">{documents.loading ? 'Loading…' : documents.error ? 'Retry documents' : `${documents.documents.length} ${documents.documents.length === 1 ? 'document' : 'documents'}`}</button></div>
      <Chat documentCount={documents.documents.length} documentsReady={!documents.loading && !documents.error} onAdd={() => setPanel('add')} />
    </main>
    {panel === 'documents' && <Dialog title="Your knowledge base" description="The documents behind your answers." onClose={() => setPanel(null)}><DocumentList state={documents} onAdd={() => setPanel('add')} /><Button variant="ghost" className="mt-5 w-full justify-start px-0" onClick={() => window.open('https://github.com/mat-moub/AgentRAG', '_blank', 'noopener,noreferrer')}><ArrowSquareOutIcon size={18} />View the source</Button></Dialog>}
    {panel === 'add' && <AddDocument onClose={() => setPanel(null)} onAdd={documents.add} />}
  </div>
}

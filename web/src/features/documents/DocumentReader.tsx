import { memo, useEffect, useRef, useState } from 'react'
import { ArrowLeftIcon } from '@phosphor-icons/react'
import { Button, Notice } from '../../components/ui'
import { errorMessage, streamDocumentContent } from '../../api'
import type { Doc } from '../../api'

/** Each streamed page is its own node, so appending a page never re-renders the text already shown. */
const Segment = memo(function Segment({ text }: { text: string }) { return <span>{text}</span> })

export function DocumentReader({ document, onClose }: { document: Doc; onClose: () => void }) {
  const [segments, setSegments] = useState<string[]>([])
  const [info, setInfo] = useState<{ title: string; chunks: number; indexed: boolean } | null>(null)
  const [status, setStatus] = useState<'loading' | 'done' | 'error'>('loading')
  const [error, setError] = useState('')
  const [attempt, setAttempt] = useState(0)
  const heading = useRef<HTMLHeadingElement>(null)

  useEffect(() => { heading.current?.focus() }, [document.id])

  useEffect(() => {
    const controller = new AbortController()
    // Defer the request so React's development-only setup/cleanup cycle does not start an immediately aborted stream.
    const timer = window.setTimeout(() => {
      streamDocumentContent(document.id, {
        onStart: next => { if (!controller.signal.aborted) setInfo(next) },
        onText: text => { if (!controller.signal.aborted && text) setSegments(previous => [...previous, text]) },
      }, controller.signal)
        .then(() => { if (!controller.signal.aborted) setStatus('done') })
        .catch(reason => { if (!controller.signal.aborted) { setError(errorMessage(reason)); setStatus('error') } })
    }, 0)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [document.id, attempt])

  const chunks = info?.chunks ?? document.chunks
  const indexed = info?.indexed ?? document.indexed !== false
  return <section aria-labelledby="document-reader-title" className="flex min-h-0 flex-1 flex-col">
    <header className="flex flex-wrap items-start justify-between gap-3 border-b border-line px-5 py-4 sm:px-10">
      <div className="min-w-0">
        <h2 id="document-reader-title" ref={heading} tabIndex={-1} className="truncate text-lg font-semibold tracking-tight outline-none">{info?.title ?? document.title}</h2>
        <p className="mt-1 text-xs text-muted">Extracted text · {indexed ? `${chunks} searchable ${chunks === 1 ? 'passage' : 'passages'}` : 'Not indexed, reading only'}</p>
      </div>
      <Button variant="ghost" className="gap-2" onClick={onClose}><ArrowLeftIcon size={18} aria-hidden />Close document</Button>
    </header>
    <div className="min-h-0 flex-1 overflow-y-auto px-5 py-6 sm:px-10" tabIndex={0} role="region" aria-label="Document content" aria-busy={status === 'loading'}>
      <div className="mx-auto max-w-3xl">
        {status === 'error' && <div className="mb-4 space-y-4"><Notice>{error}</Notice><Button variant="secondary" onClick={() => { setSegments([]); setInfo(null); setStatus('loading'); setError(''); setAttempt(value => value + 1) }}>Try again</Button></div>}
        {segments.length > 0 && <p className="break-words whitespace-pre-wrap text-sm leading-7">{segments.map((text, index) => <Segment key={index} text={text} />)}</p>}
        {status === 'loading' && <p role="status" className="py-8 text-center text-sm text-muted">{segments.length ? 'Loading the rest of the document…' : 'Loading document…'}</p>}
        {status === 'done' && segments.length === 0 && <p className="py-8 text-center text-sm text-muted">This document has no extracted text.</p>}
      </div>
    </div>
  </section>
}

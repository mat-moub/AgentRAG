import { useEffect, useRef, useState } from 'react'
import { MagnifyingGlassIcon } from '@phosphor-icons/react'
import { Button, Notice } from '../../components/ui'
import { errorMessage, explorePassages } from '../../api'
import type { Doc, Passage } from '../../api'

function Highlight({ text, query }: { text: string; query: string }) {
  if (!query) return <>{text}</>
  const start = text.toLowerCase().indexOf(query.toLowerCase())
  if (start < 0) return <>{text}</>
  return <>{text.slice(0, start)}<mark className="rounded bg-primary/15 px-0.5 text-ink">{text.slice(start, start + query.length)}</mark><Highlight text={text.slice(start + query.length)} query={query} /></>
}

export function Explore({ documents }: { documents: Doc[] }) {
  const [query, setQuery] = useState('')
  const [searched, setSearched] = useState('')
  const [documentId, setDocumentId] = useState('')
  const [passages, setPassages] = useState<Passage[]>([])
  const [total, setTotal] = useState(0)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState('')
  const request = useRef<AbortController | null>(null)
  async function search(text: string, filter: string) {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    setBusy(true); setError('')
    try {
      const result = await explorePassages(text, filter ? Number(filter) : undefined, controller.signal)
      if (controller.signal.aborted) return
      setPassages(result.passages); setTotal(result.total); setSearched(text.trim())
      setSelectedId([...result.passages].sort((a, b) => (b.score ?? 0) - (a.score ?? 0))[0]?.id ?? null)
    } catch (e) { if (!controller.signal.aborted) setError(errorMessage(e)) }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  // Initial synchronization with the API uses the same cancellable loader as user searches.
  // oxlint-disable-next-line react/set-state-in-effect
  useEffect(() => { void search('', ''); return () => request.current?.abort() }, [])
  const sorted = [...passages].sort((a, b) => (b.score ?? 0) - (a.score ?? 0))
  const selected = passages.find(p => p.id === selectedId)
  const xs = passages.map(p => p.x), ys = passages.map(p => p.y)
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys)
  const score = (p: Passage) => p.score == null ? `Passage ${p.index + 1}` : `Similarity ${p.score.toFixed(2)}`
  return <section className="min-h-0 flex-1 overflow-y-auto px-5 py-8 sm:px-10" aria-labelledby="explore-title">
    <h1 id="explore-title" className="font-display text-4xl tracking-tight">Explore your knowledge</h1>
    <p className="mt-2 text-sm leading-6 text-muted">Find and compare passages by exact phrase and semantic meaning.</p>
    <form className="my-7 flex flex-wrap gap-3" onSubmit={e => { e.preventDefault(); void search(query, documentId) }}>
      <div className="relative min-w-48 flex-1"><MagnifyingGlassIcon size={20} className="absolute top-4 left-3.5 text-muted" aria-hidden /><input aria-label="Search passages" className="field pl-11" placeholder="Search across your documents…" value={query} maxLength={2000} onChange={e => setQuery(e.target.value)} /></div>
      <select className="field w-auto max-w-full" aria-label="Filter by document" value={documentId} onChange={e => { setDocumentId(e.target.value); void search(query, e.target.value) }}><option value="">All documents</option>{documents.map(d => <option key={d.id} value={d.id}>{d.title}</option>)}</select>
      <Button type="submit" busy={busy}>Search</Button>
    </form>
    {error && <Notice>{error}</Notice>}
    {busy ? <p role="status" className="py-12 text-muted">Loading passage map…</p> : !error && !passages.length ? <div className="py-12"><h2 className="text-lg font-semibold">No indexed passages yet</h2><p className="mt-2 text-sm text-muted">Add a document or choose another document to explore.</p></div> : !error && <>
      {total > passages.length && <p className="mb-4 text-sm text-muted">Showing the first {passages.length} of {total} passages. Search and scores apply to this sample; filter by document to narrow it.</p>}
      <div className="grid gap-8 xl:grid-cols-[minmax(0,1.35fr)_minmax(0,1fr)]">
        <div className="min-w-0">
          <h2 className="font-display text-2xl">{searched ? <>Results for “{searched}”</> : 'Indexed passages'}</h2>
          <p className="mt-2 text-xs text-muted">{passages.length} passages{searched && ` · ${passages.filter(p => p.exact).length} exact phrase matches · sorted by similarity`}</p>
          <div className="mt-5 max-h-[65vh] overflow-y-auto" aria-label="Passage results">{sorted.map(p => <button key={p.id} type="button" onClick={() => setSelectedId(p.id)} aria-pressed={selectedId === p.id} className={`block w-full border-b border-line p-4 text-left transition-colors hover:bg-subtle ${selectedId === p.id ? 'rounded-control bg-primary/5 ring-1 ring-inset ring-primary' : ''}`}>
            <div className="mb-3 flex flex-wrap items-center gap-2 text-xs text-muted">{searched && <span className={`rounded px-2 py-1 font-medium ${p.exact ? 'bg-primary/10 text-primary' : 'bg-secondary/10 text-secondary'}`}>{p.exact ? 'Exact phrase' : 'Semantic similarity'}</span>}<span className="break-words">{p.title}</span><span className="ml-auto">{score(p)}</span></div>
            <p className="line-clamp-3 text-sm leading-7"><Highlight text={p.content} query={searched} /></p>
          </button>)}</div>
        </div>
        <div className="min-w-0 xl:border-l xl:border-line xl:pl-7">
          <h2 className="font-display text-2xl">Passage map</h2><p className="mt-2 text-xs leading-5 text-muted">Select a point to read its passage.</p>
          <div className="relative my-5 h-80 border-b border-l border-line bg-surface" role="group" aria-label="Two-dimensional passage map">
            {passages.map(p => <button key={p.id} type="button" onClick={() => setSelectedId(p.id)} aria-label={`${p.title}, passage ${p.index + 1}, ${score(p)}`} aria-pressed={selectedId === p.id} style={{ left: `${8 + (p.x - minX) / (maxX - minX || 1) * 84}%`, bottom: `${8 + (p.y - minY) / (maxY - minY || 1) * 84}%` }} className={`absolute flex size-7 -translate-x-1/2 translate-y-1/2 items-center justify-center rounded-full focus:z-10 ${selectedId === p.id ? 'z-10 ring-2 ring-primary' : ''}`}><span className={`size-2.5 rounded-full ${p.exact ? 'bg-primary' : searched ? 'bg-secondary' : 'bg-tertiary'}`} /></button>)}
          </div>
          <div className="flex flex-wrap gap-4 text-xs text-muted"><span><span className="mr-2 inline-block size-2 rounded-full bg-primary" />Exact phrase</span><span><span className="mr-2 inline-block size-2 rounded-full bg-secondary" />Semantic similarity</span><span>◎ Selected passage</span></div>
          <p className="mt-4 text-xs leading-5 text-muted">2D PCA projection of passage vectors. Distances are approximate; similarity uses the full vectors. Similarity is not a confidence score.</p>
          {selected && <article className="mt-7 border-t border-line pt-5"><h3 className="font-display text-xl">Selected excerpt</h3><p className="my-3 break-words text-xs text-muted">{selected.title} · Passage {selected.index + 1} · {score(selected)}</p><p className="whitespace-pre-wrap text-sm leading-7"><Highlight text={selected.content} query={searched} /></p></article>}
        </div>
      </div>
    </>}
  </section>
}

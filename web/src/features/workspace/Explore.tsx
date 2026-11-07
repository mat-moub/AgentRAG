import { useEffect, useRef, useState } from 'react'
import { MagnifyingGlassIcon } from '@phosphor-icons/react'
import { Button, Notice } from '../../components/ui'
import { errorMessage, explorePassages } from '../../api'
import type { Doc, Passage } from '../../api'

const documentColors = ['#32649b', '#388779', '#854899', '#c05d3c', '#c28a2c', '#356d78', '#8b5572', '#657532']

function similarityOpacity(score: number | null) {
  return score == null || !Number.isFinite(score) ? 1 : 0.35 + 0.65 * Math.max(0, Math.min(1, score))
}

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
  const loadedFilter = useRef('')
  async function search(text: string, filter: string) {
    request.current?.abort()
    const controller = new AbortController()
    request.current = controller
    const sameFilter = filter === loadedFilter.current
    setBusy(true); setError(''); setSearched('')
    setPassages(current => sameFilter ? current.map(p => ({ ...p, score: null, exact: false })) : [])
    if (!sameFilter) { setTotal(0); setSelectedId(null) }
    try {
      const result = await explorePassages(text, filter ? Number(filter) : undefined, controller.signal)
      if (controller.signal.aborted) return
      loadedFilter.current = filter
      setPassages(result.passages); setTotal(result.total); setSearched(text.trim())
      setSelectedId([...result.passages].sort((a, b) => (b.score ?? 0) - (a.score ?? 0))[0]?.id ?? null)
    } catch (e) { if (!controller.signal.aborted) setError(errorMessage(e)) }
    finally { if (!controller.signal.aborted) setBusy(false) }
  }
  // Defer the initial load so React's development-only setup/cleanup cycle does not start an immediately aborted request.
  useEffect(() => {
    const timer = window.setTimeout(() => { void search('', '') }, 0)
    return () => { window.clearTimeout(timer); request.current?.abort() }
  }, [])
  const sorted = [...passages].sort((a, b) => (b.score ?? 0) - (a.score ?? 0))
  const selected = passages.find(p => p.id === selectedId)
  const legend = [...new Map(passages.map(p => [p.documentId, p.title])).entries()].sort(([a], [b]) => a - b)
  const colorIds = [...new Set([...documents.map(d => d.id), ...passages.map(p => p.documentId)])].sort((a, b) => a - b)
  const documentColor = (id: number) => documentColors[colorIds.indexOf(id) % documentColors.length]
  const xs = passages.map(p => p.x), ys = passages.map(p => p.y)
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys)
  const score = (p: Passage) => !searched || p.score == null ? `Passage ${p.index + 1}` : `Similarity ${p.score.toFixed(2)}`
  return <section className="min-h-0 flex-1 overflow-y-auto px-5 py-8 sm:px-10" aria-labelledby="explore-title">
    <h1 id="explore-title" className="font-display text-4xl tracking-tight">Explore your knowledge</h1>
    <p className="mt-2 text-sm leading-6 text-muted">Find and compare passages by exact phrase and semantic meaning.</p>
    <form className="my-7 grid grid-cols-[minmax(0,1fr)_auto] items-stretch gap-3 sm:flex sm:flex-wrap" onSubmit={e => { e.preventDefault(); void search(query, documentId) }}>
      <div className="relative col-span-2 min-w-0 sm:min-w-48 sm:flex-1"><MagnifyingGlassIcon size={20} className="absolute top-4 left-3.5 text-muted" aria-hidden /><input aria-label="Search passages" className="field pl-11" placeholder="Search across your documents…" value={query} maxLength={2000} onChange={e => setQuery(e.target.value)} /></div>
      <select className="field min-w-0 w-full max-w-full sm:w-auto" aria-label="Filter by document" value={documentId} onChange={e => { setDocumentId(e.target.value); void search(query, e.target.value) }}><option value="">All documents</option>{documents.map(d => <option key={d.id} value={d.id}>{d.title}</option>)}</select>
      <Button type="submit" busy={busy}>Search</Button>
    </form>
    {error && <Notice>{error}</Notice>}
    {busy && <p role="status" className={passages.length ? 'mb-4 text-sm text-muted' : 'py-12 text-muted'}>{passages.length ? 'Calculating similarity…' : 'Loading passage map…'}</p>}
    {!busy && !error && !passages.length ? <div className="py-12"><h2 className="text-lg font-semibold">No indexed passages yet</h2><p className="mt-2 text-sm text-muted">Add a document or choose another document to explore.</p></div> : passages.length > 0 && <>
      {total > passages.length && <p className="mb-4 text-sm text-muted">Showing a sample of {passages.length} of {total} passages, distributed across documents. Search and scores apply to this sample; filter by document to narrow it. Some documents may be absent when there are more documents than sample places.</p>}
      <div className="grid gap-8 xl:grid-cols-[minmax(0,1.35fr)_minmax(0,1fr)]">
        <div className="order-1 min-w-0 xl:order-2 xl:border-l xl:border-line xl:pl-7">
          <h2 className="font-display text-2xl">Passage map</h2><p className="mt-2 text-xs leading-5 text-muted">Select a point to read its passage. Search to see similarity scores.</p>
          <div className="relative my-5 h-80 border-b border-l border-line bg-surface" role="group" aria-label="Two-dimensional passage map">
            {passages.map(p => <button key={p.id} type="button" onClick={() => setSelectedId(p.id)} aria-label={`${p.title}, passage ${p.index + 1}, ${!searched || p.score == null ? 'similarity available after search' : score(p)}${searched && p.exact ? ', exact phrase match' : ''}`} aria-pressed={selectedId === p.id} style={{ left: `${8 + (p.x - minX) / (maxX - minX || 1) * 84}%`, bottom: `${8 + (p.y - minY) / (maxY - minY || 1) * 84}%` }} className={`group absolute flex size-7 -translate-x-1/2 translate-y-1/2 items-center justify-center rounded-full hover:z-20 focus:z-20 ${selectedId === p.id ? 'z-10 ring-2 ring-ink ring-offset-1 ring-offset-surface' : ''}`}><span aria-hidden className={`flex size-4 items-center justify-center rounded-full ${searched && p.exact ? 'ring-1 ring-ink ring-offset-1 ring-offset-surface' : ''}`}><span className="size-2.5 rounded-full transition-opacity motion-reduce:transition-none" style={{ backgroundColor: documentColor(p.documentId), opacity: similarityOpacity(searched ? p.score : null) }} /></span><span aria-hidden className="pointer-events-none absolute bottom-full left-1/2 mb-1 hidden -translate-x-1/2 whitespace-nowrap rounded bg-ink px-2 py-1 text-xs text-surface shadow-sm group-hover:block group-focus-visible:block">{!searched || p.score == null ? 'Search to see similarity' : score(p)}</span></button>)}
          </div>
          <div className="flex flex-wrap gap-x-4 gap-y-2 text-xs text-muted" aria-label="Map legend">{legend.map(([id, title]) => <span key={id} className="inline-flex min-w-0 items-center gap-2"><span aria-hidden className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: documentColor(id) }} /><span className="break-words">{title}</span></span>)}<span>◎ Selected passage</span>{searched && <span>○ Exact phrase</span>}</div>
          {new Set(legend.map(([id]) => documentColor(id))).size < legend.length && <p className="mt-2 text-xs text-muted">Colors repeat for some documents.</p>}
          {searched && <p className="mt-2 text-xs text-muted">More opaque points are more similar to your search.</p>}
          <p className="mt-4 text-xs leading-5 text-muted">2D PCA projection of passage vectors. Distances are approximate; similarity uses the full vectors. Similarity is not a confidence score.</p>
          {selected && <article className="mt-7 border-t border-line pt-5"><h3 className="font-display text-xl">Selected excerpt</h3><p className="my-3 break-words text-xs text-muted">{selected.title} · Passage {selected.index + 1} · {score(selected)}</p><p className="whitespace-pre-wrap text-sm leading-7"><Highlight text={selected.content} query={searched} /></p></article>}
        </div>
        <div className="order-2 min-w-0 xl:order-1">
          <h2 className="font-display text-2xl">{searched ? <>Results for “{searched}”</> : 'Indexed passages'}</h2>
          <p className="mt-2 text-xs text-muted">{passages.length} passages{searched && ` · ${passages.filter(p => p.exact).length} exact phrase matches · sorted by similarity`}</p>
          <div className="mt-5 max-h-[65vh] overflow-y-auto" aria-label="Passage results">{sorted.map(p => <button key={p.id} type="button" onClick={() => setSelectedId(p.id)} aria-pressed={selectedId === p.id} className={`block w-full border-b border-line p-4 text-left transition-colors hover:bg-subtle ${selectedId === p.id ? 'rounded-control bg-primary/5 ring-1 ring-inset ring-primary' : ''}`}>
            <div className="mb-3 flex flex-wrap items-center gap-2 text-xs text-muted">{searched && <span className={`rounded px-2 py-1 font-medium ${p.exact ? 'bg-primary/10 text-primary' : 'bg-secondary/10 text-secondary'}`}>{p.exact ? 'Exact phrase' : 'Semantic similarity'}</span>}<span className="break-words">{p.title}</span><span className="ml-auto">{score(p)}</span></div>
            <p className="line-clamp-3 text-sm leading-7"><Highlight text={p.content} query={searched} /></p>
          </button>)}</div>
        </div>
      </div>
    </>}
  </section>
}

import { useEffect, useState } from 'react'
import { MagnifyingGlassIcon, XIcon } from '@phosphor-icons/react'
import { Button, Notice } from '../../components/ui'
import * as api from '../../api'
import { kindLabels, kindName, kindOrder } from './wikiMarkdown'

const Stale = () => <span className="rounded-full border border-line px-2 py-0.5 text-xs text-muted" title="Its sources changed since this page was written">Stale</span>

/** The wiki index grouped by page kind, with a literal and semantic search over the pages. */
export function WikiBrowser({ wikiId, version, onOpen }: { wikiId: number; version: string | null; onOpen: (slug: string) => void }) {
  const [pages, setPages] = useState<api.WikiPageSummary[] | null>(null)
  const [error, setError] = useState('')
  const [text, setText] = useState('')
  const [hits, setHits] = useState<api.WikiPageHit[] | null>(null)
  const [searching, setSearching] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    api.listWikiPages(wikiId, {}, controller.signal)
      .then(result => { if (!controller.signal.aborted) { setPages(result ?? []); setError('') } })
      .catch(e => { if (!controller.signal.aborted) setError(api.errorMessage(e)) })
    return () => controller.abort()
  }, [wikiId, version])

  async function search() {
    const q = text.trim()
    if (!q) { setHits(null); return }
    setSearching(true); setError('')
    try { setHits((await api.searchWiki(wikiId, q, 20)) ?? []) }
    catch (e) { setError(api.errorMessage(e)) }
    finally { setSearching(false) }
  }

  const groups = kindOrder.map(kind => ({ kind, pages: (pages ?? []).filter(page => page.kind === kind) })).filter(group => group.pages.length)
  return <div className="space-y-6">
    <form role="search" className="flex items-end gap-2" onSubmit={e => { e.preventDefault(); void search() }}>
      <div className="min-w-0 flex-1 space-y-2">
        <label htmlFor="wiki-search" className="block text-sm font-medium">Search the wiki</label>
        <input id="wiki-search" className="field" value={text} maxLength={300} placeholder="A term, a rule, a class name…" onChange={e => { setText(e.target.value); if (!e.target.value.trim()) setHits(null) }} />
      </div>
      {hits && <Button variant="ghost" aria-label="Clear search" title="Clear search" onClick={() => { setText(''); setHits(null) }}><XIcon size={18} aria-hidden /></Button>}
      <Button type="submit" busy={searching} disabled={!text.trim()}><MagnifyingGlassIcon size={18} aria-hidden />Search</Button>
    </form>
    {error && <Notice>{error}</Notice>}
    {hits ? <section aria-label="Search results">
      <h3 className="mb-2 text-sm font-semibold">{hits.length ? `${hits.length} ${hits.length === 1 ? 'result' : 'results'}` : 'No page matches this search.'}</h3>
      <ul className="divide-y divide-line">{hits.map(hit => <li key={hit.slug} className="py-3">
        <button type="button" className="block w-full text-left" onClick={() => onOpen(hit.slug)}>
          <span className="flex flex-wrap items-center gap-2"><span className="font-medium underline decoration-line underline-offset-4">{hit.title}</span>
            <span className="rounded-full bg-subtle px-2 py-0.5 text-xs text-muted">{kindName(hit.kind)}</span>
            <span className={`rounded-full border px-2 py-0.5 text-xs ${hit.match === 'literal' ? 'border-primary text-primary' : 'border-secondary text-secondary'}`}>{hit.match === 'literal' ? 'Exact match' : 'Semantic match'}</span></span>
          <span className="mt-1 block text-sm leading-6 text-muted">{hit.summary}</span>
          {hit.excerpt && <span className="mt-1 block border-l-2 border-line pl-3 text-sm leading-6 break-words">{hit.excerpt}</span>}
        </button>
      </li>)}</ul>
    </section> : pages === null && !error ? <p role="status" className="text-sm text-muted">Loading pages…</p>
      : !groups.length ? <p className="text-sm leading-6 text-muted">The wiki has no pages yet. Refresh it to compile pages from the folder's documents and repositories.</p>
        : groups.map(group => <section key={group.kind} aria-labelledby={`wiki-kind-${group.kind}`}>
          <h3 id={`wiki-kind-${group.kind}`} className="mb-1 text-sm font-semibold">{kindLabels[group.kind]} <span className="font-normal text-muted">({group.pages.length})</span></h3>
          <ul className="divide-y divide-line">{group.pages.map(page => <li key={page.slug}>
            <button type="button" className="block w-full py-3 text-left" onClick={() => onOpen(page.slug)}>
              <span className="flex flex-wrap items-center gap-2"><span className="font-medium underline decoration-line underline-offset-4">{page.title}</span>{page.stale && <Stale />}</span>
              <span className="mt-1 block text-sm leading-6 text-muted">{page.summary}</span>
            </button>
          </li>)}</ul>
        </section>)}
  </div>
}

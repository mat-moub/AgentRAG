import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import type { Components } from 'react-markdown'
import { GraphIcon } from '@phosphor-icons/react'
import { Button, Notice } from '../../components/ui'
import * as api from '../../api'
import { kindName, linkLabels, parseWikiHref, wikiLinksToMarkdown, wikiUrlTransform } from './wikiMarkdown'

const missingDocument = 'This document is no longer available, or you no longer have access to it.'

/** One compiled page: its markdown with in-app links and citations, and a side panel of typed links and sources. */
export function WikiPage({ wikiId, slug, onNavigate, onOpenDocument, onShowGraph, sideGraph }: {
  wikiId: number; slug: string; onNavigate: (slug: string) => void
  onOpenDocument: (documentId: number) => boolean; onShowGraph: (slug: string) => void
  /** A map shown at the top of the side panel, on screens wide enough for one. */
  sideGraph?: ReactNode
}) {
  const [page, setPage] = useState<api.WikiPageDetail | null>(null)
  const [error, setError] = useState('')
  const [citationError, setCitationError] = useState('')
  const [resolving, setResolving] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    api.getWikiPage(wikiId, slug, controller.signal)
      .then(result => { if (!controller.signal.aborted) { setPage(result); setError('') } })
      .catch(e => { if (!controller.signal.aborted) { setPage(null); setError(api.errorStatus(e) === 404 ? 'This page does not exist (any more).' : api.errorMessage(e)) } })
    return () => controller.abort()
  }, [wikiId, slug])

  function openDocument(documentId: number) {
    setCitationError('')
    if (!onOpenDocument(documentId)) setCitationError(missingDocument)
  }
  async function openRepositoryFile(repositoryId: number, path: string) {
    setCitationError(''); setResolving(true)
    try { openDocument((await api.findWikiFile(repositoryId, path)).documentId) }
    catch (e) { setCitationError(api.errorStatus(e) === 404 ? `The file ${path} is no longer in the library, or you no longer have access to it.` : api.errorMessage(e)) }
    finally { setResolving(false) }
  }

  const components: Components = {
    a({ href, children }) {
      const target = parseWikiHref(href)
      if (target.type === 'external') return <a href={target.url} target="_blank" rel="noopener noreferrer">{children}</a>
      if (target.type === 'unknown') return <span>{children}</span>
      const run = () => target.type === 'page' ? onNavigate(target.slug)
        : target.type === 'document' ? openDocument(target.documentId)
          : target.type === 'repository' ? void openRepositoryFile(target.repositoryId, target.path) : undefined
      return <a href={href} className={target.type === 'page' ? 'text-accent' : undefined} title={target.type === 'page' ? undefined : 'Open the cited source'} onClick={e => { e.preventDefault(); run() }}>{children}</a>
    },
  }

  if (error) return <Notice>{error}</Notice>
  if (!page) return <p role="status" className="text-sm text-muted">Loading page…</p>

  const specification = page.links.filter(link => link.link === 'implements')
  const implementation = page.backlinks.filter(link => link.link === 'implements')
  const links = page.links.filter(link => link.link !== 'implements')
  const backlinks = page.backlinks.filter(link => link.link !== 'implements')
  const linkList = (items: api.WikiLink[], direction: 'out' | 'back') => <ul className="space-y-2">{items.map(link => <li key={`${link.link}:${link.slug}`}>
    <button type="button" className="text-left text-sm underline decoration-line underline-offset-4 hover:decoration-ink" onClick={() => onNavigate(link.slug)}>{link.title}</button>
    <span className="ml-2 text-xs text-muted">{kindName(link.kind)} · {linkLabels[link.link][direction]}</span>
    {link.note && <p className="text-xs leading-5 text-muted">{link.note}</p>}
  </li>)}</ul>
  const section = (title: string, content: ReactNode) => <section aria-label={title}><h3 className="mb-2 text-sm font-semibold">{title}</h3>{content}</section>

  return <div className="grid min-w-0 gap-8 lg:grid-cols-[minmax(0,1fr)_17rem]">
    <article className="min-w-0">
      <header className="mb-5">
        <div className="flex flex-wrap items-center gap-2 text-xs text-muted">
          <span className="rounded-full bg-subtle px-2 py-0.5">{kindName(page.kind)}</span>
          {page.stale && <span className="rounded-full border border-line px-2 py-0.5" title="Its sources changed since this page was written">Stale</span>}
          <span>Updated {new Date(page.updatedUtc).toLocaleDateString()}</span>
        </div>
        <h2 className="mt-2 font-display text-3xl leading-tight">{page.title}</h2>
        {page.summary && <p className="mt-2 text-sm leading-6 text-muted">{page.summary}</p>}
        {page.aliases.length > 0 && <p className="mt-1 text-xs text-muted">Also known as {page.aliases.join(', ')}</p>}
      </header>
      {citationError && <div className="mb-4"><Notice>{citationError}</Notice></div>}
      {resolving && <p role="status" className="mb-4 text-sm text-muted">Looking for the file…</p>}
      <div className="answer"><ReactMarkdown urlTransform={wikiUrlTransform} components={components}>{wikiLinksToMarkdown(page.content)}</ReactMarkdown></div>
    </article>
    <aside aria-label="Page links and sources" className="space-y-6 lg:border-l lg:border-line lg:pl-6">
      {sideGraph}
      <Button variant="secondary" className="w-full" onClick={() => onShowGraph(page.slug)}><GraphIcon size={18} aria-hidden />Show in graph</Button>
      {implementation.length > 0 && section('Implementation', linkList(implementation, 'back'))}
      {specification.length > 0 && section('Specification', linkList(specification, 'out'))}
      {links.length > 0 && section('Links', linkList(links, 'out'))}
      {backlinks.length > 0 && section('Linked from', linkList(backlinks, 'back'))}
      {page.sources.length > 0 && section('Sources', <ul className="space-y-2">{page.sources.map((source, index) => <li key={index}>
        <button type="button" className="break-words text-left text-sm underline decoration-line underline-offset-4 hover:decoration-ink"
          onClick={() => source.documentId != null ? openDocument(source.documentId) : source.repositoryId != null && source.path ? void openRepositoryFile(source.repositoryId, source.path) : undefined}>{source.path ?? source.title}</button>
        {source.path && source.title !== source.path && <span className="ml-2 text-xs text-muted">{source.title}</span>}
      </li>)}</ul>)}
    </aside>
  </div>
}

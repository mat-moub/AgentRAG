import { Suspense, lazy, useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { ArrowLeftIcon, ArrowsClockwiseIcon, LinkBreakIcon, WrenchIcon } from '@phosphor-icons/react'
import { Button, Dialog, Notice } from '../../components/ui'
import * as api from '../../api'
import { ProgressRing } from '../../components/ProgressRing'
import { WikiBrowser } from './WikiBrowser'
import { WikiIntro } from './WikiIntro'
import { wikiRunPercent } from './wikiProgress'
import { WikiPage } from './WikiPage'

const wideQuery = '(min-width: 1024px)'
/** True on screens wide enough for the page's side panel (the lg breakpoint). */
function useWideScreen() {
  return useSyncExternalStore(
    notify => { const media = window.matchMedia?.(wideQuery); media?.addEventListener('change', notify); return () => media?.removeEventListener('change', notify) },
    () => window.matchMedia?.(wideQuery).matches ?? false,
    () => false)
}

const WikiGraph = lazy(() => import('./WikiGraph').then(module => ({ default: module.WikiGraph })))

const stageLabels: Record<string, string> = {
  queued: 'Waiting for the worker (runs start one at a time)', listing: 'Listing sources', extracting: 'Reading sources', resolving: 'Resolving pages',
  writing: 'Writing pages', linking: 'Linking pages and code', indexing: 'Indexing pages', completed: 'Completed',
}
const modeLabels: Record<api.WikiRunMode, string> = { incremental: 'Refresh', full: 'Rebuild', lint: 'Link check' }
const count = (value: number | undefined, singular: string, plural = singular + 's') => `${value ?? 0} ${value === 1 ? singular : plural}`
const ending = (run: api.WikiRun) => run.status === 'completed' ? 'completed' : run.status === 'cancelled' ? 'was cancelled' : 'failed'

/** What a finished run did, as a few plain sentences. */
function RunSummary({ run }: { run: api.WikiRun }) {
  const stats = run.stats
  const tokens = (stats?.inputTokens ?? 0) + (stats?.outputTokens ?? 0)
  return <div className="space-y-1 text-sm leading-6">
    <p>{modeLabels[run.mode]} {ending(run)}{run.finishedUtc && ` ${new Date(run.finishedUtc).toLocaleString()}`}.</p>
    {run.error && <Notice>{run.error}</Notice>}
    {stats && <>
      {run.mode !== 'lint' && <p className="text-muted">Sources: {stats.sourcesAdded ?? 0} added, {stats.sourcesChanged ?? 0} changed, {stats.sourcesRemoved ?? 0} removed, {stats.sourcesExcluded ?? 0} excluded{!!stats.sourcesFailed && `, ${stats.sourcesFailed} failed`}.</p>}
      {run.mode !== 'lint' && <p className="text-muted">Pages: {stats.pagesCreated ?? 0} created, {stats.pagesUpdated ?? 0} updated, {stats.pagesRemoved ?? 0} removed{!!stats.pagesFailed && `, ${stats.pagesFailed} failed`}.</p>}
      <p className="text-muted">{count(stats.pages, 'page')} and {count(stats.links, 'link')}; {count(stats.implementationLinks, 'implementation link')} tie {count(stats.pagesLinkedToCode, 'page')} to code.</p>
      {!!stats.brokenLinks && <p className="text-danger">{count(stats.brokenLinks, 'broken link')}.</p>}
      {!!stats.unresolvedCitations && <p className="text-muted">{count(stats.unresolvedCitations, 'citation')} could not be matched to a source.</p>}
      {!!stats.withoutImplementation?.length && <p className="text-muted">Without implementation: {stats.withoutImplementation.join(', ')}.</p>}
      {!!stats.orphanPages?.length && <p className="text-muted">Pages nothing links to: {stats.orphanPages.join(', ')}.</p>}
      {tokens > 0 && <p className="text-muted">Model usage: {(stats.inputTokens ?? 0).toLocaleString()} input and {(stats.outputTokens ?? 0).toLocaleString()} output tokens.</p>}
    </>}
  </div>
}

/** The wiki of one folder: its status, the background run that refreshes it, and tabs to browse, read and map its pages. */
export function WikiHome({ folder, onOpenDocument }: { folder: api.Folder; onOpenDocument: (documentId: number) => boolean }) {
  const [wiki, setWiki] = useState<api.Wiki | null | undefined>(undefined)
  const [status, setStatus] = useState<api.WikiStatus | null>(null)
  const [run, setRun] = useState<api.WikiRun | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [rebuilding, setRebuilding] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const [tab, setTab] = useState<'overview' | 'page'>('overview')
  const [slug, setSlug] = useState('index')
  const [center, setCenter] = useState<string | null>(null)
  const loaded = useRef(0)
  const wide = useWideScreen()

  const wikiId = wiki?.id
  const reload = useCallback(async (signal?: AbortSignal) => {
    const ticket = ++loaded.current
    try {
      const wikis = (await api.listWikis(signal)) ?? []
      if (signal?.aborted || ticket !== loaded.current) return
      const found = wikis.find(item => item.folderId === folder.id) ?? null
      setWiki(found)
      setRun(found?.lastRun ?? null)
      setStatus(found ? await api.getWikiStatus(found.id, signal) : null)
      setError('')
    } catch (e) { if (!signal?.aborted) { setError(api.errorMessage(e)); setWiki(current => current === undefined ? null : current) } }
  }, [folder.id])
  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => { void reload(controller.signal) }, 0)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [reload])

  // The run goes on server-side whatever happens here: attach to it whenever one is active, and detach when the view goes away.
  const activeRunId = api.wikiRunActive(run) ? run?.id : undefined
  useEffect(() => {
    if (wikiId === undefined || activeRunId === undefined) return
    const controller = new AbortController()
    api.watchWikiRun(wikiId, activeRunId, event => { if (!controller.signal.aborted && event.run) setRun(event.run) }, controller.signal)
      .catch(e => { if (!controller.signal.aborted) setError(api.errorMessage(e)) })
      .finally(() => { if (!controller.signal.aborted) void reload() })
    return () => controller.abort()
  }, [wikiId, activeRunId, attempt, reload])

  // Creating a wiki also starts its first build: an empty wiki is of no use to anyone.
  async function create(language: string) {
    setBusy(true); setError('')
    try {
      const created = await api.createWiki(folder.id, language.trim() || undefined)
      setWiki(created)
      setRun(await api.startWikiRun(created.id, 'incremental'))
    }
    catch (e) { setError(api.errorStatus(e) === 409 ? 'This folder already has a wiki.' : api.errorMessage(e)); void reload() }
    finally { setBusy(false) }
  }
  async function start(mode: api.WikiRunMode) {
    if (!wiki) return
    setBusy(true); setError('')
    try { setRun(await api.startWikiRun(wiki.id, mode)) }
    catch (e) { setError(api.errorStatus(e) === 409 ? 'The wiki is already refreshing.' : api.errorMessage(e)); void reload() }
    finally { setBusy(false); setRebuilding(false) }
  }
  async function cancel() {
    if (!wiki || !run) return
    setBusy(true); setError('')
    try { setRun(await api.cancelWikiRun(wiki.id, run.id)) }
    catch (e) { setError(api.errorMessage(e)) }
    finally { setBusy(false) }
  }

  const open = (next: string) => { setSlug(next); setTab('page') }
  const active = api.wikiRunActive(run)
  const version = wiki?.refreshedUtc ?? null
  const progress = run && active ? <div className="flex flex-col items-center gap-5 min-[400px]:flex-row">
    <ProgressRing percent={wikiRunPercent(run)} label="Wiki run progress" />
    <p role="status" className="text-sm leading-6">
      <span className="font-semibold">{modeLabels[run.mode]}: {stageLabels[run.stage] ?? run.stage}</span>
      {run.total > 0 && <><br /><span className="tabular-nums text-muted">{run.completed} of {run.total}</span></>}
    </p>
  </div> : null

  if (wiki === undefined) return <p role="status" className="p-5 text-sm text-muted sm:px-10">Loading the wiki…</p>
  const last = run && !active ? run : null
  // No wiki, or one that was never built: explain what a wiki is for and offer to create or build it.
  const neverBuilt = wiki !== null && !wiki.refreshedUtc && (status?.pages ?? wiki.pages) === 0 && !active
  if (wiki === null || neverBuilt)
    return <WikiIntro folder={folder} wiki={wiki} busy={busy} error={error}
      onCreate={next => void create(next)} onBuild={() => void start('incremental')} />

  // A small map of the page's neighbourhood: above the page on narrow screens, square in the side panel on wide ones.
  const pageGraph = <Suspense fallback={<p role="status" className="text-sm text-muted">Loading the graph…</p>}>
    <WikiGraph compact wikiId={wiki.id} version={version} center={slug} onCenterChange={() => {}} onOpenPage={open} />
  </Suspense>
  return <div className="min-h-0 flex-1 animate-enter overflow-y-auto">
    <div className="mx-auto w-full max-w-6xl space-y-6 px-5 py-6 sm:px-10">
      <header className="space-y-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div><h2 className="font-display text-3xl leading-tight">Wiki of {wiki.folder}</h2>
            <p className="mt-1 text-sm text-muted">{count(status?.pages ?? wiki.pages, 'page')}{status ? ` · ${status.stalePages} stale · ${status.pendingSources} pending sources · ${status.excludedSources} excluded sources` : ''} · {wiki.refreshedUtc ? `Refreshed ${new Date(wiki.refreshedUtc).toLocaleString()}` : 'Never refreshed'}</p></div>
          {wiki.canRefresh && <div className="flex flex-wrap gap-2">
            <Button busy={busy && !active} disabled={active} onClick={() => void start('incremental')}><ArrowsClockwiseIcon size={18} aria-hidden />Refresh wiki</Button>
            <Button variant="secondary" disabled={active || busy} onClick={() => setRebuilding(true)}><WrenchIcon size={18} aria-hidden />Rebuild</Button>
            <Button variant="secondary" disabled={active || busy} onClick={() => void start('lint')}><LinkBreakIcon size={18} aria-hidden />Check links</Button>
          </div>}
        </div>
        {error && <Notice>{error}{active && <> <button type="button" className="underline underline-offset-4" onClick={() => { setError(''); setAttempt(value => value + 1) }}>Reconnect to the run</button></>}</Notice>}
        {progress && <div className="flex flex-wrap items-center gap-3">{progress}{wiki.canRefresh && <Button variant="secondary" busy={busy} onClick={() => void cancel()}>Cancel run</Button>}</div>}
        {last && <RunSummary run={last} />}
      </header>
      <nav aria-label="Wiki sections" className="flex gap-1 border-b border-line pb-2">
        <Button variant={tab === 'overview' ? 'primary' : 'ghost'} aria-pressed={tab === 'overview'} onClick={() => setTab('overview')}>Overview</Button>
        <Button variant={tab === 'page' ? 'primary' : 'ghost'} aria-pressed={tab === 'page'} onClick={() => setTab('page')}>Page</Button>
      </nav>
      {tab === 'overview' && <div className="space-y-8">
        <Suspense fallback={<p role="status" className="text-sm text-muted">Loading the graph…</p>}>
          <WikiGraph wikiId={wiki.id} version={version} center={center} onCenterChange={setCenter} onOpenPage={open} />
        </Suspense>
        <WikiBrowser wikiId={wiki.id} version={version} onOpen={open} />
      </div>}
      {tab === 'page' && <div className="space-y-4">
        <Button variant="ghost" onClick={() => setTab('overview')}><ArrowLeftIcon size={18} aria-hidden />Overview</Button>
        {!wide && pageGraph}
        <WikiPage wikiId={wiki.id} slug={slug} onNavigate={open} onOpenDocument={onOpenDocument} onShowGraph={next => { setCenter(next); setTab('overview') }} sideGraph={wide ? pageGraph : undefined} />
      </div>}
    </div>
    {rebuilding && <Dialog title="Rebuild the whole wiki?" onClose={() => setRebuilding(false)} dismissible={!busy}>
      <p className="mb-5 text-sm leading-6 text-muted">Every page is written again from all of the sources. This takes longer than a refresh and uses many more model tokens. The current pages stay readable meanwhile.</p>
      <div className="flex justify-end gap-2"><Button variant="secondary" onClick={() => setRebuilding(false)} disabled={busy}>Cancel</Button><Button busy={busy} onClick={() => void start('full')}>Rebuild</Button></div>
    </Dialog>}
  </div>
}

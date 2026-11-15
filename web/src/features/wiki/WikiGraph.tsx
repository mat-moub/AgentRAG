import { useEffect, useMemo, useRef, useState } from 'react'
import ForceGraph2D from 'react-force-graph-2d'
import { Button, Notice } from '../../components/ui'
import * as api from '../../api'
import { kindLabels, kindName, kindOrder } from './wikiMarkdown'

const codeKinds = new Set(['module', 'component', 'data-entity', 'api'])
const tokenOf: Record<string, string> = {
  overview: '--color-primary', feature: '--color-secondary', concept: '--color-tertiary', rule: '--color-danger',
  module: '--color-ink', component: '--color-muted', 'data-entity': '--color-secondary', api: '--color-tertiary',
  source: '--color-muted', document: '--color-muted', 'code-file': '--color-muted',
}
const linkKinds = [
  { kind: 'implements', label: 'Implements (code to specification)', dash: null, width: 2.5, token: '--color-accent' },
  { kind: 'depends-on', label: 'Depends on', dash: [6, 4], width: 1.25, token: '--color-ink' },
  { kind: 'part-of', label: 'Part of', dash: null, width: 1.25, token: '--color-secondary' },
  { kind: 'related', label: 'Related', dash: null, width: 0.6, token: '--color-muted' },
  { kind: 'mentions', label: 'Mentions', dash: [1, 3], width: 0.6, token: '--color-muted' },
  { kind: 'source', label: 'Source', dash: [1, 3], width: 0.8, token: '--color-tertiary' },
]
const fallback: Record<string, string> = {
  '--color-primary': '#b84b16', '--color-secondary': '#895344', '--color-tertiary': '#a06a18', '--color-danger': '#a3332b',
  '--color-ink': '#252724', '--color-muted': '#666862', '--color-line': '#deded8', '--color-accent': '#b84b16', '--color-canvas': '#f8f7f4',
}
/** Reads the design tokens at runtime, so a dark colour scheme is followed. */
function readPalette(): Record<string, string> {
  const style = getComputedStyle(document.documentElement)
  return Object.fromEntries(Object.keys(fallback).map(name => [name, style.getPropertyValue(name).trim() || fallback[name]]))
}

interface DrawnNode extends api.WikiGraphNode { x?: number; y?: number }

/** Interactive map of the wiki's pages and typed links, whole or around one page. */
export function WikiGraph({ wikiId, version, center, onCenterChange, onOpenPage }: {
  wikiId: number; version: string | null; center: string | null; onCenterChange: (slug: string | null) => void; onOpenPage: (slug: string) => void
}) {
  const [graph, setGraph] = useState<api.WikiGraph | null>(null)
  const [error, setError] = useState('')
  const [hidden, setHidden] = useState<Set<string>>(new Set())
  const [sources, setSources] = useState(false)
  const [depth, setDepth] = useState(2)
  const [palette, setPalette] = useState(readPalette)
  const [size, setSize] = useState({ width: 600, height: 480 })
  const frame = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)')
    const update = () => setPalette(readPalette())
    media?.addEventListener('change', update)
    return () => media?.removeEventListener('change', update)
  }, [])
  useEffect(() => {
    const element = frame.current
    if (!element) return
    const measure = () => setSize({ width: Math.max(element.clientWidth, 200), height: Math.max(element.clientHeight, 320) })
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  const kindsKey = [...hidden].sort().join(',')
  useEffect(() => {
    const controller = new AbortController()
    const shown = kindOrder.filter(kind => !kindsKey.split(',').includes(kind))
    api.getWikiGraph(wikiId, {
      center: center ?? undefined, depth: center ? depth : undefined, includeSources: sources,
      kinds: shown.length === kindOrder.length ? undefined : shown,
    }, controller.signal)
      .then(result => { if (!controller.signal.aborted) { setGraph(result); setError('') } })
      .catch(e => { if (!controller.signal.aborted) setError(api.errorMessage(e)) })
    return () => controller.abort()
  }, [wikiId, version, center, depth, sources, kindsKey])

  // The library mutates the data it is given, so it gets copies, and no edge may point at a missing node.
  const data = useMemo(() => {
    const nodes: DrawnNode[] = (graph?.nodes ?? []).map(node => ({ ...node }))
    const ids = new Set(nodes.map(node => node.id))
    return { nodes, links: (graph?.edges ?? []).filter(edge => ids.has(edge.source) && ids.has(edge.target)).map(edge => ({ ...edge })) }
  }, [graph])
  const colourOf = (kind: string) => palette[tokenOf[kind] ?? '--color-muted']
  const styleOf = (kind: string) => linkKinds.find(link => link.kind === kind) ?? linkKinds[3]

  function draw(node: DrawnNode, context: CanvasRenderingContext2D, scale: number) {
    const x = node.x ?? 0
    const y = node.y ?? 0
    const isSource = node.kind === 'document' || node.kind === 'code-file'
    const radius = (isSource ? 3 : 4) + Math.min(node.degree, 12) * 0.6
    context.beginPath()
    if (codeKinds.has(node.kind)) context.rect(x - radius, y - radius, radius * 2, radius * 2)
    else if (isSource) { context.moveTo(x, y - radius); context.lineTo(x + radius, y); context.lineTo(x, y + radius); context.lineTo(x - radius, y); context.closePath() }
    else context.arc(x, y, radius, 0, 2 * Math.PI)
    context.fillStyle = isSource ? palette['--color-canvas'] : colourOf(node.kind)
    context.fill()
    context.lineWidth = (node.id === center ? 3 : 1.25) / scale
    context.strokeStyle = node.id === center ? palette['--color-ink'] : colourOf(node.kind)
    context.stroke()
    if (scale >= 1.1 || node.id === center) {
      context.font = `${12 / scale}px 'DM Sans Variable', sans-serif`
      context.textAlign = 'center'
      context.textBaseline = 'top'
      context.fillStyle = palette['--color-ink']
      context.fillText(node.label, x, y + radius + 2 / scale)
    }
  }
  function pointer(node: DrawnNode, colour: string, context: CanvasRenderingContext2D) {
    context.fillStyle = colour
    context.beginPath()
    context.arc(node.x ?? 0, node.y ?? 0, 8, 0, 2 * Math.PI)
    context.fill()
  }

  const shownKinds = new Set(data.nodes.map(node => node.kind))
  return <div className="space-y-4">
    <div className="flex flex-wrap items-end gap-x-6 gap-y-3">
      <fieldset className="min-w-0">
        <legend className="mb-1 text-sm font-medium">Page kinds</legend>
        <div className="flex flex-wrap gap-x-4 gap-y-1">{kindOrder.map(kind => <label key={kind} className="flex min-h-8 items-center gap-2 text-sm">
          <input type="checkbox" checked={!hidden.has(kind)} onChange={e => setHidden(current => { const next = new Set(current); if (e.target.checked) next.delete(kind); else next.add(kind); return next })} />{kindLabels[kind]}
        </label>)}</div>
      </fieldset>
      <label className="flex min-h-8 items-center gap-2 text-sm"><input type="checkbox" checked={sources} onChange={e => setSources(e.target.checked)} />Show sources</label>
      {center && <div className="flex flex-wrap items-center gap-2 text-sm">
        <span>Around <strong className="font-medium">{data.nodes.find(node => node.id === center)?.label ?? center}</strong></span>
        <label className="flex items-center gap-2">Depth
          <select className="field w-auto py-1" value={depth} onChange={e => setDepth(Number(e.target.value))}>{[1, 2, 3, 4].map(value => <option key={value} value={value}>{value}</option>)}</select>
        </label>
        <Button variant="secondary" className="min-h-9 py-1.5" onClick={() => onCenterChange(null)}>Show the whole wiki</Button>
      </div>}
    </div>
    {error && <Notice>{error}</Notice>}
    {graph?.truncated && <p role="status" className="rounded-control bg-subtle p-3 text-sm leading-6 text-muted">The graph is too large to be drawn in full. Hide some page kinds or show the neighbourhood of a page to see the rest.</p>}
    <div ref={frame} className="h-[28rem] overflow-hidden rounded-panel border border-line bg-surface lg:h-[34rem]" role="img" aria-label="Graph of wiki pages and their links">
      {graph && !data.nodes.length ? <p className="p-5 text-sm text-muted">There is nothing to draw yet.</p> :
        <ForceGraph2D<DrawnNode, { source: string; target: string; kind: string }> graphData={data} width={size.width} height={size.height} nodeId="id" cooldownTime={4000}
          nodeLabel={node => `${node.label} · ${kindName(node.kind)}${node.summary ? '\n' + node.summary : ''}`}
          nodeCanvasObject={draw} nodePointerAreaPaint={pointer}
          linkColor={link => palette[styleOf(link.kind).token]} linkWidth={link => styleOf(link.kind).width} linkLineDash={link => styleOf(link.kind).dash}
          linkDirectionalArrowLength={link => link.kind === 'implements' || link.kind === 'depends-on' ? 4 : 0} linkDirectionalArrowRelPos={1} linkDirectionalArrowColor={link => palette[styleOf(link.kind).token]}
          onNodeClick={node => { if (node.kind !== 'document' && node.kind !== 'code-file') onOpenPage(node.id) }} />}
    </div>
    <div className="grid gap-x-8 gap-y-3 text-xs text-muted sm:grid-cols-2" aria-label="Legend">
      <ul className="flex flex-wrap gap-x-4 gap-y-1">{[...kindOrder, ...(sources ? ['document', 'code-file'] : [])].filter(kind => shownKinds.has(kind) || !data.nodes.length).map(kind => <li key={kind} className="flex items-center gap-1.5">
        <span aria-hidden className={`inline-block size-3 border-2 ${codeKinds.has(kind) ? '' : kind === 'document' || kind === 'code-file' ? 'rotate-45 bg-canvas' : 'rounded-full'}`} style={{ borderColor: colourOf(kind), backgroundColor: kind === 'document' || kind === 'code-file' ? undefined : colourOf(kind) }} />{kindName(kind)}
      </li>)}</ul>
      <ul className="flex flex-wrap gap-x-4 gap-y-1">{linkKinds.filter(link => link.kind !== 'source' || sources).map(link => <li key={link.kind} className="flex items-center gap-1.5">
        <span aria-hidden className="inline-block w-6 border-t" style={{ borderTopColor: palette[link.token], borderTopWidth: Math.max(link.width, 1), borderTopStyle: link.dash ? (link.dash[0] <= 1 ? 'dotted' : 'dashed') : 'solid' }} />{link.label}
      </li>)}</ul>
      <p className="sm:col-span-2">Round shapes are functional pages, squares are code pages{sources ? ', diamonds are sources' : ''}. Click a page to open it.</p>
    </div>
  </div>
}

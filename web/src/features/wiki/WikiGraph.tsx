import { useEffect, useMemo, useRef, useState } from 'react'
import ForceGraph2D, { type ForceGraphMethods } from 'react-force-graph-2d'
import { forceCollide, forceX, forceY } from 'd3-force'
import { Button, Notice } from '../../components/ui'
import * as api from '../../api'
import { kindOrder } from './wikiMarkdown'
import {
  codeKinds, defaultLinkKinds, drawnRadius, edgeSentence, functionalKinds, isRawSourceKind, labelCandidates, linkKindOrder, linkStyleOf, linkStyles,
  nodeRadius, nodeStyleOf, placeLabels, shapePath, sideOf, truncateLabel, withAlpha, type LabelBox,
} from './wikiGraphStyle'

const fallback: Record<string, string> = {
  '--color-chart-terracotta': '#c05d3c', '--color-chart-ochre': '#c28a2c', '--color-chart-deep-teal': '#356d78', '--color-chart-teal': '#388779',
  '--color-ink': '#252724', '--color-muted': '#666862', '--color-surface': '#fdfcfb',
}
/** Reads the design tokens at runtime, so a dark colour scheme is followed. */
function readPalette(): Record<string, string> {
  const style = getComputedStyle(document.documentElement)
  return Object.fromEntries(Object.keys(fallback).map(name => [name, style.getPropertyValue(name).trim() || fallback[name]]))
}

interface DrawnNode extends api.WikiGraphNode { x?: number; y?: number }
interface GraphLink { source: string | DrawnNode; target: string | DrawnNode; kind: string }

const endId = (end: string | DrawnNode | number | undefined) => typeof end === 'object' ? end.id : String(end)
const escapeHtml = (text: string) => text.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c] ?? c)
const fadedAlpha = 0.12
const labelFont = (scale: number) => `${12 / scale}px 'DM Sans Variable', sans-serif`

/** Small SVG copy of a node shape, identical to the one on the canvas. */
function NodeGlyph({ kind, palette }: { kind: string; palette: Record<string, string> }) {
  const style = nodeStyleOf(kind)
  const colour = palette[style.token]
  const r = (kind === 'module' ? 8.5 : 7) * style.scale
  return <svg aria-hidden width="20" height="20" viewBox="0 0 20 20" className="shrink-0">
    {style.hollow
      ? <path d={shapePath(style.shape, 10, 10, r - style.strokeWidth / 2)} fill={palette['--color-surface']} stroke={colour} strokeWidth={style.strokeWidth} />
      : <path d={shapePath(style.shape, 10, 10, style.outerRing ? r - 2 : r)} fill={colour} stroke={palette['--color-surface']} strokeWidth="1.5" />}
    {style.outerRing && <path d={shapePath('circle', 10, 10, r)} fill="none" stroke={colour} strokeWidth="1.25" />}
  </svg>
}

/** Small SVG copy of a link style. */
function LinkGlyph({ kind, palette }: { kind: string; palette: Record<string, string> }) {
  const style = linkStyleOf(kind)
  const colour = withAlpha(palette[style.token], Math.max(style.alpha, 0.7))
  return <svg aria-hidden width="28" height="14" viewBox="0 0 28 14" className="shrink-0">
    <line x1="1" y1="7" x2={style.arrow ? 20 : 27} y2="7" stroke={colour} strokeWidth={Math.max(style.width, 1.25)} strokeDasharray={style.dash?.join(' ')} />
    {style.arrow > 0 && <path d={`M27 7L${27 - style.arrow - 2} ${7 - style.arrow * 0.7}L${27 - style.arrow - 2} ${7 + style.arrow * 0.7}Z`} fill={colour} />}
  </svg>
}

function LegendToggle({ pressed, onToggle, glyph, children }: { pressed: boolean; onToggle: () => void; glyph: React.ReactNode; children: React.ReactNode }) {
  return <button type="button" aria-pressed={pressed} onClick={onToggle}
    className={`inline-flex min-h-11 items-center gap-2 rounded-control border px-2.5 text-sm transition-colors ${pressed ? 'border-ink/30 bg-subtle text-ink' : 'border-line bg-surface text-muted opacity-70'}`}>
    {glyph}{children}
  </button>
}

function toggled<T>(set: Set<T>, value: T) {
  const next = new Set(set)
  if (!next.delete(value)) next.add(value)
  return next
}

/** Interactive map of the wiki's pages and typed links, whole or around one page. */
export function WikiGraph({ wikiId, version, center, onCenterChange, onOpenPage, compact = false }: {
  wikiId: number; version: string | null; center: string | null; onCenterChange: (slug: string | null) => void; onOpenPage: (slug: string) => void
  /** A small map of the neighbourhood of one page, without filters or legend. */
  compact?: boolean
}) {
  const [graph, setGraph] = useState<api.WikiGraph | null>(null)
  const [error, setError] = useState('')
  const [hidden, setHidden] = useState<Set<string>>(new Set())
  const [shownLinks, setShownLinks] = useState<Set<string>>(() => new Set(defaultLinkKinds))
  const [sources, setSources] = useState(false)
  const [chosenDepth, setDepth] = useState(2)
  const depth = compact ? 1 : chosenDepth
  const [palette, setPalette] = useState(readPalette)
  const [size, setSize] = useState({ width: 600, height: 480 })
  const [hoverId, setHoverId] = useState<string | null>(null)
  const frame = useRef<HTMLDivElement>(null)
  const fg = useRef<ForceGraphMethods<DrawnNode, GraphLink> | undefined>(undefined)

  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)')
    const update = () => setPalette(readPalette())
    media?.addEventListener('change', update)
    return () => media?.removeEventListener('change', update)
  }, [])
  useEffect(() => {
    const element = frame.current
    if (!element) return
    const measure = () => setSize({ width: Math.max(element.clientWidth, 200), height: Math.max(element.clientHeight, compact ? 160 : 320) })
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [compact])

  const kindsKey = [...hidden].toSorted().join(',')
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

  // The library mutates the data it is given, so it gets copies. Nodes stay the same objects when only the link filter changes.
  const nodes = useMemo<DrawnNode[]>(() => (graph?.nodes ?? []).map(node => ({ ...node })), [graph])
  const byId = useMemo(() => new Map(nodes.map(node => [node.id, node])), [nodes])
  const data = useMemo(() => ({
    nodes,
    // No edge may point at a missing node, and hiding a kind of link never removes a node.
    links: (graph?.edges ?? []).filter(edge => byId.has(edge.source) && byId.has(edge.target) && (compact || shownLinks.has(edge.kind))).map(edge => ({ ...edge })),
  }), [graph, nodes, byId, shownLinks, compact])

  const neighbours = useMemo(() => {
    const map = new Map<string, Set<string>>()
    for (const link of data.links) {
      const [a, b] = [endId(link.source), endId(link.target)]
      if (!map.has(a)) map.set(a, new Set())
      if (!map.has(b)) map.set(b, new Set())
      map.get(a)!.add(b)
      map.get(b)!.add(a)
    }
    return map
  }, [data])
  // The hovered node first, then its neighbours by decreasing degree.
  const focusOrder = useMemo(() => {
    if (!hoverId || !byId.has(hoverId)) return []
    const near = [...(neighbours.get(hoverId) ?? [])].filter(id => byId.has(id)).toSorted((a, b) => byId.get(b)!.degree - byId.get(a)!.degree)
    return [hoverId, ...near]
  }, [hoverId, neighbours, byId])
  const focus = useMemo(() => focusOrder.length ? new Set(focusOrder) : null, [focusOrder])

  // Spacing: charge, link distances, collisions, and in the full view documentation on the left and code on the right.
  useEffect(() => {
    const graphRef = fg.current
    if (!graphRef) return
    const factor = compact ? 0.8 : 1
    const radiusOf = (node: DrawnNode) => nodeRadius(node.kind, node.degree)
    graphRef.d3Force('charge')?.strength((node: DrawnNode) => -220 * (0.85 + radiusOf(node) / 50)).distanceMax(600)
    graphRef.d3Force('link')?.distance((link: GraphLink) => linkStyleOf(link.kind).distance * factor).strength((link: GraphLink) => linkStyleOf(link.kind).strength)
    graphRef.d3Force('collide', forceCollide<DrawnNode>(node => radiusOf(node) + 10) as never)
    graphRef.d3Force('x', compact ? null : forceX<DrawnNode>(node => sideOf(node.kind)).strength(0.07) as never)
    graphRef.d3Force('y', compact ? null : forceY<DrawnNode>(0).strength(0.05) as never)
    graphRef.d3ReheatSimulation()
  }, [data, compact])
  useEffect(() => {
    const timer = window.setTimeout(() => fg.current?.zoomToFit(400, 40), 150)
    return () => window.clearTimeout(timer)
  }, [data])

  const colourOf = (token: string) => palette[token] ?? palette['--color-muted']
  const linkAlpha = (link: GraphLink) => {
    const base = linkStyleOf(link.kind).alpha
    return focus && !(focus.has(endId(link.source)) && focus.has(endId(link.target))) ? base * fadedAlpha : base
  }

  function drawNode(node: DrawnNode, context: CanvasRenderingContext2D) {
    const style = nodeStyleOf(node.kind)
    const [x, y] = [node.x ?? 0, node.y ?? 0]
    const r = drawnRadius(node.kind, node.degree)
    const colour = colourOf(style.token)
    const surface = palette['--color-surface']
    context.save()
    context.globalAlpha = focus && !focus.has(node.id) ? fadedAlpha : 1
    // A surface outline keeps overlapping nodes apart.
    context.lineJoin = 'round'
    context.strokeStyle = surface
    context.lineWidth = 1.5
    context.stroke(new Path2D(shapePath(style.shape, x, y, r + 0.75)))
    if (style.hollow) {
      context.fillStyle = surface
      context.fill(new Path2D(shapePath(style.shape, x, y, r)))
      context.strokeStyle = colour
      context.lineWidth = style.strokeWidth
      context.stroke(new Path2D(shapePath(style.shape, x, y, r - style.strokeWidth / 2)))
    } else {
      context.fillStyle = colour
      context.fill(new Path2D(shapePath(style.shape, x, y, r)))
    }
    if (style.outerRing) {
      context.strokeStyle = colour
      context.lineWidth = 1.25
      context.stroke(new Path2D(shapePath('circle', x, y, r + 3.5)))
    }
    if (node.id === center) {
      context.strokeStyle = palette['--color-ink']
      context.lineWidth = 2
      context.stroke(new Path2D(shapePath('circle', x, y, r + (style.outerRing ? 7 : 4.5))))
    }
    context.restore()
  }
  function pointer(node: DrawnNode, colour: string, context: CanvasRenderingContext2D) {
    context.fillStyle = colour
    context.beginPath()
    context.arc(node.x ?? 0, node.y ?? 0, nodeRadius(node.kind, node.degree) + 4, 0, 2 * Math.PI)
    context.fill()
  }
  // Labels are drawn in one pass after the nodes, so that they can avoid each other.
  function drawLabels(context: CanvasRenderingContext2D, scale: number) {
    const ids = labelCandidates(data.nodes, { focus: focusOrder, centre: center, zoom: scale })
    context.save()
    context.font = labelFont(scale)
    const pad = 2 / scale
    const lineHeight = 12 * 1.25 / scale
    const boxes: (LabelBox & { text: string })[] = []
    for (const id of ids) {
      const node = byId.get(id)
      if (!node || node.x === undefined || node.y === undefined) continue
      const text = truncateLabel(node.label)
      const width = context.measureText(text).width + 2 * pad
      const extra = (node.id === center ? 6 : 2.5) / scale
      boxes.push({ id, text, width, height: lineHeight, left: node.x - width / 2, top: node.y + nodeRadius(node.kind, node.degree) + extra })
    }
    context.textAlign = 'center'
    context.textBaseline = 'top'
    context.lineJoin = 'round'
    context.lineWidth = 3 / scale
    for (const box of placeLabels(boxes, 2 / scale) as typeof boxes) {
      context.strokeStyle = palette['--color-surface']
      context.strokeText(box.text, box.left + box.width / 2, box.top + pad)
      context.fillStyle = palette['--color-ink']
      context.fillText(box.text, box.left + box.width / 2, box.top + pad)
    }
    context.restore()
  }

  const nodeKinds = (list: readonly string[]) => list.map(kind => <LegendToggle key={kind} pressed={!hidden.has(kind)} onToggle={() => setHidden(current => toggled(current, kind))} glyph={<NodeGlyph kind={kind} palette={palette} />}>{nodeStyleOf(kind).label}</LegendToggle>)
  return <div className="space-y-4">
    {!compact && <div className="flex flex-wrap items-center gap-x-6 gap-y-3">
      <label className="flex min-h-11 items-center gap-2 text-sm"><input type="checkbox" checked={sources} onChange={e => setSources(e.target.checked)} />Show sources</label>
      {center && <div className="flex flex-wrap items-center gap-2 text-sm">
        <span>Around <strong className="font-medium">{byId.get(center)?.label ?? center}</strong></span>
        <label className="flex items-center gap-2">Depth
          <select className="field w-auto py-1" value={depth} onChange={e => setDepth(Number(e.target.value))}>{[1, 2, 3, 4].map(value => <option key={value} value={value}>{value}</option>)}</select>
        </label>
        <Button variant="secondary" className="min-h-9 py-1.5" onClick={() => onCenterChange(null)}>Show the whole wiki</Button>
      </div>}
    </div>}
    {error && <Notice>{error}</Notice>}
    {!compact && graph?.truncated && <p role="status" className="rounded-control bg-subtle p-3 text-sm leading-6 text-muted">The graph is too large to be drawn in full. Hide some page kinds or show the neighbourhood of a page to see the rest.</p>}
    <div ref={frame} className={`overflow-hidden rounded-panel border border-line bg-surface ${compact ? 'h-56 lg:aspect-square lg:h-auto' : 'h-[28rem] lg:h-[34rem]'}`} role="img" aria-label="Graph of wiki pages and their links">
      {graph && !data.nodes.length ? <p className="p-5 text-sm text-muted">There is nothing to draw yet.</p> :
        <ForceGraph2D<DrawnNode, GraphLink> ref={fg} graphData={data} width={size.width} height={size.height} nodeId="id" cooldownTime={4000} warmupTicks={120}
          nodeRelSize={1} nodeVal={node => nodeRadius(node.kind, node.degree) ** 2}
          nodeLabel={node => `${escapeHtml(node.label)} · ${escapeHtml(nodeStyleOf(node.kind).label)}${node.summary ? '<br/>' + escapeHtml(node.summary) : ''}`}
          nodeCanvasObject={drawNode} nodePointerAreaPaint={pointer} onRenderFramePost={drawLabels}
          linkCurvature={0.12}
          linkColor={link => withAlpha(colourOf(linkStyleOf(link.kind).token), linkAlpha(link))}
          linkWidth={link => linkStyleOf(link.kind).width} linkLineDash={link => linkStyleOf(link.kind).dash}
          linkDirectionalArrowLength={link => linkStyleOf(link.kind).arrow} linkDirectionalArrowRelPos={1}
          linkDirectionalArrowColor={link => withAlpha(colourOf(linkStyleOf(link.kind).token), linkAlpha(link))}
          linkLabel={link => {
            const [from, to] = [byId.get(endId(link.source)), byId.get(endId(link.target))]
            return from && to ? escapeHtml(edgeSentence(link.kind, from.label, to.label)) : ''
          }}
          onNodeHover={node => setHoverId(node?.id ?? null)}
          onEngineStop={() => fg.current?.zoomToFit(400, 40)}
          onNodeClick={node => { if (!isRawSourceKind(node.kind)) onOpenPage(node.id) }} />}
    </div>
    {!compact && <div className="space-y-3" aria-label="Legend">
      <div className="grid gap-x-8 gap-y-3 md:grid-cols-2">
        <section aria-labelledby="legend-documentation">
          <h3 id="legend-documentation" className="mb-1 text-sm font-medium">Documentation</h3>
          <div className="flex flex-wrap gap-2">
            {nodeKinds(['overview', ...functionalKinds, 'source'])}
            {sources && ['document', 'code-file'].map(kind => <span key={kind} className="inline-flex min-h-11 items-center gap-2 px-2.5 text-sm text-muted"><NodeGlyph kind={kind} palette={palette} />{nodeStyleOf(kind).label}</span>)}
          </div>
        </section>
        <section aria-labelledby="legend-code">
          <h3 id="legend-code" className="mb-1 text-sm font-medium">Code</h3>
          <div className="flex flex-wrap gap-2">{nodeKinds(codeKinds)}</div>
        </section>
      </div>
      <section aria-labelledby="legend-links">
        <h3 id="legend-links" className="mb-1 text-sm font-medium">Links</h3>
        <div className="flex flex-wrap gap-2">{linkKindOrder.filter(kind => kind !== 'source' || sources).map(kind =>
          <LegendToggle key={kind} pressed={shownLinks.has(kind)} onToggle={() => setShownLinks(current => toggled(current, kind))} glyph={<LinkGlyph kind={kind} palette={palette} />}>{linkStyles[kind].label}</LegendToggle>)}
        </div>
      </section>
    </div>}
    <p className="text-xs leading-5 text-muted">{compact ? 'Dark arrows show which code implements what.' : 'Documentation is on the left, code on the right; dark arrows show which code implements what.'}</p>
  </div>
}

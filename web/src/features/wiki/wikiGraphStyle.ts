/** Style tables and pure helpers of the wiki graph. Colour tells the family, shape tells the kind. */

export type NodeFamily = 'functional' | 'code' | 'neutral'
export type NodeShape = 'circle' | 'square' | 'rounded-square' | 'hexagon' | 'diamond'

export interface NodeStyle {
  family: NodeFamily
  shape: NodeShape
  /** Name of the CSS colour token. */
  token: string
  /** Surface fill with a coloured stroke instead of a coloured fill. */
  hollow: boolean
  /** Stroke width of a hollow shape. */
  strokeWidth: number
  /** Drawn size relative to the node radius. */
  scale: number
  /** An extra ring around the shape. */
  outerRing?: boolean
  label: string
}

export const nodeStyles: Record<string, NodeStyle> = {
  feature: { family: 'functional', shape: 'circle', token: '--color-chart-terracotta', hollow: false, strokeWidth: 0, scale: 1, label: 'Feature' },
  rule: { family: 'functional', shape: 'circle', token: '--color-chart-terracotta', hollow: true, strokeWidth: 2.5, scale: 1, label: 'Business rule' },
  concept: { family: 'functional', shape: 'circle', token: '--color-chart-ochre', hollow: false, strokeWidth: 0, scale: 0.8, label: 'Concept' },
  module: { family: 'code', shape: 'rounded-square', token: '--color-chart-deep-teal', hollow: false, strokeWidth: 0, scale: 1, label: 'Module' },
  component: { family: 'code', shape: 'square', token: '--color-chart-teal', hollow: false, strokeWidth: 0, scale: 1, label: 'Component' },
  'data-entity': { family: 'code', shape: 'square', token: '--color-chart-teal', hollow: true, strokeWidth: 2.5, scale: 1, label: 'Data entity' },
  api: { family: 'code', shape: 'hexagon', token: '--color-chart-teal', hollow: false, strokeWidth: 0, scale: 1, label: 'API' },
  overview: { family: 'neutral', shape: 'circle', token: '--color-ink', hollow: false, strokeWidth: 0, scale: 1, outerRing: true, label: 'Overview' },
  source: { family: 'neutral', shape: 'diamond', token: '--color-muted', hollow: true, strokeWidth: 1.5, scale: 1, label: 'Source' },
  document: { family: 'neutral', shape: 'diamond', token: '--color-muted', hollow: true, strokeWidth: 1.5, scale: 1, label: 'Document' },
  'code-file': { family: 'neutral', shape: 'diamond', token: '--color-muted', hollow: true, strokeWidth: 1.5, scale: 1, label: 'Code file' },
}
const fallbackNodeStyle = nodeStyles.source
export const nodeStyleOf = (kind: string): NodeStyle => nodeStyles[kind] ?? fallbackNodeStyle

export const functionalKinds = ['feature', 'rule', 'concept'] as const
export const codeKinds = ['module', 'component', 'data-entity', 'api'] as const
export const isCodeKind = (kind: string) => nodeStyleOf(kind).family === 'code'
export const isSourceKind = (kind: string) => kind === 'source' || kind === 'document' || kind === 'code-file'
/** Documents and code files are not wiki pages, so there is nothing to open. */
export const isRawSourceKind = (kind: string) => kind === 'document' || kind === 'code-file'

/** Node radius in graph units: grows with the number of links, sources have a fixed size. */
export function nodeRadius(kind: string, degree: number): number {
  if (isSourceKind(kind)) return 4
  const safe = Number.isFinite(degree) ? Math.max(degree, 0) : 0
  return 5 + 2.2 * Math.sqrt(Math.min(safe, 30)) + (kind === 'module' ? 3 : 0)
}

/** Size of the drawn shape, which can be smaller than the collision radius. */
export const drawnRadius = (kind: string, degree: number) => nodeRadius(kind, degree) * nodeStyleOf(kind).scale

const round = (value: number) => Math.round(value * 100) / 100

/** SVG path data of a node shape, used both by the canvas (through Path2D) and by the legend glyphs. */
export function shapePath(shape: NodeShape, cx: number, cy: number, r: number): string {
  const [x, y, s] = [round(cx), round(cy), round(r)]
  switch (shape) {
    case 'circle':
      return `M${round(x - s)} ${y}a${s} ${s} 0 1 0 ${round(2 * s)} 0a${s} ${s} 0 1 0 ${round(-2 * s)} 0Z`
    case 'diamond':
      return `M${x} ${round(y - s)}L${round(x + s)} ${y}L${x} ${round(y + s)}L${round(x - s)} ${y}Z`
    case 'hexagon': {
      const points = Array.from({ length: 6 }, (_, i) => {
        const angle = Math.PI / 6 + i * Math.PI / 3
        return `${round(x + s * Math.cos(angle))} ${round(y + s * Math.sin(angle))}`
      })
      return `M${points.join('L')}Z`
    }
    case 'rounded-square':
    case 'square': {
      const half = round(s * 0.88)
      const corner = shape === 'rounded-square' ? Math.min(round(half * 0.35), 4) : 0
      const [l, t, rr, b] = [round(x - half), round(y - half), round(x + half), round(y + half)]
      if (!corner) return `M${l} ${t}L${rr} ${t}L${rr} ${b}L${l} ${b}Z`
      return `M${round(l + corner)} ${t}L${round(rr - corner)} ${t}Q${rr} ${t} ${rr} ${round(t + corner)}L${rr} ${round(b - corner)}Q${rr} ${b} ${round(rr - corner)} ${b}`
        + `L${round(l + corner)} ${b}Q${l} ${b} ${l} ${round(b - corner)}L${l} ${round(t + corner)}Q${l} ${t} ${round(l + corner)} ${t}Z`
    }
  }
}

export interface LinkStyle {
  /** Name of the CSS colour token. */
  token: string
  alpha: number
  width: number
  dash: number[] | null
  /** Length of the arrow head, 0 for none. */
  arrow: number
  /** Distance the force layout tries to keep between the ends. */
  distance: number
  /** Force strength; the noisy kinds pull less. */
  strength: number
  label: string
}

export const linkStyles: Record<string, LinkStyle> = {
  implements: { token: '--color-ink', alpha: 1, width: 2, dash: null, arrow: 6, distance: 120, strength: 0.5, label: 'Implements' },
  'part-of': { token: '--color-chart-deep-teal', alpha: 0.45, width: 1, dash: null, arrow: 0, distance: 45, strength: 0.7, label: 'Part of' },
  'depends-on': { token: '--color-chart-deep-teal', alpha: 0.45, width: 1, dash: [5, 4], arrow: 3.5, distance: 110, strength: 0.5, label: 'Depends on' },
  related: { token: '--color-muted', alpha: 0.35, width: 0.75, dash: null, arrow: 0, distance: 100, strength: 0.12, label: 'Related' },
  mentions: { token: '--color-muted', alpha: 0.25, width: 0.75, dash: [1, 3], arrow: 0, distance: 130, strength: 0.08, label: 'Mentions' },
  source: { token: '--color-muted', alpha: 0.3, width: 0.75, dash: [1, 3], arrow: 0, distance: 50, strength: 0.5, label: 'Source' },
}
export const linkKindOrder = ['implements', 'part-of', 'depends-on', 'related', 'mentions', 'source'] as const
/** Link kinds drawn by default in the full view; related and mentions are noise until asked for. */
export const defaultLinkKinds: readonly string[] = ['implements', 'part-of', 'depends-on', 'source']
export const linkStyleOf = (kind: string): LinkStyle => linkStyles[kind] ?? linkStyles.related

/** The relation as a sentence, for the tooltip of an edge. */
export function edgeSentence(kind: string, source: string, target: string): string {
  switch (kind) {
    case 'implements': return `${source} implements ${target}`
    case 'part-of': return `${source} is part of ${target}`
    case 'depends-on': return `${source} depends on ${target}`
    case 'related': return `${source} is related to ${target}`
    case 'mentions': return `${source} mentions ${target}`
    case 'source': return `${source} is based on ${target}`
    default: return `${source} and ${target}`
  }
}

export const maxLabelLength = 28
export function truncateLabel(text: string, max = maxLabelLength): string {
  const chars = Array.from(text)
  return chars.length <= max ? text : chars.slice(0, Math.max(max - 1, 0)).join('').trimEnd() + '…'
}

/** `#rgb` or `#rrggbb` with an opacity; any other colour syntax is returned unchanged. */
export function withAlpha(colour: string, alpha: number): string {
  const match = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(colour.trim())
  if (!match) return colour
  const hex = match[1].length === 3 ? match[1].split('').map(c => c + c).join('') : match[1]
  const [r, g, b] = [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16))
  return `rgba(${r}, ${g}, ${b}, ${Math.round(alpha * 1000) / 1000})`
}

export interface LabelBox { id: string; left: number; top: number; width: number; height: number }

const intersects = (a: LabelBox, b: LabelBox, gap: number) =>
  a.left < b.left + b.width + gap && b.left < a.left + a.width + gap && a.top < b.top + b.height + gap && b.top < a.top + a.height + gap

/** Greedy placement: boxes arrive by decreasing priority, and one that overlaps a box already kept is dropped. */
export function placeLabels(boxes: LabelBox[], gap = 0): LabelBox[] {
  const placed: LabelBox[] = []
  for (const box of boxes) if (!placed.some(other => intersects(box, other, gap))) placed.push(box)
  return placed
}

/** Below this zoom only the most important labels are considered. */
export const labelZoomThreshold = 0.9
export const sparseLabelCount = 12

/**
 * Ids of the nodes that may get a label, by decreasing priority: the hovered node and its neighbours, the centre,
 * then the most linked nodes. While hovering, only the focused nodes are labelled, since the rest is faded.
 */
export function labelCandidates(nodes: { id: string; degree: number }[], options: { focus: string[]; centre: string | null; zoom: number }): string[] {
  const known = new Set(nodes.map(node => node.id))
  const focus = options.focus.filter(id => known.has(id))
  if (focus.length) return [...new Set(focus)]
  const byDegree = [...nodes].sort((a, b) => b.degree - a.degree).map(node => node.id)
  const ordered = options.centre && known.has(options.centre) ? [options.centre, ...byDegree.filter(id => id !== options.centre)] : byDegree
  return options.zoom < labelZoomThreshold ? ordered.slice(0, (options.centre && known.has(options.centre) ? 1 : 0) + sparseLabelCount) : ordered
}

/** Horizontal target of a kind in the full view: documentation on the left, code on the right. */
export const sideOf = (kind: string): -260 | 0 | 260 => {
  const family = nodeStyleOf(kind).family
  return family === 'functional' ? -260 : family === 'code' ? 260 : 0
}

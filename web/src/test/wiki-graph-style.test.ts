import { describe, expect, it } from 'vitest'
import { kindOrder } from '../features/wiki/wikiMarkdown'
import {
  edgeSentence, labelCandidates, linkKindOrder, linkStyles, nodeAlpha, nodeRadius, nodeStyleOf, nodeStyles, placeLabels, shapePath, sideOf, truncateLabel, withAlpha,
} from '../features/wiki/wikiGraphStyle'

describe('node radius', () => {
  it('stays within bounds and grows with the degree', () => {
    expect(nodeRadius('concept', 0)).toBe(5)
    expect(nodeRadius('concept', -3)).toBe(5)
    expect(nodeRadius('concept', 4)).toBeGreaterThan(nodeRadius('concept', 1))
    expect(nodeRadius('concept', 30)).toBeCloseTo(5 + 2.2 * Math.sqrt(30))
    expect(nodeRadius('concept', 5000)).toBe(nodeRadius('concept', 30))
  })
  it('lets features and modules grow faster and further with their links', () => {
    expect(nodeRadius('feature', 0)).toBe(5)
    expect(nodeRadius('feature', 9)).toBeGreaterThan(nodeRadius('rule', 9))
    expect(nodeRadius('feature', 60)).toBeCloseTo(5 + 3.2 * Math.sqrt(60))
    expect(nodeRadius('feature', 5000)).toBe(nodeRadius('feature', 60))
    expect(nodeRadius('feature', 60)).toBeGreaterThan(nodeRadius('feature', 30))
  })
  it('makes modules larger and sources fixed', () => {
    expect(nodeRadius('module', 9)).toBe(nodeRadius('feature', 9) + 3)
    expect(nodeRadius('module', 9)).toBeGreaterThan(nodeRadius('component', 9))
    for (const kind of ['source', 'document', 'code-file']) expect(nodeRadius(kind, 25)).toBe(4)
  })
})

describe('node opacity', () => {
  it('fades hubs with few links and shows well-linked hubs in full colour', () => {
    expect(nodeAlpha('feature', 0)).toBe(0.5)
    expect(nodeAlpha('module', 3)).toBeGreaterThan(nodeAlpha('module', 1))
    expect(nodeAlpha('module', 12)).toBe(1)
    expect(nodeAlpha('feature', 500)).toBe(1)
    expect(nodeAlpha('feature', Number.NaN)).toBe(0.5)
  })
  it('keeps the other kinds opaque', () => {
    for (const kind of ['rule', 'concept', 'component', 'api', 'overview', 'source']) expect(nodeAlpha(kind, 0), kind).toBe(1)
  })
})

describe('styles', () => {
  it('gives every page kind and source kind a node style', () => {
    for (const kind of [...kindOrder, 'document', 'code-file']) expect(nodeStyles[kind], kind).toBeDefined()
  })
  it('colours by family: warm functional, cool code, neutral others', () => {
    expect(nodeStyleOf('feature').token).toBe('--color-chart-terracotta')
    expect(nodeStyleOf('concept').token).toBe('--color-chart-ochre')
    expect(nodeStyleOf('module').token).toBe('--color-chart-deep-teal')
    expect(nodeStyleOf('api').shape).toBe('hexagon')
    expect(sideOf('rule')).toBe(-260)
    expect(sideOf('data-entity')).toBe(260)
    expect(sideOf('overview')).toBe(0)
  })
  it('gives every link kind a style, with implements discreet at rest and clear on hover', () => {
    for (const kind of linkKindOrder) expect(linkStyles[kind], kind).toBeDefined()
    expect(linkStyles.implements.alpha).toBeLessThan(1)
    expect(linkStyles.implements.alpha).toBeGreaterThan(linkStyles.related.alpha)
    expect(linkStyles.implements.focusAlpha).toBeGreaterThan(linkStyles.implements.alpha)
    expect(linkStyles.implements.width).toBeGreaterThan(linkStyles.related.width)
    expect(linkStyles.implements.width).toBeLessThan(2)
    expect(linkStyles.implements.arrow).toBeGreaterThan(0)
    expect(linkStyles['depends-on'].dash).toEqual([5, 4])
    expect(linkStyles.related.strength).toBeLessThan(linkStyles['part-of'].strength)
  })
  it('builds closed path data for each shape', () => {
    for (const shape of ['circle', 'square', 'rounded-square', 'hexagon', 'diamond'] as const) expect(shapePath(shape, 0, 0, 5)).toMatch(/^M.*Z$/)
  })
  it('converts hex colours to rgba and leaves others alone', () => {
    expect(withAlpha('#ff0000', 0.5)).toBe('rgba(255, 0, 0, 0.5)')
    expect(withAlpha('#f00', 1)).toBe('rgba(255, 0, 0, 1)')
    expect(withAlpha('red', 0.5)).toBe('red')
  })
})

describe('edge sentences', () => {
  it('reads each relation', () => {
    expect(edgeSentence('implements', 'InvoiceService', 'Invoicing')).toBe('InvoiceService implements Invoicing')
    expect(edgeSentence('part-of', 'A', 'B')).toBe('A is part of B')
    expect(edgeSentence('depends-on', 'A', 'B')).toBe('A depends on B')
    expect(edgeSentence('related', 'A', 'B')).toBe('A is related to B')
    expect(edgeSentence('mentions', 'A', 'B')).toBe('A mentions B')
  })
})

describe('labels', () => {
  const box = (id: string, left: number, top = 0, width = 40, height = 12) => ({ id, left, top, width, height })
  it('truncates long labels with an ellipsis', () => {
    expect(truncateLabel('Short')).toBe('Short')
    expect(truncateLabel('a'.repeat(28))).toBe('a'.repeat(28))
    const cut = truncateLabel('a'.repeat(40))
    expect(cut).toBe('a'.repeat(27) + '…')
    expect(Array.from(cut)).toHaveLength(28)
  })
  it('keeps all labels that do not overlap', () => {
    expect(placeLabels([box('a', 0), box('b', 50), box('c', 0, 20)]).map(b => b.id)).toEqual(['a', 'b', 'c'])
  })
  it('lets the higher priority label win an overlap', () => {
    expect(placeLabels([box('first', 0), box('second', 20), box('third', 100)]).map(b => b.id)).toEqual(['first', 'third'])
    expect(placeLabels([box('second', 20), box('first', 0)]).map(b => b.id)).toEqual(['second'])
  })
  it('treats the gap as part of the overlap test', () => {
    expect(placeLabels([box('a', 0), box('b', 42)], 0)).toHaveLength(2)
    expect(placeLabels([box('a', 0), box('b', 42)], 4)).toHaveLength(1)
  })
  const nodes = Array.from({ length: 20 }, (_, i) => ({ id: `n${i}`, degree: i }))
  it('orders candidates by hover focus, centre, then degree, and thins them out when zoomed out', () => {
    const far = labelCandidates(nodes, { focus: [], centre: 'n2', zoom: 0.5 })
    expect(far).toHaveLength(13)
    expect(far.slice(0, 3)).toEqual(['n2', 'n19', 'n18'])
    const near = labelCandidates(nodes, { focus: [], centre: null, zoom: 1 })
    expect(near).toHaveLength(20)
    expect(near[0]).toBe('n19')
    expect(labelCandidates(nodes, { focus: ['n3', 'n7'], centre: 'n2', zoom: 2 })).toEqual(['n3', 'n7'])
  })
})

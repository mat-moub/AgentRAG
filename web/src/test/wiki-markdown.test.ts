import { describe, expect, it } from 'vitest'
import { parseWikiHref, wikiLinksToMarkdown, wikiSlug, wikiUrlTransform } from '../features/wiki/wikiMarkdown'

describe('wiki slugs', () => {
  it('lower-cases, removes accents and collapses other characters', () => {
    expect(wikiSlug('Règles de Facturation')).toBe('regles-de-facturation')
    expect(wikiSlug('  --Order_Service / v2!  ')).toBe('order-service-v2')
    expect(wikiSlug('???')).toBe('')
  })
})

describe('wiki link conversion', () => {
  it('converts links with and without a label', () => {
    expect(wikiLinksToMarkdown('See [[order-service|the order service]] and [[Règle de Calcul]].'))
      .toBe('See [the order service](wiki:order-service) and [Règle de Calcul](wiki:regle-de-calcul).')
  })
  it('ignores links whose slug is empty', () => {
    expect(wikiLinksToMarkdown('[[???]]')).toBe('[[???]]')
  })
  it('leaves code spans and fenced blocks untouched', () => {
    const text = 'Use `[[a]]` here, [[b]]\n```\n[[c]]\n```\nthen [[d]]'
    expect(wikiLinksToMarkdown(text)).toBe('Use `[[a]]` here, [b](wiki:b)\n```\n[[c]]\n```\nthen [d](wiki:d)')
  })
  it('keeps doc and repo citations intact', () => {
    const text = '[Guide · p.12](doc:123#p12) [a b.cs:107](repo:5/src/a%20b.cs#L107) [[x]]'
    expect(wikiLinksToMarkdown(text)).toBe('[Guide · p.12](doc:123#p12) [a b.cs:107](repo:5/src/a%20b.cs#L107) [x](wiki:x)')
  })
})

describe('wiki hrefs', () => {
  it('keeps wiki, doc and repo urls through the url transform', () => {
    for (const url of ['wiki:x', 'doc:1#p2', 'repo:5/a%20b.cs#L1']) expect(wikiUrlTransform(url)).toBe(url)
    expect(wikiUrlTransform('javascript:alert(1)')).toBe('')
  })
  it('parses citations', () => {
    expect(parseWikiHref('wiki:order')).toEqual({ type: 'page', slug: 'order' })
    expect(parseWikiHref('doc:123#p12')).toEqual({ type: 'document', documentId: 123, passage: 'p12' })
    expect(parseWikiHref('repo:5/src/a%20b.cs#L107')).toEqual({ type: 'repository', repositoryId: 5, path: 'src/a b.cs', fragment: 'L107' })
    expect(parseWikiHref('https://example.com')).toEqual({ type: 'external', url: 'https://example.com' })
  })
})

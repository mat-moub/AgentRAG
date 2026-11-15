import { defaultUrlTransform } from 'react-markdown'
import type { WikiLinkKind, WikiPageKind } from '../../api'

/** Same normalisation as the server's slugs: lower case, accents removed, anything but letters and digits collapsed to '-', trimmed. */
export function wikiSlug(text: string): string {
  return text.normalize('NFD').replace(/\p{M}+/gu, '').toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '')
}

// Fenced blocks, inline code spans and wiki links, in this order of precedence, so links inside code stay untouched.
const tokens = /(?:^|\n)[ \t]*(```|~~~)[\s\S]*?(?:\n[ \t]*\1|$)|(`+)[\s\S]*?\2|\[\[([^\]\n|]+)(?:\|([^\]\n]*))?\]\]/g

/** Turns `[[slug|label]]` and `[[slug]]` into markdown links using the `wiki:` scheme. Code is left as is. */
export function wikiLinksToMarkdown(markdown: string): string {
  return markdown.replace(tokens, (match: string, _fence?: string, _ticks?: string, target?: string, label?: string) => {
    if (target === undefined) return match
    const slug = wikiSlug(target)
    if (!slug) return match
    const text = (label?.trim() || target.trim()).replace(/[[\]\\]/g, '\\$&')
    return `[${text}](wiki:${slug})`
  })
}

/** react-markdown strips unknown schemes by default; keep the wiki and citation ones. */
export const wikiUrlTransform = (url: string) => /^(wiki|doc|repo):/i.test(url) ? url : defaultUrlTransform(url)

export type WikiHref =
  | { type: 'page'; slug: string }
  | { type: 'document'; documentId: number; passage: string | null }
  | { type: 'repository'; repositoryId: number; path: string; fragment: string | null }
  | { type: 'external'; url: string }
  | { type: 'unknown' }

export function parseWikiHref(href: string | undefined): WikiHref {
  if (!href) return { type: 'unknown' }
  if (/^wiki:/i.test(href)) return { type: 'page', slug: href.slice(5) }
  const document = /^doc:(\d+)(?:#(.*))?$/i.exec(href)
  if (document) return { type: 'document', documentId: Number(document[1]), passage: document[2] || null }
  const repository = /^repo:(\d+)\/([^#]*)(?:#(.*))?$/i.exec(href)
  if (repository) {
    let path = repository[2]
    try { path = decodeURIComponent(path) } catch { /* keep the raw path */ }
    return { type: 'repository', repositoryId: Number(repository[1]), path, fragment: repository[3] || null }
  }
  if (/^https?:\/\//i.test(href)) return { type: 'external', url: href }
  return { type: 'unknown' }
}

export const kindLabels: Record<WikiPageKind, string> = {
  overview: 'Overview', feature: 'Features', rule: 'Business rules', concept: 'Concepts', module: 'Modules',
  component: 'Components', 'data-entity': 'Data entities', api: 'APIs', source: 'Sources',
}
export const kindOrder = Object.keys(kindLabels) as WikiPageKind[]
const kindNames: Record<string, string> = {
  overview: 'Overview', feature: 'Feature', rule: 'Business rule', concept: 'Concept', module: 'Module', component: 'Component',
  'data-entity': 'Data entity', api: 'API', source: 'Source', document: 'Document', 'code-file': 'Code file',
}
export const kindName = (kind: string) => kindNames[kind] ?? kind
export const linkLabels: Record<WikiLinkKind, { out: string; back: string }> = {
  related: { out: 'Related', back: 'Related' },
  'part-of': { out: 'Part of', back: 'Contains' },
  'depends-on': { out: 'Depends on', back: 'Used by' },
  implements: { out: 'Implements', back: 'Implemented by' },
  mentions: { out: 'Mentions', back: 'Mentioned by' },
}

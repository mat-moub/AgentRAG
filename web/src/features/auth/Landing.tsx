import { Suspense, lazy, useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import {
  ArrowRightIcon, BookOpenTextIcon, DatabaseIcon, FileMagnifyingGlassIcon, GitBranchIcon, MagnifyingGlassIcon,
  PlugsConnectedIcon, QuotesIcon, ScissorsIcon, TreeStructureIcon, UploadSimpleIcon,
} from '@phosphor-icons/react'
import { nodeStyleOf } from '../wiki/wikiGraphStyle'
import { sampleNeighbours, samplePage, sampleWiki } from './sampleWiki'

const reducedMotion = () => typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches

/**
 * True once the element has scrolled into view. Without IntersectionObserver (tests, old browsers) or with reduced
 * motion, content is shown at once: an animation never decides whether something can be read.
 */
function useInView<T extends Element>() {
  const ref = useRef<T>(null)
  const [inView, setInView] = useState(() => typeof IntersectionObserver === 'undefined' || reducedMotion())
  useEffect(() => {
    if (inView || !ref.current) return
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { setInView(true); observer.disconnect() }
    }, { threshold: 0.2 })
    observer.observe(ref.current)
    return () => observer.disconnect()
  }, [inView])
  return [ref, inView] as const
}

/** Fades and lifts its content in when it scrolls into view; `delay` staggers siblings. */
function Reveal({ children, delay = 0, className = '' }: { children: ReactNode; delay?: number; className?: string }) {
  const [ref, inView] = useInView<HTMLDivElement>()
  return <div ref={ref} className={`reveal ${inView ? 'reveal-in' : ''} ${className}`} style={{ transitionDelay: `${delay}ms` }}>{children}</div>
}

/** Counts up to a number once in view. Screen readers get the final value only. */
function CountUp({ value, suffix = '', prefix = '' }: { value: number; suffix?: string; prefix?: string }) {
  const [ref, inView] = useInView<HTMLSpanElement>()
  const [shown, setShown] = useState(0)
  // Without animation the final value is shown at once, derived here rather than set from the effect.
  const instant = reducedMotion() || typeof requestAnimationFrame === 'undefined'
  useEffect(() => {
    if (!inView || instant) return
    let frame = 0
    const start = performance.now()
    const tick = (now: number) => {
      const progress = Math.min(1, (now - start) / 1100)
      setShown(Math.round(value * (1 - Math.pow(1 - progress, 3))))
      if (progress < 1) frame = requestAnimationFrame(tick)
    }
    frame = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(frame)
  }, [inView, instant, value])
  return <span ref={ref}><span aria-hidden="true">{prefix}{instant ? value : shown}{suffix}</span><span className="sr-only">{prefix}{value}{suffix}</span></span>
}

const highlights = [
  { value: 35, prefix: '~', suffix: '%', label: 'fewer tokens', detail: 'than a keyword search for the same complete answer' },
  { value: 0, prefix: '', suffix: '', label: 'noise passages to sort', detail: 'where a broad keyword matched 365' },
  { value: 1, prefix: '', suffix: ' call', label: 'from a business rule to its code', detail: 'file and line included' },
  { value: 3, prefix: '~', suffix: '×', label: 'lighter database', detail: 'compact half-precision vectors (estimate)' },
]

/** The facts a complete answer to the benchmark question holds, each checked against the source code. */
const facts = [
  'Gross receipts: rent × 12 × occupancy', 'Operating charges and their yearly growth', 'Depreciation by building component',
  'Notary fees: expense, capitalised or none', 'Loan payment formula and yearly schedule', '20-year projection with first-year proration',
  'Micro-BIC allowances and thresholds', 'Real regime: depreciation capped and carried forward', 'Tax: marginal rate + 18.6% social contributions',
]

const benchmark = [
  { name: 'AgentRAG', how: 'Wiki, file outline, targeted reading', tokens: 13_000, noise: 0, found: [1, 1, 1, 1, 1, 1, 1, 1, 1], ours: true },
  { name: 'Keyword search', how: 'Grep, then reading files', tokens: 20_000, noise: 365, found: [1, 1, 1, 1, 1, 1, 1, 1, 1] },
  { name: 'Wiki alone', how: 'First version, pages only', tokens: 7_000, noise: 0, found: [0, 0, 0, 1, 0, 1, 0, 1, 0] },
  { name: 'Semantic search', how: 'Vector search only', tokens: 5_500, noise: 0, found: [0, 0, 0, 0, 0, 0, 0, 1, 0] },
].map(row => {
  const count = row.found.filter(Boolean).length
  return { ...row, count, perFact: Math.round(row.tokens / count) }
})

const thousands = (value: number) => `${(value / 1000).toLocaleString('en-US', { maximumFractionDigits: 1 })}k`

/**
 * Precision and cost side by side: facts found (higher is better) and tokens read per fact found (lower is better).
 * Reading more does not answer better: AgentRAG finds every fact for the fewest tokens per fact.
 */
function BenchmarkComparison() {
  const [ref, inView] = useInView<HTMLDivElement>()
  const maxPerFact = Math.max(...benchmark.map(row => row.perFact))
  return <div ref={ref}>
    <div className="hidden grid-cols-[minmax(0,11rem)_1fr_1fr] gap-6 pb-3 text-xs text-muted sm:grid">
      <span />
      <span>Facts of the answer found <strong className="font-medium text-ink">· higher is better</strong></span>
      <span>Tokens read per fact found <strong className="font-medium text-ink">· lower is better</strong></span>
    </div>
    <ol className="divide-y divide-line border-y border-line" aria-label="Benchmark: facts found and tokens read per fact, per approach">
      {benchmark.map((row, index) => <li key={row.name} className={`grid gap-3 py-4 sm:grid-cols-[minmax(0,11rem)_1fr_1fr] sm:items-center sm:gap-6 ${row.ours ? 'benchmark-ours' : ''}`}>
        <div>
          <p className={`font-semibold ${row.ours ? 'text-accent' : ''}`}>{row.name}</p>
          <p className="text-xs text-muted">{row.how}</p>
        </div>
        <div>
          <p className="mb-1 text-xs text-muted sm:hidden">Facts found · higher is better</p>
          <div className="flex items-center gap-3">
            <div className="flex flex-1 gap-1" aria-hidden="true">
              {row.found.map((hit, fact) => <span key={facts.at(fact)} className={`benchmark-fact h-3 flex-1 rounded-[3px] ${hit ? (row.ours ? 'bg-accent' : 'bg-ink/55') : 'bg-subtle'}`}
                style={{ transitionDelay: `${index * 120 + fact * 45}ms`, opacity: inView ? 1 : 0, transform: inView ? 'none' : 'scaleY(0.2)' }} />)}
            </div>
            <span className={`w-10 text-right text-sm tabular-nums ${row.ours ? 'font-semibold' : ''}`}>{row.count}/{facts.length}</span>
          </div>
        </div>
        <div>
          <p className="mb-1 text-xs text-muted sm:hidden">Tokens per fact found · lower is better</p>
          <div className="flex items-center gap-3">
            <div className="h-3 flex-1 overflow-hidden rounded-full bg-subtle" aria-hidden="true">
              <div className={`benchmark-bar h-full rounded-full ${row.ours ? 'bg-accent' : 'bg-ink/35'}`}
                style={{ width: inView ? `${row.perFact / maxPerFact * 100}%` : '0%', transitionDelay: `${index * 120}ms` }} />
            </div>
            <span className={`w-12 text-right text-sm tabular-nums ${row.ours ? 'font-semibold' : ''}`}>{thousands(row.perFact)}</span>
          </div>
          <p className="mt-1 text-xs text-muted tabular-nums">{thousands(row.tokens)} tokens in all · {row.noise ? `${row.noise} noise passages` : 'no noise'}</p>
        </div>
        <span className="sr-only">{row.name}: {row.count} of {facts.length} facts found, about {row.perFact} tokens read per fact found, {row.tokens} tokens in all, {row.noise} noise passages.</span>
      </li>)}
    </ol>
    <details className="mt-4 text-sm">
      <summary className="inline-flex min-h-11 cursor-pointer items-center font-medium underline decoration-line underline-offset-4">See the {facts.length} facts checked</summary>
      <div className="mt-3 overflow-x-auto">
        <table className="w-full min-w-[34rem] text-left text-xs">
          <thead><tr className="border-b border-line text-muted"><th className="py-2 pr-3 font-medium">Fact of the answer</th>{benchmark.map(row => <th key={row.name} className="px-2 py-2 text-center font-medium">{row.name}</th>)}</tr></thead>
          <tbody>{facts.map((fact, index) => <tr key={fact} className="border-b border-line/70">
            <td className="py-2 pr-3">{fact}</td>
            {benchmark.map(row => <td key={row.name} className={`px-2 py-2 text-center ${row.found.at(index) ? (row.ours ? 'font-semibold text-accent' : '') : 'text-muted'}`}>{row.found.at(index) ? '✓' : '–'}<span className="sr-only">{row.found.at(index) ? ' found' : ' missing'}</span></td>)}
          </tr>)}</tbody>
        </table>
      </div>
    </details>
  </div>
}

const indexing = [
  { icon: UploadSimpleIcon, title: 'Add your sources', text: 'Upload PDF, Markdown, text or CSV files up to 10 MB, paste text, or connect a GitHub or Azure DevOps repository.' },
  { icon: ScissorsIcon, title: 'Split into passages', text: 'Prose becomes passages of about 800 characters that overlap slightly; code is split on line ends, so every passage knows its line.' },
  { icon: DatabaseIcon, title: 'Index for meaning and words', text: 'Documentation gets compact 512-value embeddings stored at half precision; every passage also gets a trigram index for exact search.' },
  { icon: TreeStructureIcon, title: 'Compile the wiki', text: 'A language model writes interlinked pages from each folder and refreshes only what changed, with a time estimate for every step.' },
]

const ladder = [
  { icon: BookOpenTextIcon, title: 'Wiki', text: 'Find the page by words or meaning, then jump to the code or document it cites.' },
  { icon: FileMagnifyingGlassIcon, title: 'File outline', text: 'Declarations and constants of a file with their line, for a few hundred tokens.' },
  { icon: MagnifyingGlassIcon, title: 'Exact search', text: 'An identifier, an article number, an acronym. Declarations come first.' },
  { icon: QuotesIcon, title: 'Semantic search', text: 'A natural-language question across your written documents.' },
  { icon: ArrowRightIcon, title: 'Targeted reading', text: 'Two to four passages around the answer, read before anything is quoted.' },
]

const audiences = [
  { title: 'Developers', text: 'Understand a module without reading it all: the outline leads straight to the function.' },
  { title: 'Product owners', text: 'Write user stories that match what the product and its code really do.' },
  { title: 'Legal and business teams', text: 'Find an article by its number, or a notion by its meaning, with the exact quote.' },
  { title: 'AI assistants', text: 'Claude and Codex connect through MCP and are guided to the right source with fewer tokens.' },
]

// The graph library is large: it is only downloaded when the wiki section is reached, never for the sign-in form.
const WikiGraph = lazy(() => import('../wiki/WikiGraph').then(module => ({ default: module.WikiGraph })))

const linkPhrases: Record<string, [outgoing: string, incoming: string]> = {
  implements: ['Implements', 'Implemented by'], 'part-of': ['Part of', 'Contains'], 'depends-on': ['Depends on', 'Used by'],
  related: ['Related to', 'Related to'], mentions: ['Mentions', 'Mentioned by'],
}

/** A live, explorable graph of a real wiki: hover a page to see its links, select one to list them. */
function SampleWikiGraph() {
  const [ref, inView] = useInView<HTMLDivElement>()
  const [selected, setSelected] = useState('simulation-lmnp')
  const page = samplePage(selected)
  // Implementation links first: they are what joins the documentation to the code.
  const neighbours = sampleNeighbours(selected).toSorted((a, b) => Number(b.kind === 'implements') - Number(a.kind === 'implements')).slice(0, 8)
  const total = sampleNeighbours(selected).length
  // The graph draws on a canvas sized by a ResizeObserver; where those are missing, the picture of a real wiki stands in.
  const canDraw = typeof ResizeObserver !== 'undefined' && typeof IntersectionObserver !== 'undefined'
  return <div ref={ref} className="grid gap-5 lg:grid-cols-[3fr_1fr]">
    <div>
      {!canDraw && <img src="/images/preview-wiki.png" alt="Graph of an LLM wiki: documentation pages in warm colours linked to code modules in teal" className="w-full rounded-panel border border-line bg-surface object-contain p-4" loading="lazy" />}
      {canDraw && inView && <Suspense fallback={<p role="status" className="flex h-[28rem] items-center justify-center rounded-panel border border-line bg-surface text-sm text-muted">Loading the example wiki…</p>}>
        <WikiGraph sample={sampleWiki} version={null} center={null} onCenterChange={() => {}} onOpenPage={setSelected} />
      </Suspense>}
    </div>
    <aside aria-live="polite" className="h-fit rounded-panel border border-line bg-surface p-5">
      <p className="eyebrow">Real wiki · selected page</p>
      {page && <>
        <h3 className="mt-3 text-lg font-semibold break-words">{page.label}</h3>
        <p className="mt-1 text-xs text-muted">{nodeStyleOf(page.kind).label} · {total} links</p>
        <ul className="mt-3 space-y-2 text-sm">
          {neighbours.map(({ page: other, kind, outgoing }) => <li key={`${kind}-${other.id}`}>
            <span className="block text-xs text-muted">{linkPhrases[kind]?.[outgoing ? 0 : 1] ?? kind}</span>
            <button type="button" className="text-left font-medium break-words underline decoration-line underline-offset-4 hover:decoration-ink" onClick={() => setSelected(other.id)}>{other.label}</button>
            <span className="text-xs text-muted"> · {nodeStyleOf(other.kind).label}</span>
          </li>)}
        </ul>
        {total > neighbours.length && <p className="mt-2 text-xs text-muted">and {total - neighbours.length} more</p>}
      </>}
      <p className="mt-4 border-t border-line pt-4 text-xs leading-5 text-muted">99 pages and 282 links of the real wiki of Moubri Toolkit, a French rental-management product, around its LMNP simulation. Hover a page to bring out its links; select one to follow them.</p>
    </aside>
  </div>
}

/** The marketing sections shown under the sign-in form: benchmark, wiki, search, indexing, audiences. */
export function Landing() {
  return <div className="landing">
    <section className="mx-auto w-full max-w-6xl px-6 pb-16 sm:px-10 lg:px-16" aria-label="AgentRAG at work">
      <Reveal>
        <figure className="hero-shot overflow-hidden rounded-panel border border-line bg-surface shadow-[0_24px_60px_-32px_rgb(37_39_36/0.35)]">
          <img src="/images/application-preview.png" width={2555} height={1270} alt="AgentRAG explore view: search results for “construction” with exact and semantic matches beside a map of passages coloured by document" className="w-full" />
        </figure>
      </Reveal>
      <dl className="mt-10 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
        {highlights.map((item, index) => <Reveal key={item.label} delay={index * 90} className="border-t border-line pt-4">
          <dt className="font-display text-4xl tracking-tight text-accent tabular-nums"><CountUp value={item.value} prefix={item.prefix} suffix={item.suffix} /></dt>
          <dd className="mt-2 text-sm font-medium">{item.label}</dd>
          <dd className="mt-1 text-xs leading-5 text-muted">{item.detail}</dd>
        </Reveal>)}
      </dl>
      <p className="mt-4 text-xs text-muted">On our benchmark of a real code question; token counts are estimates.</p>
    </section>

    <section className="border-y border-line bg-surface" aria-labelledby="benchmark-title">
      <div className="mx-auto grid w-full max-w-6xl gap-10 px-6 py-16 sm:px-10 lg:px-16">
        <Reveal className="max-w-3xl">
          <p className="eyebrow mb-3">The benchmark</p>
          <h2 id="benchmark-title" className="font-display text-3xl tracking-tight sm:text-4xl">Every fact of the answer, for the fewest tokens per fact.</h2>
          <p className="mt-4 text-sm leading-6 text-muted">One question on a real repository of 309 files: “How does Moubri Toolkit compute its LMNP rental simulations?” A complete answer holds 9 facts, from the depreciation split to the 18.6% social contributions.</p>
          <p className="mt-4 text-sm leading-6 text-muted">Reading more is not answering better. Keyword search found all 9 facts after 365 noise passages; the wiki or semantic search alone read less and missed most of them. AgentRAG found all 9, each with its file and line, for about <strong className="font-semibold text-ink">1.4k tokens per fact</strong>: 35% less than keyword search.</p>
          <p className="mt-4 text-xs leading-5 text-muted">Facts checked against the source code; token counts are estimates from the size of each tool answer.</p>
        </Reveal>
        <Reveal delay={120}><BenchmarkComparison /></Reveal>
      </div>
    </section>

    <section className="mx-auto w-full max-w-6xl px-6 py-16 sm:px-10 lg:px-16" aria-labelledby="wiki-title">
      <div className="grid gap-10">
        <Reveal>
          <p className="eyebrow mb-3">The LLM wiki</p>
          <h2 id="wiki-title" className="font-display text-3xl tracking-tight sm:text-4xl">The wiki says where to look. The sources say what is true.</h2>
          <ul className="mt-5 space-y-3 text-sm leading-6">
            <li className="flex gap-2"><span aria-hidden className="mt-2 size-2 shrink-0 rounded-full bg-chart-terracotta" />Pages for features, business rules, concepts, code modules and APIs, written from your documents and code.</li>
            <li className="flex gap-2"><span aria-hidden className="mt-2 size-2 shrink-0 rounded-[2px] bg-chart-teal" />Every rule links to the code that implements it, and every claim cites a passage ready to open.</li>
            <li className="flex gap-2"><span aria-hidden className="mt-2 size-2 shrink-0 rounded-full bg-chart-ochre" />Key values of the code (constants, thresholds, formulas) with their line.</li>
            <li className="flex gap-2"><span aria-hidden className="mt-2 size-2 shrink-0 rounded-full bg-ink" />Pages are flagged stale as soon as the code they describe changes.</li>
          </ul>
        </Reveal>
        <Reveal delay={120}><SampleWikiGraph /></Reveal>
      </div>
    </section>

    <section className="border-y border-line bg-surface" aria-labelledby="search-title">
      <div className="mx-auto w-full max-w-6xl px-6 py-16 sm:px-10 lg:px-16">
        <Reveal>
          <p className="eyebrow mb-3">Every search in its place</p>
          <h2 id="search-title" className="font-display text-3xl tracking-tight sm:text-4xl">From compiled context down to the exact line.</h2>
          <p className="mt-4 max-w-2xl text-sm leading-6 text-muted">AgentRAG picks the search that fits the question and stops as soon as the sources it has read answer.</p>
        </Reveal>
        <ol className="search-ladder mt-10 grid gap-4 lg:grid-cols-5">
          {ladder.map((step, index) => <li key={step.title}>
            <Reveal delay={index * 110} className="h-full rounded-panel border border-line bg-canvas p-5">
              <step.icon size={22} className="text-accent" aria-hidden />
              <h3 className="mt-3 font-semibold">{step.title}</h3>
              <p className="mt-2 text-sm leading-6 text-muted">{step.text}</p>
            </Reveal>
          </li>)}
        </ol>
        <div className="mt-8 grid gap-5 sm:grid-cols-2">
          <Reveal className="overflow-hidden rounded-panel border border-line bg-canvas">
            <img src="/images/preview-search.png" alt="Search results showing an exact phrase match and a semantic match" className="w-full object-contain p-4" loading="lazy" />
          </Reveal>
          <Reveal delay={120} className="overflow-hidden rounded-panel border border-line bg-surface">
            <img src="/images/preview-map.png" alt="Map of document passages, coloured by document" className="max-h-64 w-full object-contain p-4" loading="lazy" />
          </Reveal>
        </div>
      </div>
    </section>

    <section className="mx-auto w-full max-w-6xl px-6 py-16 sm:px-10 lg:px-16" aria-labelledby="indexing-title">
      <Reveal>
        <p className="eyebrow mb-3">Document indexing</p>
        <h2 id="indexing-title" className="font-display text-3xl tracking-tight sm:text-4xl">From a file to a searchable source, in four steps.</h2>
      </Reveal>
      <ol className="indexing-steps mt-10 grid gap-8 sm:grid-cols-2 lg:grid-cols-4">
        {indexing.map((step, index) => <li key={step.title}>
          <Reveal delay={index * 120}>
            <span className="flex size-11 items-center justify-center rounded-full border border-line bg-surface text-accent"><step.icon size={22} aria-hidden /></span>
            <p className="mt-4 text-xs font-semibold tracking-widest text-accent">0{index + 1}</p>
            <h3 className="mt-1 text-lg font-semibold">{step.title}</h3>
            <p className="mt-2 text-sm leading-6 text-muted">{step.text}</p>
          </Reveal>
        </li>)}
      </ol>
      <Reveal delay={100} className="mt-10 grid items-center gap-6 rounded-panel border border-line bg-canvas p-5 sm:p-8 lg:grid-cols-2">
        <img src="/images/preview-indexing.png" alt="Document indexing progress with passages prepared and time remaining" className="w-full object-contain" loading="lazy" />
        <div>
          <h3 className="text-lg font-semibold">Follow every document as it is prepared</h3>
          <p className="mt-2 text-sm leading-6 text-muted">See passages prepared, the pace and the time left. When the embedding service asks to slow down, completed passages are kept and the wait is shown.</p>
          <p className="mt-3 flex items-center gap-2 text-sm"><GitBranchIcon size={18} className="text-accent" aria-hidden />Repositories re-sync incrementally: only changed files are fetched again.</p>
        </div>
      </Reveal>
    </section>

    <section className="border-y border-line bg-surface" aria-labelledby="audience-title">
      <div className="mx-auto w-full max-w-6xl px-6 py-16 sm:px-10 lg:px-16">
        <Reveal>
          <p className="eyebrow mb-3">Who it is for</p>
          <h2 id="audience-title" className="font-display text-3xl tracking-tight sm:text-4xl">One library, every way of asking.</h2>
        </Reveal>
        <div className="mt-8 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
          {audiences.map((item, index) => <Reveal key={item.title} delay={index * 90} className="border-t border-line pt-4">
            <h3 className="font-semibold">{item.title}</h3>
            <p className="mt-2 text-sm leading-6 text-muted">{item.text}</p>
          </Reveal>)}
        </div>
        <Reveal delay={120} className="mt-10 flex flex-col gap-5 rounded-panel border border-line bg-canvas p-6 sm:flex-row sm:items-center sm:justify-between sm:p-8">
          <div className="flex gap-4">
            <PlugsConnectedIcon size={28} className="shrink-0 text-accent" aria-hidden />
            <div>
              <h3 className="text-lg font-semibold">Connect your AI assistant</h3>
              <p className="mt-1 text-sm leading-6 text-muted">Claude and Codex sign in with OAuth and search your library through MCP, with read-only access to what you can read.</p>
            </div>
          </div>
          <a href="#auth-title" className="inline-flex min-h-11 shrink-0 items-center justify-center gap-2 rounded-control bg-accent px-5 text-sm font-medium text-white hover:bg-accent/90">Get started<ArrowRightIcon size={18} aria-hidden /></a>
        </Reveal>
      </div>
    </section>
  </div>
}

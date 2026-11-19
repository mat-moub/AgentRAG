import { useState } from 'react'
import type { ReactNode } from 'react'
import {
  ArrowDownIcon, ArrowRightIcon, ArrowsClockwiseIcon, BookOpenTextIcon, BrainIcon, CheckCircleIcon, ClockIcon, CodeIcon,
  FileTextIcon, GitBranchIcon, GraphIcon, MagnifyingGlassIcon, PlugsConnectedIcon, XCircleIcon,
} from '@phosphor-icons/react'
import { Button, Field, Notice } from '../../components/ui'
import type * as api from '../../api'

type Shape = 'feature' | 'rule' | 'concept' | 'module' | 'api' | 'data-entity'

/** The same shapes and colours as the wiki graph legend: warm and round for documentation, cool and angular for code. */
function KindMark({ shape }: { shape: Shape }) {
  const warm = 'var(--color-chart-terracotta)'
  const cool = 'var(--color-chart-teal)'
  const marks: Record<Shape, ReactNode> = {
    feature: <circle cx="8" cy="8" r="6" fill={warm} />,
    rule: <circle cx="8" cy="8" r="5" fill="none" stroke={warm} strokeWidth="2.5" />,
    concept: <circle cx="8" cy="8" r="4.5" fill="var(--color-chart-ochre)" />,
    module: <rect x="2" y="2" width="12" height="12" rx="3" fill={cool} />,
    api: <polygon points="8,1.5 13.6,4.75 13.6,11.25 8,14.5 2.4,11.25 2.4,4.75" fill="var(--color-chart-deep-teal)" />,
    'data-entity': <rect x="3" y="3" width="10" height="10" fill="none" stroke={cool} strokeWidth="2.5" />,
  }
  return <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden className="shrink-0">{marks[shape]}</svg>
}

function Section({ title, intro, children }: { title: string; intro?: string; children: ReactNode }) {
  return <section className="space-y-4">
    <div><h3 className="text-lg font-semibold tracking-tight">{title}</h3>{intro && <p className="mt-1 text-sm leading-6 text-muted">{intro}</p>}</div>
    {children}
  </section>
}

/** A diagram: the content carries the meaning as text; the caption says what to read in it. */
function Figure({ label, caption, children }: { label: string; caption: string; children: ReactNode }) {
  return <figure aria-label={label} className="rounded-panel border border-line bg-surface p-4 sm:p-6">
    {children}
    <figcaption className="mt-4 text-xs leading-5 text-muted">{caption}</figcaption>
  </figure>
}

const stages = [
  { icon: <FileTextIcon size={22} aria-hidden />, title: 'Your sources', text: 'Documents and connected Git repositories of this folder and its subfolders.' },
  { icon: <BrainIcon size={22} aria-hidden />, title: 'Read', text: 'A model reads each source and notes features, rules, concepts and code modules, with citations.' },
  { icon: <BookOpenTextIcon size={22} aria-hidden />, title: 'Write', text: 'One page per subject, duplicates merged, every statement linked to its passage or line of code.' },
  { icon: <GraphIcon size={22} aria-hidden />, title: 'Link', text: 'Typed links between pages, and "implements" links from the code to the features it serves.' },
]

function BuildDiagram() {
  return <Figure label="How a wiki is built" caption="The build runs in the background. A refresh only reads again the sources that changed since the last one.">
    <ol className="flex flex-col items-stretch gap-2 lg:flex-row lg:items-center">
      {stages.map((stage, index) => <li key={stage.title} className="flex flex-col items-center gap-2 lg:flex-1 lg:flex-row">
        <div className="w-full rounded-control border border-line bg-canvas p-4 lg:min-h-44">
          <div className="flex items-center gap-2 text-primary">{stage.icon}<span className="text-xs font-semibold tracking-wide uppercase">Step {index + 1}</span></div>
          <p className="mt-2 font-semibold">{stage.title}</p>
          <p className="mt-1 text-sm leading-6 text-muted">{stage.text}</p>
        </div>
        {index < stages.length - 1 && <><ArrowDownIcon size={20} aria-hidden className="shrink-0 text-muted lg:hidden" /><ArrowRightIcon size={20} aria-hidden className="hidden shrink-0 text-muted lg:block" /></>}
      </li>)}
    </ol>
  </Figure>
}

const traces: { doc: [Shape, string, string]; code: [Shape, string, string] }[] = [
  { doc: ['feature', 'Feature', 'Order checkout'], code: ['module', 'Module', 'src/Orders'] },
  { doc: ['rule', 'Business rule', 'Refunds within 30 days'], code: ['api', 'API', 'POST /api/refunds'] },
  { doc: ['concept', 'Concept', 'Customer account'], code: ['data-entity', 'Data entity', 'Customer table'] },
]

function TraceDiagram() {
  const card = ([shape, kind, title]: [Shape, string, string]) => <div className="min-w-0 rounded-control border border-line bg-canvas p-3">
    <p className="flex items-center gap-2 text-xs text-muted"><KindMark shape={shape} />{kind}</p>
    <p className="mt-1 text-sm font-semibold break-words">{title}</p>
  </div>
  return <Figure label="Documentation linked to the code that implements it" caption="Example of a business application: the wiki answers both “where is this feature implemented?” and “what is this code for?”. Shapes and colours match the wiki graph.">
    <div className="grid grid-cols-[1fr_auto_1fr] items-center gap-x-2 gap-y-3 sm:gap-x-4">
      <p className="text-xs font-semibold tracking-wide text-muted uppercase">What the product does</p><span />
      <p className="text-xs font-semibold tracking-wide text-muted uppercase">How the code does it</p>
      {traces.map(trace => <div key={trace.doc[2]} className="contents">
        {card(trace.doc)}
        <div className="flex flex-col items-center text-ink"><span className="text-[11px] text-muted">implements</span><svg width="44" height="12" viewBox="0 0 44 12" aria-hidden><path d="M42 6H4M10 1 4 6l6 5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" /></svg></div>
        {card(trace.code)}
      </div>)}
    </div>
  </Figure>
}

function DecisionDiagram() {
  const question = (text: string) => <div className="rounded-control border border-line bg-canvas p-3 text-sm font-semibold leading-6">{text}</div>
  const answer = (yes: boolean, text: string) => <p className={`flex items-start gap-2 text-sm leading-6 ${yes ? 'text-ink' : 'text-muted'}`}>
    {yes ? <CheckCircleIcon size={20} aria-hidden className="mt-0.5 shrink-0 text-primary" /> : <XCircleIcon size={20} aria-hidden className="mt-0.5 shrink-0" />}<span><span className="font-semibold">{yes ? 'Yes' : 'No'}</span> — {text}</span>
  </p>
  return <Figure label="Is a wiki right for this folder?" caption="When the answer is no, Conversation and Explore documents already search every passage of the folder, without building anything.">
    <div className="space-y-3">
      {question('1. Does the folder hold several documents, or a code repository, about one product, process or domain?')}
      <div className="space-y-1 border-l-2 border-line pl-4">{answer(false, 'ask questions in Conversation or search in Explore documents.')}{answer(true, 'go on to question 2.')}</div>
      {question('2. Will people or AI agents need to understand how things fit together: features, rules, concepts, the code behind them?')}
      <div className="space-y-1 border-l-2 border-line pl-4">{answer(false, 'searching the passages is enough.')}{answer(true, 'create a wiki for this folder.')}</div>
    </div>
  </Figure>
}

const examples = [
  { icon: <CodeIcon size={20} aria-hidden />, title: 'Business application', text: 'Functional specifications and the application repository. Each feature and rule is traced to the module or API that implements it.' },
  { icon: <PlugsConnectedIcon size={20} aria-hidden />, title: 'API or platform', text: 'API reference, integration guides and the SDK repository, organised by resource, concept and endpoint.' },
  { icon: <FileTextIcon size={20} aria-hidden />, title: 'Regulations and procedures', text: 'Policies, procedures and legal texts. Rules and definitions get their own pages, each citing the article it comes from.' },
  { icon: <BookOpenTextIcon size={20} aria-hidden />, title: 'Team onboarding', text: 'Architecture decisions, runbooks and how-to guides, so a newcomer starts from an overview instead of a list of files.' },
  { icon: <GitBranchIcon size={20} aria-hidden />, title: 'Legacy system', text: 'Old documentation next to the code, to rediscover what the system does and which parts still match the documents.' },
  { icon: <MagnifyingGlassIcon size={20} aria-hidden />, title: 'Research topic', text: 'Papers, reports and notes on one subject, with concepts linked across sources.' },
]

const notWorthIt = [
  ['A single document or a few short notes', 'read it, or ask about it in Conversation.'],
  ['Unrelated files gathered in one folder', 'pages would not connect. Split the folder by subject and give each one its own wiki.'],
  ['Mostly tables and figures (CSV exports, reports of numbers)', 'a wiki describes subjects, not rows. Search the passages instead.'],
  ['Content that changes many times a day (logs, chat exports)', 'every refresh reads the changed sources again with the model, which costs time and tokens.'],
  ['When you need the exact wording', 'pages are summaries written by a model. Quote the sources, which every page cites.'],
  ['Scanned PDFs without a text layer', 'there is nothing to read. Run OCR before adding them.'],
]

const steps = [
  { icon: <FileTextIcon size={20} aria-hidden />, title: 'Gather the sources', text: 'Add documents to this folder or connect a Git repository. Subfolders are included, except those shared with a different audience.' },
  { icon: <BookOpenTextIcon size={20} aria-hidden />, title: 'Create the wiki', text: 'Choose the language of the pages, or leave it empty to follow the sources. The first build starts right away.' },
  { icon: <ClockIcon size={20} aria-hidden />, title: 'Let it build', text: 'It runs on the server: you can close the page. Count from a few minutes to an hour for large repositories.' },
  { icon: <ArrowsClockwiseIcon size={20} aria-hidden />, title: 'Keep it current', text: 'Refresh after the sources change: only what changed is read again. Check links finds pages that lost their target.' },
]

/**
 * Shown while a folder has no wiki, or has one that was never built: explains what an LLM wiki is, when it is worth
 * creating, and offers to create or build it.
 */
export function WikiIntro({ folder, wiki, busy, error, onCreate, onBuild }: {
  folder: api.Folder; wiki: api.Wiki | null; busy: boolean; error: string
  onCreate: (language: string) => void; onBuild: () => void
}) {
  const [language, setLanguage] = useState('')
  const canAct = wiki ? wiki.canRefresh : folder.canWrite
  const action = !canAct
    ? <p className="text-sm leading-6 text-muted">{wiki ? 'This wiki has not been built yet.' : 'This folder has no wiki yet.'} Someone with write access to the folder can {wiki ? 'build' : 'create'} it.</p>
    : wiki
      ? <Button busy={busy} onClick={onBuild}><ArrowsClockwiseIcon size={18} aria-hidden />Build the wiki</Button>
      : <form className="space-y-4" onSubmit={event => { event.preventDefault(); onCreate(language) }}>
        <Field label="Language of the pages" hint="Leave empty to follow the language of the sources." maxLength={50} value={language} onChange={event => setLanguage(event.target.value)} disabled={busy} />
        <Button type="submit" busy={busy}><BookOpenTextIcon size={18} aria-hidden />Create and build the wiki</Button>
      </form>

  return <div className="min-h-0 flex-1 animate-enter overflow-y-auto">
    <div className="mx-auto w-full max-w-5xl space-y-12 px-5 py-6 sm:px-10 sm:py-10">
      <header className="grid gap-8 lg:grid-cols-[1fr_22rem] lg:items-start">
        <div className="space-y-4">
          <p className="text-xs font-semibold tracking-wide text-primary uppercase">LLM wiki</p>
          <h2 className="font-display text-3xl leading-tight sm:text-4xl">Turn {folder.name} into a living wiki</h2>
          <p className="text-base leading-7 text-muted">A language model reads the documents and code of this folder and writes interlinked pages: an overview, then one page per feature, business rule, concept, code module and API. Every page cites its sources, and links tie the documentation to the code that implements it.</p>
          <ul className="space-y-2 text-sm leading-6">
            <li className="flex gap-2"><CheckCircleIcon size={20} aria-hidden className="mt-0.5 shrink-0 text-primary" />Start from an overview instead of searching file by file.</li>
            <li className="flex gap-2"><CheckCircleIcon size={20} aria-hidden className="mt-0.5 shrink-0 text-primary" />Go from a feature to its code, and from a file to the features it serves.</li>
            <li className="flex gap-2"><CheckCircleIcon size={20} aria-hidden className="mt-0.5 shrink-0 text-primary" />AI agents connected through MCP (Claude, Codex) read the wiki first, then check the sources.</li>
          </ul>
        </div>
        <div className="space-y-4 rounded-panel border border-line bg-surface p-5">
          <p className="font-semibold">{wiki ? 'Your wiki is ready to be built' : 'Create a wiki for this folder'}</p>
          {wiki && canAct && <p className="text-sm leading-6 text-muted">The wiki of this folder has no pages yet. Build it to turn the documents and repositories into an overview and linked pages; it runs in the background.</p>}
          {error && <Notice>{error}</Notice>}
          {action}
        </div>
      </header>

      <Section title="How it works" intro="Four stages turn the sources into a connected set of pages.">
        <BuildDiagram />
        <TraceDiagram />
      </Section>

      <Section title="How to create your wiki">
        <ol className="grid gap-3 sm:grid-cols-2">
          {steps.map((step, index) => <li key={step.title} className="flex gap-3 rounded-panel border border-line p-4">
            <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-subtle text-sm font-semibold tabular-nums">{index + 1}</span>
            <div><p className="flex items-center gap-2 font-semibold"><span className="text-primary">{step.icon}</span>{step.title}</p><p className="mt-1 text-sm leading-6 text-muted">{step.text}</p></div>
          </li>)}
        </ol>
      </Section>

      <Section title="Is a wiki right for this folder?" intro="A wiki pays off when the sources describe one subject from several angles.">
        <DecisionDiagram />
      </Section>

      <Section title="Projects that benefit from a wiki">
        <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {examples.map(example => <li key={example.title} className="rounded-panel border border-line p-4">
            <p className="flex items-center gap-2 font-semibold"><span className="text-primary">{example.icon}</span>{example.title}</p>
            <p className="mt-1 text-sm leading-6 text-muted">{example.text}</p>
          </li>)}
        </ul>
      </Section>

      <Section title="When a wiki is not worth it" intro="Building reads every source with a model, which takes time and model tokens. Skip it in these cases:">
        <ul className="divide-y divide-line border-y border-line">
          {notWorthIt.map(([title, text]) => <li key={title} className="flex gap-3 py-3 text-sm leading-6">
            <XCircleIcon size={20} aria-hidden className="mt-0.5 shrink-0 text-muted" /><p><span className="font-semibold">{title}</span>: {text}</p>
          </li>)}
        </ul>
      </Section>
    </div>
  </div>
}

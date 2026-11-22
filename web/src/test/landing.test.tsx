import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { publicPageUrl } from '../api'
import { Login } from '../features/auth/Login'
import { sampleNeighbours, sampleWiki } from '../features/auth/sampleWiki'

describe('Landing page', () => {
  it('keeps the sign-in form first and presents the benchmark, wiki, search and indexing in English', () => {
    render(<Login onDone={vi.fn()} />)

    // The sign-in form stays where it was, above the marketing sections.
    expect(screen.getByRole('heading', { level: 1, name: /The right answer\.\s*With its source\./ })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Welcome back' })).toBeInTheDocument()
    expect(screen.getByLabelText('Email address')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument()

    // The wiki comes right after the benchmark, where indexing used to be.
    const titles = screen.getAllByRole('heading', { level: 2 }).map(heading => heading.textContent)
    expect(titles.slice(-5)).toEqual([
      'Every fact of the answer, for the fewest tokens per fact.', 'The wiki says where to look. The sources say what is true.',
      'From compiled context down to the exact line.', 'From a file to a searchable source, in four steps.', 'One library, every way of asking.',
    ])
    expect(document.querySelectorAll('.reveal:not(.reveal-in)')).toHaveLength(0)
    expect(screen.getByRole('img', { name: /AgentRAG explore view/ })).toHaveAttribute('src', '/images/application-preview.png')
    // Get started leads to the public guide that explains how to connect an AI assistant through MCP.
    const getStarted = screen.getByRole('link', { name: /^Get started\s*\(opens in a new tab\)$/ })
    expect(getStarted).toHaveAttribute('href', publicPageUrl('/connect'))
    expect(getStarted).toHaveAttribute('rel', 'noopener noreferrer')
    // The hero call to action leads to the same guide.
    expect(screen.getByRole('link', { name: /^Connect with your AI\s*\(opens in a new tab\)$/ })).toHaveAttribute('href', publicPageUrl('/connect'))
  })

  it('shows precision and cost on one scale: the cheaper approaches miss most of the answer', () => {
    render(<Login onDone={vi.fn()} />)

    const rows = within(screen.getByRole('list', { name: 'Benchmark: facts found and tokens read, per approach' })).getAllByRole('listitem')
    const summaries = rows.map(row => row.querySelector('.sr-only')!.textContent)

    expect(summaries).toEqual([
      'AgentRAG: 9 of 9 facts found, 13000 tokens read, about 1444 tokens per fact found, 0 noise passages.',
      'Keyword search: 9 of 9 facts found, 20000 tokens read, about 2222 tokens per fact found, 365 noise passages.',
      'Wiki alone: 3 of 9 facts found (incomplete answer), 7000 tokens read, about 2333 tokens per fact found, 0 noise passages.',
      'Semantic search: 1 of 9 facts found (incomplete answer), 5500 tokens read, about 5500 tokens per fact found, 0 noise passages.',
    ])
    expect(screen.getByText('Incomplete: 8 facts missing')).toBeInTheDocument()
    expect(within(screen.getByRole('table')).getAllByRole('row')).toHaveLength(10)
  })

  it('lists the pages linked to the selected page of the real wiki, and follows them', async () => {
    render(<Login onDone={vi.fn()} />)

    // Without a canvas-ready browser (jsdom) the picture of a real wiki stands in for the live graph.
    expect(screen.getByRole('img', { name: /Graph of an LLM wiki/ })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 3, name: 'Simulation LMNP' })).toBeInTheDocument()
    const first = sampleNeighbours('simulation-lmnp').find(link => link.kind === 'implements')!
    await userEvent.click(screen.getAllByRole('button', { name: first.page.label })[0])

    expect(screen.getByRole('heading', { level: 3, name: first.page.label })).toBeInTheDocument()
  })

  it('draws a real wiki whose links all join its pages, with documentation and code linked', () => {
    const ids = new Set(sampleWiki.nodes.map(node => node.id))

    expect(sampleWiki.nodes).toHaveLength(99)
    expect(sampleWiki.edges).toHaveLength(282)
    expect(sampleWiki.edges.every(edge => ids.has(edge.source) && ids.has(edge.target))).toBe(true)
    expect(sampleWiki.edges.filter(edge => edge.kind === 'implements')).toHaveLength(76)
    expect(sampleWiki.nodes.every(node => node.degree > 0)).toBe(true)
  })
})
